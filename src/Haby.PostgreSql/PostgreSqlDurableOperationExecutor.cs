using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.PostgreSql;

/// <summary>
/// The internal single-instance execution boundary for one exact durable Create. The unique-attempt
/// <c>Dispatching</c> marker is committed durably before any SQL is sent; after the marker is
/// committed nothing is retried automatically and no success is recorded that is not confirmed by
/// both the Create response and an independent existence read. The operator host delegates here;
/// this executor performs no manual assignment from <c>NeedsAttention</c>.
/// </summary>
public sealed class PostgreSqlDurableOperationExecutor(IDurableOperationJournal journal,
    IPostgreSqlDatabaseCreateTarget target, IDurableCreateDispatchFaultHooks hooks,
    TimeProvider clock, DurableOperationSerialization serialization) : IDurableOperationExecutor
{
    private readonly PostgreSqlDurableCreateInputResolver resolver = new();

    public ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperationKey key,
        CancellationToken cancellationToken) => serialization.RunAsync(key,
            () => ExecuteCoreAsync(key, cancellationToken), cancellationToken);

    // The exact dispatcher already holds the shared operation gate.
    internal ValueTask<DurableExecutionResult> ExecuteUnderLockAsync(DurableOperationKey key,
        PostgreSqlDatabaseCreateInput? input, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(key, cancellationToken, input, prepared: true);

    // Observation retains the existing journal-only read behavior. It never transitions
    // state and must remain available while an external request holds the execution gate.
    public async ValueTask<DurableOperationView?> ObserveAsync(DurableOperationKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var operation = await journal.FindAsync(key, cancellationToken);
        return operation is null ? null : new DurableOperationView(operation,
            await journal.FindAttemptsAsync(key, cancellationToken));
    }

    private async ValueTask<DurableExecutionResult> ExecuteCoreAsync(DurableOperationKey key,
        CancellationToken cancellationToken, PostgreSqlDatabaseCreateInput? preparedInput = null, bool prepared = false)
    {
        var operation = await journal.FindAsync(key, cancellationToken);
        if (operation is null) return DurableExecutionResult.NotFound();
        if (operation.State is not (DurableOperationState.Pending or DurableOperationState.Preparing
            or DurableOperationState.Dispatching))
            return new DurableExecutionResult(MapTerminal(operation.State), operation);
        if (operation.State == DurableOperationState.Dispatching)
            return await RecoverDispatchingAsync(key, cancellationToken);

        var progress = await journal.ProgressAsync(key.DeploymentId, key.Generation, cancellationToken);
        if (progress is null) return DurableExecutionResult.NotFound();
        if (operation.Node.Dependencies.Any(d => !progress.Nodes.Any(n => n.Operation.Key.OperationId == d &&
            n.Operation.State == DurableOperationState.Succeeded))) return new(DurableExecutionOutcome.Blocked, operation);

        var attemptId = Guid.Empty;
        switch (await journal.TryBeginAttemptAsync(key, clock.GetUtcNow(), cancellationToken))
        {
            case DurableBeginAttemptResult.Started started:
                {
                    attemptId = started.AttemptId;
                    operation = started.Operation;
                    break;
                }
            case DurableBeginAttemptResult.Terminal terminal:
                {
                    if (terminal.Operation.State == DurableOperationState.Dispatching)
                        return await RecoverDispatchingAsync(key, cancellationToken);
                    return MapObserved(terminal.Operation);
                }
            default:
                {
                    return DurableExecutionResult.NotFound();
                }
        }

        // Before the barrier, temporary dependency failures close only this attempt. Immutable
        // input defects and a confirmed preexisting database are definitive terminal failures.
        PostgreSqlDatabaseCreateInput input;
        try
        {
            var shape = prepared
                ? (Input: preparedInput, Reason: preparedInput is null ? DurableOperationReason.InputInvalid : (DurableOperationReason?)null, Definitive: true)
                : await resolver.ResolveAsync(key, operation, cancellationToken);
            if (shape.Reason is { } rejection)
                return await FailBeforeDispatchAsync(key, attemptId, rejection, shape.Definitive,
                    cancellationToken);
            input = shape.Input!;
            var absence = await target.CheckDatabaseAbsentAsync(input, cancellationToken);
            if (absence != PostgreSqlDatabaseAbsenceOutcome.Absent)
                return await FailBeforeDispatchAsync(key, attemptId, MapAbsenceFailure(absence),
                    absence is PostgreSqlDatabaseAbsenceOutcome.Present or PostgreSqlDatabaseAbsenceOutcome.DatabaseNameInvalid,
                    cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await journal.TryCloseAttemptBeforeDispatchAsync(key, attemptId,
                DurableAttemptOutcome.CanceledBeforeDispatch, null, clock.GetUtcNow(), CancellationToken.None);
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await hooks.BeforeDispatchMarkerAsync(cancellationToken);

        DurableMarkerResult marker;
        try
        {
            marker = await journal.TryCommitDispatchMarkerAsync(key, attemptId, clock.GetUtcNow(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The commit outcome is unknown, so SQL must not be sent even though the attempt was canceled.
            throw;
        }
        catch (Exception)
        {
            // An uncertain marker commit never authorizes SQL. The state stays recoverable.
            return await UncertainResultAsync(key, cancellationToken);
        }

        if (marker is DurableMarkerResult.Rejected rejected)
        {
            return rejected.Operation.State is DurableOperationState.Succeeded
                    or DurableOperationState.Failed or DurableOperationState.NeedsAttention
                ? MapObserved(rejected.Operation)
                : await UncertainResultAsync(key, cancellationToken);
        }

        // Past the committed marker the effect intent is durable: every interruption records
        // NeedsAttention without retry and never a false applied generation.
        try
        {
            await hooks.AfterDispatchMarkerCommittedAsync(cancellationToken);
        }
        catch (Exception)
        {
            return await FinalizeUncertainAsync(key, attemptId, cancellationToken);
        }

        PostgreSqlDatabaseCreateDispatchOutcome dispatch;
        try
        {
            dispatch = await target.DispatchCreateAsync(input, cancellationToken);
        }
        catch (Exception)
        {
            return await FinalizeUncertainAsync(key, attemptId, cancellationToken);
        }

        switch (dispatch)
        {
            case PostgreSqlDatabaseCreateDispatchOutcome.Created:
                {
                    // Succeeded requires the confirmed Create response and an independent existence read.
                    PostgreSqlDatabaseAbsenceOutcome confirmed;
                    try
                    {
                        confirmed = await target.CheckDatabaseAbsentAsync(input, cancellationToken);
                    }
                    catch (Exception)
                    {
                        return await FinalizeUncertainAsync(key, attemptId, cancellationToken);
                    }

                    if (confirmed != PostgreSqlDatabaseAbsenceOutcome.Present)
                        return await FinalizeUncertainAsync(key, attemptId, cancellationToken);

                    // The crash window after external success but before terminal persistence must leave
                    // the recoverable Dispatching marker untouched; nothing here may rewrite it.
                    await hooks.BeforeSuccessPersistedAsync(cancellationToken);
                    // A verified success is recorded to completion: the caller token can no longer drop it.
                    bool recorded;
                    try
                    {
                        recorded = await journal.TryRecordSuccessAsync(key, attemptId, clock.GetUtcNow(),
                            CancellationToken.None);
                    }
                    catch (OperationCanceledException)
                    {
                        return await UncertainResultAsync(key, cancellationToken);
                    }
                    catch (Exception)
                    {
                        return await UncertainResultAsync(key, cancellationToken);
                    }

                    return recorded
                        ? new DurableExecutionResult(DurableExecutionOutcome.Succeeded,
                            await journal.FindAsync(key, CancellationToken.None) ?? operation)
                        : await UncertainResultAsync(key, cancellationToken);
                }
            case PostgreSqlDatabaseCreateDispatchOutcome.Conflict:
                {
                    // A duplicate answer after dispatch is a definitive conflict; an existing database is
                    // never adopted. The post-marker outcome is recorded regardless of the caller token.
                    await journal.TryFinalizeFailureAsync(key, attemptId, DurableOperationState.Failed,
                        DurableOperationReason.Conflict, clock.GetUtcNow(), CancellationToken.None);
                    return await RereadResultAsync(key, cancellationToken);
                }
            case PostgreSqlDatabaseCreateDispatchOutcome.ServerRejected:
                {
                    await journal.TryFinalizeFailureAsync(key, attemptId, DurableOperationState.Failed,
                        DurableOperationReason.ServerRejected, clock.GetUtcNow(), CancellationToken.None);
                    return await RereadResultAsync(key, cancellationToken);
                }
            default:
                {
                    return await FinalizeUncertainAsync(key, attemptId, cancellationToken);
                }
        }
    }

    private async ValueTask<DurableExecutionResult> RecoverDispatchingAsync(DurableOperationKey key,
        CancellationToken cancellationToken)
    {
        // A durably committed marker survived a restart. No SQL is sent, even when the database
        // exists; only manual recovery may resolve the uncertainty.
        var recovered = await journal.TryRecoverDispatchingAsync(key, clock.GetUtcNow(), cancellationToken);
        var operation = await journal.FindAsync(key, cancellationToken);
        if (operation is null) return DurableExecutionResult.NotFound();
        return new DurableExecutionResult(
            recovered && operation.State == DurableOperationState.NeedsAttention
                ? DurableExecutionOutcome.NeedsAttention
                : MapTerminal(operation.State), operation);
    }

    private async ValueTask<DurableExecutionResult> FailBeforeDispatchAsync(DurableOperationKey key,
        Guid attemptId, DurableOperationReason reason, bool definitive, CancellationToken cancellationToken)
    {
        if (definitive)
            await journal.TryFinalizeFailureAsync(key, attemptId, DurableOperationState.Failed, reason,
                clock.GetUtcNow(), cancellationToken);
        else
            await journal.TryCloseAttemptBeforeDispatchAsync(key, attemptId,
                DurableAttemptOutcome.FailedBeforeDispatch, reason, clock.GetUtcNow(), cancellationToken);
        return await RereadResultAsync(key, cancellationToken);
    }

    private async ValueTask<DurableExecutionResult> FinalizeUncertainAsync(DurableOperationKey key,
        Guid attemptId, CancellationToken cancellationToken)
    {
        try
        {
            // The NeedsAttention bookkeeping for a dispatched window must survive caller cancellation.
            await journal.TryFinalizeFailureAsync(key, attemptId, DurableOperationState.NeedsAttention,
                DurableOperationReason.UncertainAfterDispatch, clock.GetUtcNow(), CancellationToken.None);
        }
        catch (Exception)
        {
            // The NeedsAttention write was itself interrupted; the recoverable marker stays in place.
        }

        return await RereadResultAsync(key, cancellationToken);
    }

    private async ValueTask<DurableExecutionResult> UncertainResultAsync(DurableOperationKey key,
        CancellationToken cancellationToken)
    {
        var operation = await journal.FindAsync(key, CancellationToken.None);
        return new DurableExecutionResult(DurableExecutionOutcome.DispatchUncertain, operation);
    }

    private async ValueTask<DurableExecutionResult> RereadResultAsync(DurableOperationKey key,
        CancellationToken cancellationToken)
    {
        var operation = await journal.FindAsync(key, CancellationToken.None);
        return operation is null
            ? DurableExecutionResult.NotFound()
            : MapObserved(operation);
    }

    private static DurableOperationReason MapAbsenceFailure(PostgreSqlDatabaseAbsenceOutcome absence) =>
        absence switch
        {
            PostgreSqlDatabaseAbsenceOutcome.Present => DurableOperationReason.Conflict,
            PostgreSqlDatabaseAbsenceOutcome.DatabaseNameInvalid => DurableOperationReason.InputInvalid,
            PostgreSqlDatabaseAbsenceOutcome.Connectivity => DurableOperationReason.Connectivity,
            PostgreSqlDatabaseAbsenceOutcome.SecretUnavailable => DurableOperationReason.SecretUnavailable,
            PostgreSqlDatabaseAbsenceOutcome.ProviderInputUnavailable => DurableOperationReason.InputUnavailable,
            _ => DurableOperationReason.InputUnavailable
        };

    private static DurableExecutionResult MapObserved(DurableOperation operation) => new(
        operation.State == DurableOperationState.Preparing &&
        operation.Reason is DurableOperationReason.InputUnavailable or
            DurableOperationReason.SecretUnavailable or DurableOperationReason.Connectivity
            ? DurableExecutionOutcome.RetryableFailure
            : MapTerminal(operation.State), operation);

    private static DurableExecutionOutcome MapTerminal(DurableOperationState state) => state switch
    {
        DurableOperationState.Succeeded => DurableExecutionOutcome.Succeeded,
        DurableOperationState.Failed => DurableExecutionOutcome.Failed,
        DurableOperationState.NeedsAttention => DurableExecutionOutcome.NeedsAttention,
        _ => DurableExecutionOutcome.DispatchUncertain
    };
}

public static class PostgreSqlDurableOperationExecutorServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlDurableOperationExecution(this IServiceCollection services)
    {
        services.TryAddSingleton<IDurableCreateDispatchFaultHooks, NullDurableCreateDispatchFaultHooks>();
        services.AddPostgreSqlDatabaseCreateTarget();
        services.AddPostgreSqlDatabaseCreateAction();
        services.TryAddSingleton<PostgreSqlDurableOperationExecutor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDurableActionHandler, PostgreSqlDatabaseCreateAction>());
        services.TryAddSingleton<ExactDurableOperationExecutor>();
        services.TryAddSingleton<IDurableOperationExecutor>(p => p.GetRequiredService<ExactDurableOperationExecutor>());
        services.TryAddSingleton<DurableGenerationRunner>();
        return services;
    }
}
