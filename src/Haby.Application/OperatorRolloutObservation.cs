using Haby.Domain;

namespace Haby.Application;

/// <summary>Caller settings may narrow configured Host limits. Polling may only become less frequent.</summary>
public sealed record GenerationObservationSettings(int? MaximumCalls = null, int? MaximumRounds = null,
    int? ReadTimeoutMilliseconds = null, int? PollMilliseconds = null, int? RequestMilliseconds = null);
public sealed record GenerationObservationMember(string Subject, Guid ExpectedAttempt);
public enum GenerationReadinessOutcome { Observed, Resumed, InvalidRequest, NotFound, UnsupportedShape, Unknown, Conflict, Refused, Canceled, Deadline, Unavailable, NotStartedBudget, Interrupted, BudgetExhausted }
public sealed record GenerationReadinessMember(string Subject, Guid? Attempt, DateTimeOffset? Deadline,
    string Budget, string Outcome, string Code, bool Temporary);
/// <summary>OriginalRequestReady and SavedReady are historical; CurrentReady requires a fresh complete request.</summary>
public sealed record GenerationReadinessResult(GenerationReadinessOutcome Outcome, string RequestId,
    bool CurrentReady = false, bool OriginalRequestReady = false, bool Replayed = false, Guid? Submitted = null,
    Guid? SavedReady = null, string? Stop = null, IReadOnlyList<GenerationReadinessMember>? Members = null);

/// <summary>Authenticated exact-generation readiness observation and expired observation renewal only.</summary>
public interface IOperatorRolloutObservation
{
    ValueTask<BatchObservationResult> WaitBatchAsync(string request, string actor, IReadOnlyList<BatchObservationMember> members,
        BatchObservationSettings? settings, GenerationObservationSettings? memberSettings, DateTimeOffset? deadline, CancellationToken token);
    ValueTask<GenerationReadinessResult> WaitAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation,
        string request, string actor, GenerationObservationSettings? settings, DateTimeOffset? deadline, CancellationToken token);
    ValueTask<GenerationReadinessResult> ResumeAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation,
        string request, string actor, IReadOnlyList<GenerationObservationMember> members, DateTimeOffset? deadline, CancellationToken token);
}

internal static class ObservationSettings
{
    internal static RolloutOptions Narrow(RolloutOptions limits, GenerationObservationSettings? settings)
    {
        limits.Validate();
        var value = new RolloutOptions(settings?.MaximumCalls ?? limits.MaximumCalls, settings?.MaximumRounds ?? limits.MaximumRounds,
            settings?.ReadTimeoutMilliseconds ?? limits.ReadTimeoutMilliseconds, settings?.PollMilliseconds ?? limits.PollMilliseconds,
            settings?.RequestMilliseconds ?? limits.RequestMilliseconds);
        value.Validate();
        if (value.MaximumCalls > limits.MaximumCalls || value.MaximumRounds > limits.MaximumRounds ||
            value.ReadTimeoutMilliseconds > limits.ReadTimeoutMilliseconds || value.RequestMilliseconds > limits.RequestMilliseconds ||
            value.PollMilliseconds < limits.PollMilliseconds) throw new ArgumentException("Observation settings exceed Host authority.");
        return value;
    }
}
