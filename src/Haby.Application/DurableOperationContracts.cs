using Haby.Domain;

namespace Haby.Application;

/// <summary>The exact identity of a step within an accepted generation.</summary>
public sealed record DurableOperationKey(ApplicationDeploymentId DeploymentId,
    DeploymentGeneration Generation, OperationId OperationId);

/// <summary>
/// One durable pilot operation state. After a restart only <see cref="Pending"/> and
/// <see cref="Preparing"/> may resume; <see cref="Dispatching"/> must move to
/// <see cref="NeedsAttention"/> without sending SQL, as defined by ADR-025.
/// </summary>
public enum DurableOperationState
{
    Pending,
    Preparing,
    Dispatching,
    Succeeded,
    Failed,
    NeedsAttention
}

/// <summary>A bounded, value-free reason code. It never carries driver text, configuration or secret material.</summary>
public enum DurableOperationReason
{
    SecretUnavailable, Connectivity, Conflict, ServerRejected, UncertainAfterDispatch,
    InputInvalid, InputUnavailable, HandlerUnavailable
}

/// <summary>A bounded, value-free outcome retained on one separately attributable attempt row.</summary>
public enum DurableAttemptOutcome
{
    Succeeded,
    Conflict,
    ServerRejected,
    Uncertain,
    CanceledBeforeDispatch,
    AbandonedBeforeDispatch,
    FailedBeforeDispatch
}

/// <summary>
/// Non-secret operation intent and durable state. Provider inputs come from the immutable
/// generation and are never re-resolved from the current Provider alias.
/// </summary>
public sealed record DurableOperation(DurableOperationKey Key,
    DurableOperationState State, EnvironmentRecordId Environment, ApplicationRecordId Application,
    ManifestRevisionRef Revision, ProviderInstanceId ProviderInstance,
    ProviderConfigurationRevisionNumber ConfigurationRevision, string ImplementationId,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DurableOperationReason? Reason,
    Guid? AttemptId, long AttemptNumber, DateTimeOffset? DispatchedAt, DateTimeOffset? FinalizedAt)
{
    public DurableOperationNode Node { get; init; } = null!;
}

/// <summary>One separately retained execution attempt. It is append-only history for the operation.</summary>
public sealed record DurableOperationAttempt(Guid AttemptId, long AttemptNumber, DateTimeOffset StartedAt,
    DateTimeOffset? DispatchedAt, DateTimeOffset? FinishedAt, DurableAttemptOutcome? Outcome);

/// <summary>The complete read model: the operation and every retained attempt.</summary>
public sealed record DurableOperationView(DurableOperation Operation,
    IReadOnlyList<DurableOperationAttempt> Attempts)
{
    public DurableOperationView(DurableOperation operation) : this(operation,
        Array.Empty<DurableOperationAttempt>())
    { }
}

/// <summary>The result of durably starting (or resuming) one execution attempt.</summary>
public abstract record DurableBeginAttemptResult
{
    private DurableBeginAttemptResult() { }
    public sealed record Started(DurableOperation Operation, Guid AttemptId, long AttemptNumber)
        : DurableBeginAttemptResult;
    public sealed record NotFound : DurableBeginAttemptResult;
    public sealed record Terminal(DurableOperation Operation) : DurableBeginAttemptResult;
}

/// <summary>
/// The result of committing the unique-attempt dispatch marker. An exception instead of a
/// result means the commit outcome is uncertain: SQL must not be sent and the attempt is abandoned.
/// </summary>
public abstract record DurableMarkerResult
{
    private DurableMarkerResult() { }
    public sealed record Committed : DurableMarkerResult;
    public sealed record Rejected(DurableOperation Operation) : DurableMarkerResult;
}

