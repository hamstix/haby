using Haby.Domain;

namespace Haby.Application;

/// <summary>The exact identity of one pilot Resource action in an accepted generation.</summary>
public enum DurableOperationAction { Create }

public sealed record DurableCreateOperationKey(ApplicationDeploymentId DeploymentId,
    DeploymentGeneration Generation, ManifestLocalId ResourceId, DurableOperationAction Action);

/// <summary>
/// One durable pilot operation state. After a restart only <see cref="Pending"/> and
/// <see cref="Preparing"/> may resume; <see cref="Dispatching"/> must move to
/// <see cref="NeedsAttention"/> without sending SQL, as defined by ADR-025.
/// </summary>
public enum DurableCreateOperationState
{
    Pending,
    Preparing,
    Dispatching,
    Succeeded,
    Failed,
    NeedsAttention
}

/// <summary>A bounded, value-free reason code. It never carries driver text, configuration or secret material.</summary>
public enum DurableCreateOperationReason
{
    /// <summary>The pinned generation input, codec, implementation or Provider revision could not be resolved exactly.</summary>
    ProviderInputUnavailable,
    /// <summary>The exact pinned SecretRef could not be resolved.</summary>
    SecretUnavailable,
    /// <summary>The accepted revision is not the one supported database shape for this operation.</summary>
    UnsupportedShape,
    /// <summary>
    /// Legacy PostgreSQL database-name validation reason retained for stored history and wire compatibility.
    /// Temporary debt under ADR-031: issue #91 must define a Provider-neutral successor and explicit
    /// legacy mapping, implemented before role/grant or any other new effect kind is admitted.
    /// </summary>
    DatabaseNameInvalid,
    /// <summary>The pinned target could not be reached or admitted the connection before dispatch.</summary>
    Connectivity,
    /// <summary>The target state conflicts with the requested operation, before dispatch or in a definitive response.</summary>
    Conflict,
    /// <summary>The target server definitively rejected the dispatched operation.</summary>
    ServerRejected,
    /// <summary>The post-dispatch outcome is uncertain; only manual recovery may resolve it.</summary>
    UncertainAfterDispatch
}

/// <summary>A bounded, value-free outcome retained on one separately attributable attempt row.</summary>
public enum DurableCreateAttemptOutcome
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
public sealed record DurableCreateOperation(DurableCreateOperationKey Key,
    DurableCreateOperationState State, EnvironmentRecordId Environment, ApplicationRecordId Application,
    ManifestRevisionRef Revision, ProviderInstanceId ProviderInstance,
    ProviderConfigurationRevisionNumber ConfigurationRevision, string ImplementationId,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DurableCreateOperationReason? Reason,
    Guid? AttemptId, long AttemptNumber, DateTimeOffset? DispatchedAt, DateTimeOffset? FinalizedAt);

/// <summary>One separately retained execution attempt. It is append-only history for the operation.</summary>
public sealed record DurableCreateOperationAttempt(Guid AttemptId, long AttemptNumber, DateTimeOffset StartedAt,
    DateTimeOffset? DispatchedAt, DateTimeOffset? FinishedAt, DurableCreateAttemptOutcome? Outcome);

/// <summary>The complete read model: the operation and every retained attempt.</summary>
public sealed record DurableCreateOperationView(DurableCreateOperation Operation,
    IReadOnlyList<DurableCreateOperationAttempt> Attempts)
{
    public DurableCreateOperationView(DurableCreateOperation operation) : this(operation,
        Array.Empty<DurableCreateOperationAttempt>())
    { }
}

/// <summary>The result of durably starting (or resuming) one execution attempt.</summary>
public abstract record DurableCreateBeginAttemptResult
{
    private DurableCreateBeginAttemptResult() { }
    public sealed record Started(DurableCreateOperation Operation, Guid AttemptId, long AttemptNumber)
        : DurableCreateBeginAttemptResult;
    public sealed record NotFound : DurableCreateBeginAttemptResult;
    public sealed record Terminal(DurableCreateOperation Operation) : DurableCreateBeginAttemptResult;
}

