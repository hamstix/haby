using Haby.Application;

namespace Haby.Persistence.PostgreSql;

internal sealed partial class PostgreSqlOperatorRolloutObservation
{
    public async ValueTask<BatchObservationResult> WaitBatchAsync(string request, string actor, IReadOnlyList<BatchObservationMember> members,
        BatchObservationSettings? settings, GenerationObservationSettings? memberSettings, DateTimeOffset? deadline, CancellationToken token)
    {
        var started = clock.GetTimestamp();
        _ = OperationId.Create(request); PostgreSqlDurableDeploymentPlanner.ValidateActor(actor);
        var options = (batchLimits ?? new BatchObservationOptions()).Narrow(settings);
        _ = ObservationSettings.Narrow(limits, memberSettings);
        if (members is null || members.Count < 1 || members.Count > options.MaximumMembers ||
            members.Any(x => x is null || x.Deployment is null || x.Generation is null) ||
            members.Select(x => (x.Deployment, x.Generation)).Distinct().Count() != members.Count)
            throw new ArgumentException("Invalid batch members.");
        var input = members.ToArray();
        foreach (var member in input) _ = OperationId.Create(member.RequestId);
        var duration = TimeSpan.FromMilliseconds(options.RequestMilliseconds);
        if (deadline is { } end && end - clock.GetUtcNow() < duration) duration = end - clock.GetUtcNow();
        var reads = new BatchReadBudget(options, clock, started, duration);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remaining = reads.Remaining;
        if (remaining <= TimeSpan.Zero) budget.Cancel(); else budget.CancelAfter(remaining);
        var results = input.Select(x => new BatchObservationSummary(x, GenerationReadinessOutcome.NotStartedBudget)).ToArray();
        var next = -1;
        var workers = new Task[Math.Min(options.Concurrency, input.Length)];
        for (var i = 0; i < workers.Length; i++) workers[i] = Worker();
        await Task.WhenAll(workers);
        var canceled = token.IsCancellationRequested;
        var expired = reads.Remaining <= TimeSpan.Zero || budget.IsCancellationRequested && !canceled;
        return new(request, !canceled && !expired && results.All(x => x.CurrentReady && !x.Replayed), results, canceled, expired, !canceled && !expired && results.All(x => x.CurrentApplied && !x.Replayed));

        async Task Worker()
        {
            while (!budget.IsCancellationRequested && reads.CanStart)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= input.Length) return;
                var member = input[index]; var state = new BatchMemberObservation(reads);
                GenerationReadinessResult result;
                try
                {
                    result = await RunAsync(member.Deployment, member.Generation, member.RequestId, actor, memberSettings,
                        null, deadline, budget.Token, state);
                }
                catch (Exception) { result = new(GenerationReadinessOutcome.Unknown, member.RequestId); }
                var outcome = result.Outcome;
                if (!state.Started && outcome is GenerationReadinessOutcome.Deadline or GenerationReadinessOutcome.Canceled)
                    outcome = GenerationReadinessOutcome.NotStartedBudget;
                if (state.Started && result.Stop == "CallLimit" && !reads.CanStart) outcome = GenerationReadinessOutcome.BudgetExhausted;
                if (!result.Replayed && result.Members?.Any(x => x.Outcome == "Refused") == true)
                {
                    results[index] = new(member, GenerationReadinessOutcome.Refused); continue;
                }
                if (!result.Replayed && result.Members?.Any(x => x.Outcome == "Unavailable") == true) outcome = GenerationReadinessOutcome.Unavailable;
                if (!result.Replayed && result.Members?.Any(x => x.Outcome == "Conflict") == true) outcome = GenerationReadinessOutcome.Conflict;
                if (outcome == GenerationReadinessOutcome.Conflict) { results[index] = new(member, outcome); continue; }
                results[index] = new(member, outcome, result.CurrentReady, result.OriginalRequestReady,
                    result.Replayed, result.Submitted, result.SavedReady, result.Stop, result.CurrentApplied, result.OriginalRequestApplied, result.Retirement, result.AppliedGeneration, result.CompletionReason);
            }
        }
    }
}