/// <summary>
/// The durable journal behind the dispatch barrier. Every transition is a compare-and-transition
/// guard; only the separate, audited PostgreSQL recovery path may change
/// <c>NeedsAttention</c> to <c>Succeeded</c>. Attempts are never erased.
/// </summary>
public interface IDurableOperationJournal
{
    /// <summary>Checks exact retained reuse evidence before new work, without creating attempts or advancing state.</summary>
    ValueTask<bool> ValidateReuseAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation,
        DurableActionAdmission admission, CancellationToken cancellationToken) => new(false);

    /// <summary>Completes a supported Resource-only generation through its locked, persisted evidence boundary.</summary>
    ValueTask<bool> TryCompleteGenerationAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation,
        DurableActionAdmission admission, CancellationToken cancellationToken) => new(false);

    ValueTask<DurableGenerationProgress?> ProgressAsync(ApplicationDeploymentId deployment,
        DeploymentGeneration generation, CancellationToken cancellationToken);
    ValueTask<DurableOperation?> FindAsync(DurableOperationKey key, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<DurableOperationAttempt>> FindAttemptsAsync(DurableOperationKey key,
        CancellationToken cancellationToken);

    /// <summary>Starts a new attempt from <c>Pending</c> or safely resumes an interrupted <c>Preparing</c> attempt.</summary>
    ValueTask<DurableBeginAttemptResult> TryBeginAttemptAsync(DurableOperationKey key, DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>Commits the <c>Dispatching</c> marker. Only a confirmed commit authorizes sending SQL.</summary>
    ValueTask<DurableMarkerResult> TryCommitDispatchMarkerAsync(DurableOperationKey key, Guid attemptId,
        DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Closes one still-<c>Preparing</c> attempt while retaining a bounded retryable reason.</summary>
    ValueTask<bool> TryCloseAttemptBeforeDispatchAsync(DurableOperationKey key, Guid attemptId,
        DurableAttemptOutcome outcome, DurableOperationReason? reason, DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>Records a definitive pre-dispatch failure or a post-dispatch uncertain outcome.</summary>
    ValueTask<bool> TryFinalizeFailureAsync(DurableOperationKey key, Guid attemptId,
        DurableOperationState terminal, DurableOperationReason reason, DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>Recovery: a durably committed <c>Dispatching</c> marker becomes <c>NeedsAttention</c> without SQL.</summary>
    ValueTask<bool> TryRecoverDispatchingAsync(DurableOperationKey key, DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically records <c>Succeeded</c> and checks operation completion. Mixed generations
    /// retain this success until the runner separately validates their combined reuse requirements.
    /// A rejected guard must leave the recoverable pre-terminal state, never a false applied generation.
    /// </summary>
    ValueTask<bool> TryRecordSuccessAsync(DurableOperationKey key, Guid attemptId, DateTimeOffset at,
        CancellationToken cancellationToken);
}

/// <summary>Read-only pilot operation query; it never resolves a current Provider alias.</summary>
public interface IDurableOperationQuery
{
    ValueTask<DurableOperation?> FindAsync(DurableOperationKey key,
        CancellationToken cancellationToken);
}

/// <summary>The bounded, value-free outcome of one execution request.</summary>
public enum DurableExecutionOutcome
{
    Succeeded,
    Failed,
    /// <summary>A pre-dispatch dependency is unavailable; the same operation can start another attempt.</summary>
    RetryableFailure,
    NeedsAttention,
    OperationNotFound,
    /// <summary>The dispatch marker commit result was uncertain; no SQL was sent and the state remains recoverable.</summary>
    DispatchUncertain,
    /// <summary>The exact handler is unavailable; persisted state and attempts are unchanged.</summary>
    HandlerUnavailable,
    Blocked,
    InputUnavailable
}

public sealed record DurableExecutionResult(DurableExecutionOutcome Outcome,
    DurableOperation? Operation)
{
    public static DurableExecutionResult NotFound() =>
        new(DurableExecutionOutcome.OperationNotFound, null);
}

/// <summary>
/// The internal single-instance execution boundary for one exact <see cref="DurableOperationKey"/>.
/// The operator adapters delegate here; SQL is never dispatched before a committed marker.
/// </summary>
public interface IDurableOperationExecutor
{
    ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperationKey key,
        CancellationToken cancellationToken);
    ValueTask<DurableOperationView?> ObserveAsync(DurableOperationKey key,
        CancellationToken cancellationToken);
}

/// <summary>Deterministic test-only windows around the dispatch barrier. Production uses the no-op instance.</summary>
public interface IDurableCreateDispatchFaultHooks
{
    /// <summary>Runs after the absence check and immediately before the marker compare-and-transition.</summary>
    ValueTask BeforeDispatchMarkerAsync(CancellationToken cancellationToken);
    /// <summary>Runs after the marker commit is confirmed and before any SQL is sent.</summary>
    ValueTask AfterDispatchMarkerCommittedAsync(CancellationToken cancellationToken);
    /// <summary>Runs after the dedicated session opens and before invoking the Create command.</summary>
    ValueTask DuringCreateCommandAsync(CancellationToken cancellationToken);
    /// <summary>Runs after confirmed creation plus the independent existence read, before terminal persistence.</summary>
    ValueTask BeforeSuccessPersistedAsync(CancellationToken cancellationToken);
}

public sealed class NullDurableCreateDispatchFaultHooks : IDurableCreateDispatchFaultHooks
{
    public ValueTask BeforeDispatchMarkerAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask AfterDispatchMarkerCommittedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask DuringCreateCommandAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask BeforeSuccessPersistedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
