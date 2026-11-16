using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed class RolloutRequestConflictException : Exception;
internal sealed class RolloutPermissionException : Exception;

internal sealed partial class PostgreSqlOperatorRolloutObservation(IDbContextFactory<ManifestCatalogDbContext> contexts,
    InternalWorkloadConfiguration configuration, InternalDurableActions actions, RolloutOptions limits,
    TimeProvider clock, string deploymentAction, string identityAction, BatchObservationOptions? batchLimits = null) : IOperatorRolloutObservation
{
    public ValueTask<GenerationReadinessResult> WaitAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation,
        string request, string actor, GenerationObservationSettings? settings, DateTimeOffset? deadline, CancellationToken token) =>
        RunAsync(deployment, generation, request, actor, settings, null, deadline, token);

    public ValueTask<GenerationReadinessResult> ResumeAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation,
        string request, string actor, IReadOnlyList<GenerationObservationMember> members, DateTimeOffset? deadline, CancellationToken token) =>
        members is null ? ValueTask.FromResult(new GenerationReadinessResult(GenerationReadinessOutcome.InvalidRequest, request)) :
        RunAsync(deployment, generation, request, actor, null, members, deadline, token);

    private async ValueTask<GenerationReadinessResult> RunAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation,
        string request, string actor, GenerationObservationSettings? settings, IReadOnlyList<GenerationObservationMember>? members,
        DateTimeOffset? deadline, CancellationToken token, BatchMemberObservation? batch = null)
    {
        var started = batch?.Budget.Started ?? clock.GetTimestamp();
        RolloutOptions options;
        RolloutResume[]? resumes;
        try
        {
            _ = OperationId.Create(request); PostgreSqlDurableDeploymentPlanner.ValidateActor(actor);
            options = ObservationSettings.Narrow(limits, settings);
            if (members is { Count: < 1 or > 512 }) throw new ArgumentException("Invalid observation member count.");
            resumes = members?.Select(x => new RolloutResume(x.Subject, x.ExpectedAttempt)).ToArray();
            if (resumes is not null && (resumes.Length is < 1 or > 512 || resumes.Any(x => x.ExpectedAttempt == Guid.Empty ||
                !ValidSubject(x.Subject)) || resumes.Select(x => x.Subject).Distinct(StringComparer.Ordinal).Count() != resumes.Length))
                throw new ArgumentException("Invalid observation members.");
            if (resumes is not null && System.Text.Encoding.UTF8.GetByteCount(RolloutJson.Encode(new RolloutRequest("Resume", actor, null, resumes))) > 131072)
                throw new ArgumentException("Observation request too large.");
        }
        catch (ArgumentException) { return new(GenerationReadinessOutcome.InvalidRequest, request); }
        var duration = TimeSpan.FromMilliseconds(options.RequestMilliseconds);
        if (batch is not null && batch.Budget.Duration < duration) duration = batch.Budget.Duration;
        // Batch transport time was capped once at entry; recalculating it here would charge queued time twice.
        if (batch is null && deadline is { } end && end - clock.GetUtcNow() < duration) duration = end - clock.GetUtcNow();
        var remaining = duration - clock.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) return new(GenerationReadinessOutcome.Deadline, request);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        // The batch owns its one total timer. Only a genuinely shorter stable member limit needs another timer.
        if (batch is null || duration < batch.Budget.Duration) budget.CancelAfter(remaining);
        try
        {
            budget.Token.ThrowIfCancellationRequested();
            var inspected = await WorkloadGenerationAuthority.InspectAsync(contexts, configuration, actions, deploymentAction,
                identityAction, deployment, generation, budget.Token, completedRead: true);
            if (inspected.Reason is not null) return new(inspected.Reason == "generation-not-found" ? GenerationReadinessOutcome.NotFound :
                GenerationReadinessOutcome.UnsupportedShape, request, CompletionReason: inspected.Reason == "unsupported-inventory" ? "unsupported-inventory" : "not-ready");
            if (clock.GetElapsedTime(started) >= duration) return new(GenerationReadinessOutcome.Deadline, request);
            var result = resumes is null
                ? await actions.ObserveOperatorRolloutAsync(inspected.Accepted!, request, actor, options, started, duration, token, budget.Token, batch)
                : await actions.ResumeOperatorRolloutAsync(inspected.Accepted!, request, actor, resumes, started, duration, budget.Token);
            return new(resumes is null ? GenerationReadinessOutcome.Observed : GenerationReadinessOutcome.Resumed, request,
                result.Ready && !result.Replayed, result.Ready, result.Replayed, result.Submitted, result.SavedReady,
                result.Stop.ToString(), result.Members.Select(x => new GenerationReadinessMember(x.Subject, x.Attempt, x.Deadline,
                    x.Budget.ToString(), x.Observation.Outcome.ToString(), x.Observation.Code, x.Observation.Temporary)).ToArray(), result.Applied && !result.Replayed && result.Ready, result.Applied,
                result.Retirement, result.AppliedGeneration, result.CompletionReason);
        }
        catch (RolloutRequestConflictException) { return new(GenerationReadinessOutcome.Conflict, request); }
        catch (RolloutPermissionException) { return new(GenerationReadinessOutcome.Refused, request); }
        catch (RolloutBatchExhaustedException) { return new(batch?.Started == true ? GenerationReadinessOutcome.BudgetExhausted : GenerationReadinessOutcome.NotStartedBudget, request); }
        catch (OperationCanceledException)
        {
            return new(batch is not null ? batch.Started ? GenerationReadinessOutcome.Interrupted : GenerationReadinessOutcome.NotStartedBudget :
                token.IsCancellationRequested ? GenerationReadinessOutcome.Canceled : GenerationReadinessOutcome.Deadline, request);
        }
        catch (ArgumentException) { return new(GenerationReadinessOutcome.InvalidRequest, request); }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or OverflowException)
        { return new(GenerationReadinessOutcome.Unknown, request, CompletionReason: "prerequisite-incomplete"); }
        catch (Exception error) when (error is Npgsql.NpgsqlException or DbUpdateException or IOException)
        { return new(GenerationReadinessOutcome.Unavailable, request, CompletionReason: "commit-unconfirmed"); }
    }
    private static bool ValidSubject(string subject)
    {
        var parts = subject.Split('/');
        if (parts is not ["Workload", var component, var workload]) return false;
        try { _ = ManifestLocalId.Create(component); _ = ManifestLocalId.Create(workload); return true; }
        catch (ArgumentException) { return false; }
    }
}