/// <summary>
/// The result of committing the unique-attempt dispatch marker. An exception instead of a
/// result means the commit outcome is uncertain: SQL must not be sent and the attempt is abandoned.
/// </summary>
public abstract record DurableCreateMarkerResult
{
    private DurableCreateMarkerResult() { }
    public sealed record Committed : DurableCreateMarkerResult;
    public sealed record Rejected(DurableCreateOperation Operation) : DurableCreateMarkerResult;
}

/// <summary>
/// The durable journal behind the dispatch barrier. Every transition is a compare-and-transition
/// guard; only the separate, audited PostgreSQL recovery path may change
/// <c>NeedsAttention</c> to <c>Succeeded</c>. Attempts are never erased.
/// </summary>
public interface IDurableCreateOperationJournal
{
    ValueTask<DurableCreateOperation?> FindAsync(DurableCreateOperationKey key, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<DurableCreateOperationAttempt>> FindAttemptsAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken);

    /// <summary>Starts a new attempt from <c>Pending</c> or safely resumes an interrupted <c>Preparing</c> attempt.</summary>
    ValueTask<DurableCreateBeginAttemptResult> TryBeginAttemptAsync(DurableCreateOperationKey key, DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>Commits the <c>Dispatching</c> marker. Only a confirmed commit authorizes sending SQL.</summary>
    ValueTask<DurableCreateMarkerResult> TryCommitDispatchMarkerAsync(DurableCreateOperationKey key, Guid attemptId,
        DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Closes one still-<c>Preparing</c> attempt while retaining a bounded retryable reason.</summary>
    ValueTask<bool> TryCloseAttemptBeforeDispatchAsync(DurableCreateOperationKey key, Guid attemptId,
        DurableCreateAttemptOutcome outcome, DurableCreateOperationReason? reason, DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>Records a definitive pre-dispatch failure or a post-dispatch uncertain outcome.</summary>
    ValueTask<bool> TryFinalizeFailureAsync(DurableCreateOperationKey key, Guid attemptId,
        DurableCreateOperationState terminal, DurableCreateOperationReason reason, DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>Recovery: a durably committed <c>Dispatching</c> marker becomes <c>NeedsAttention</c> without SQL.</summary>
    ValueTask<bool> TryRecoverDispatchingAsync(DurableCreateOperationKey key, DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically records <c>Succeeded</c> and advances the deployment's applied generation.
    /// A rejected guard must leave the recoverable pre-terminal state, never a false applied generation.
    /// </summary>
    ValueTask<bool> TryRecordSuccessAsync(DurableCreateOperationKey key, Guid attemptId, DateTimeOffset at,
        CancellationToken cancellationToken);
}

/// <summary>Read-only pilot operation query; it never resolves a current Provider alias.</summary>
public interface IDurableCreateOperationQuery
{
    ValueTask<DurableCreateOperation?> FindAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken);
}

/// <summary>The bounded, value-free outcome of one execution request.</summary>
public enum DurableCreateExecutionOutcome
{
    Succeeded,
    Failed,
    /// <summary>A pre-dispatch dependency is unavailable; the same operation can start another attempt.</summary>
    RetryableFailure,
    NeedsAttention,
    OperationNotFound,
    /// <summary>The dispatch marker commit result was uncertain; no SQL was sent and the state remains recoverable.</summary>
    DispatchUncertain
}

public sealed record DurableCreateExecutionResult(DurableCreateExecutionOutcome Outcome,
    DurableCreateOperation? Operation)
{
    public static DurableCreateExecutionResult NotFound() =>
        new(DurableCreateExecutionOutcome.OperationNotFound, null);
}

/// <summary>
/// The internal single-instance execution boundary for one exact <see cref="DurableCreateOperationKey"/>.
/// The operator adapters delegate here; SQL is never dispatched before a committed marker.
/// </summary>
public interface IDurableCreateOperationExecutor
{
    ValueTask<DurableCreateExecutionResult> ExecuteAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken);
    ValueTask<DurableCreateOperationView?> ObserveAsync(DurableCreateOperationKey key,
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
