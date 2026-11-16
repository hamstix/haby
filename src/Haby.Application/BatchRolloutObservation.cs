using Haby.Domain;

namespace Haby.Application;

/// <summary>Per-invocation limits. Callers may only lower configured limits.</summary>
public sealed record BatchObservationSettings(int? Concurrency = null, int? MaximumReads = null,
    int? RequestMilliseconds = null, int? StartsPerSecond = null);
public sealed record BatchObservationMember(ApplicationDeploymentId Deployment, DeploymentGeneration Generation, string RequestId);
public sealed record BatchObservationSummary(BatchObservationMember Member, GenerationReadinessOutcome Outcome,
    bool CurrentReady = false, bool OriginalRequestReady = false, bool Replayed = false,
    Guid? Submitted = null, Guid? SavedReady = null, string? Stop = null, bool CurrentApplied = false, bool OriginalRequestApplied = false,
    Guid? Retirement = null, long? AppliedGeneration = null, string CompletionReason = "not-ready");
public sealed record BatchObservationResult(string RequestId, bool CurrentReady, IReadOnlyList<BatchObservationSummary> Members,
    bool Canceled = false, bool Deadline = false, bool CurrentApplied = false);

internal sealed record BatchObservationOptions(int MaximumMembers = 200, int Concurrency = 4,
    int MaximumReads = 256, int RequestMilliseconds = 30000, int StartsPerSecond = 10)
{
    internal void Validate()
    {
        if (MaximumMembers is < 1 or > 200 || Concurrency is < 1 or > 8 || MaximumReads is < 1 or > 1024 ||
            RequestMilliseconds is < 1 or > 300000 || StartsPerSecond is < 1 or > 100)
            throw new ArgumentException("Invalid batch observation bounds.");
    }
    internal BatchObservationOptions Narrow(BatchObservationSettings? settings)
    {
        Validate();
        var value = this with
        {
            Concurrency = settings?.Concurrency ?? Concurrency,
            MaximumReads = settings?.MaximumReads ?? MaximumReads,
            RequestMilliseconds = settings?.RequestMilliseconds ?? RequestMilliseconds,
            StartsPerSecond = settings?.StartsPerSecond ?? StartsPerSecond
        };
        value.Validate();
        if (value.Concurrency > Concurrency || value.MaximumReads > MaximumReads || value.RequestMilliseconds > RequestMilliseconds ||
            value.StartsPerSecond > StartsPerSecond) throw new ArgumentException("Batch settings exceed Host authority.");
        return value;
    }
}

/// <summary>Wrap only the actual readiness read, after connection acquisition. No write authority.</summary>
internal interface IReadinessReadAdmission
{
    ValueTask<RolloutObservation> RunAsync(Func<CancellationToken, ValueTask<RolloutObservation>> read, CancellationToken token);
}
internal sealed class RolloutBatchExhaustedException : Exception;
internal sealed class RolloutReadRefusedException : Exception;

/// <summary>Invocation-local token bucket: burst <= concurrency; no queued tasks or replenishment timer.</summary>
internal sealed class BatchReadBudget
{
    private readonly object gate = new();
    private readonly TimeProvider time;
    private readonly BatchObservationOptions options;
    private int reads;
    private double tokens;
    private long refilled;
    internal BatchReadBudget(BatchObservationOptions options, TimeProvider clock, long started, TimeSpan duration)
    {
        this.options = options; time = clock; Started = started; Duration = duration; refilled = started; tokens = options.Concurrency;
    }
    internal long Started { get; }
    internal TimeSpan Duration { get; }
    internal TimeSpan Remaining => Duration - time.GetElapsedTime(Started);
    internal bool CanStart { get { lock (gate) return Remaining > TimeSpan.Zero && reads < options.MaximumReads; } }

    internal IReadinessReadAdmission Admission(Func<CancellationToken, ValueTask> authorize, Action? validateDispatch = null) => new ReadAdmission(this, authorize, validateDispatch);
    private TimeSpan Delay()
    {
        if (Remaining <= TimeSpan.Zero || reads >= options.MaximumReads) throw new RolloutBatchExhaustedException();
        var now = time.GetTimestamp();
        tokens = Math.Min(options.Concurrency, tokens + time.GetElapsedTime(refilled, now).TotalSeconds * options.StartsPerSecond);
        refilled = now;
        return tokens >= 1 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Max(1, (long)Math.Ceiling((1 - tokens) / options.StartsPerSecond * TimeSpan.TicksPerSecond)));
    }
    private sealed class ReadAdmission(BatchReadBudget owner, Func<CancellationToken, ValueTask> authorize, Action? validateDispatch) : IReadinessReadAdmission
    {
        public async ValueTask<RolloutObservation> RunAsync(Func<CancellationToken, ValueTask<RolloutObservation>> read, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                TimeSpan delay;
                lock (owner.gate) delay = owner.Delay();
                if (delay > TimeSpan.Zero) { await Task.Delay(delay, owner.time, token); continue; }
                await authorize(token); // No database lock is held during pacing or network work.
                ValueTask<RolloutObservation> dispatched;
                lock (owner.gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (owner.Delay() > TimeSpan.Zero) continue; // Reauthorize after the next wait.
                    validateDispatch?.Invoke();
                    owner.tokens--; owner.reads++;
                    dispatched = read(token); // Charge and invoke together, including failed/canceled dispatch.
                }
                return await dispatched;
            }
        }
    }
}

internal sealed class BatchMemberObservation(BatchReadBudget budget)
{
    internal BatchReadBudget Budget => budget;
    internal bool Started { get; private set; }
    internal void Start()
    {
        if (!budget.CanStart) throw new RolloutBatchExhaustedException();
        Started = true;
    }
}
