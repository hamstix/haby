using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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
/// both the Create response and an independent existence read. It is not exposed through the
/// operator host and performs no manual assignment from <c>NeedsAttention</c>.
/// </summary>
public sealed class PostgreSqlDurableCreateOperationExecutor(IDurableCreateOperationJournal journal,
    IManifestRevisionQuery manifests, IDurableCreateTarget target, IDurableCreateDispatchFaultHooks hooks,
    TimeProvider clock) : IDurableCreateOperationExecutor
{
    private static readonly Regex SafeDatabase = new(@"\A[a-z][a-z0-9_]{0,62}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly ConcurrentDictionary<DurableCreateOperationKey, SemaphoreSlim> gates = new();

    public async ValueTask<DurableCreateExecutionResult> ExecuteAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var gate = gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteCoreAsync(key, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<DurableCreateOperationView?> ObserveAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var operation = await journal.FindAsync(key, cancellationToken);
        if (operation is null) return null;
        var attempts = await journal.FindAttemptsAsync(key, cancellationToken);
        return new DurableCreateOperationView(operation, attempts);
    }

    private async ValueTask<DurableCreateExecutionResult> ExecuteCoreAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken)
    {
        var operation = await journal.FindAsync(key, cancellationToken);
        if (operation is null) return DurableCreateExecutionResult.NotFound();
        if (operation.State is not (DurableCreateOperationState.Pending or DurableCreateOperationState.Preparing
            or DurableCreateOperationState.Dispatching))
            return new DurableCreateExecutionResult(MapTerminal(operation.State), operation);
        if (operation.State == DurableCreateOperationState.Dispatching)
            return await RecoverDispatchingAsync(key, cancellationToken);

        var attemptId = Guid.Empty;
        switch (await journal.TryBeginAttemptAsync(key, clock.GetUtcNow(), cancellationToken))
        {
            case DurableCreateBeginAttemptResult.Started started:
                {
                    attemptId = started.AttemptId;
                    operation = started.Operation;
                    break;
                }
            case DurableCreateBeginAttemptResult.Terminal terminal:
                {
                    if (terminal.Operation.State == DurableCreateOperationState.Dispatching)
                        return await RecoverDispatchingAsync(key, cancellationToken);
                    return MapObserved(terminal.Operation);
                }
            default:
                {
                    return DurableCreateExecutionResult.NotFound();
                }
        }

        // Pre-dispatch: typed rejections are definitive; unexpected failures leave a resumable
        // Preparing state, and caller cancellation closes only the attempt, never the operation.
        DurableCreateTargetInput input;
        try
        {
            var shape = await ResolveInputAsync(key, operation, cancellationToken);
            if (shape.Reason is { } rejection)
                return await FailBeforeDispatchAsync(key, attemptId, rejection, cancellationToken);
            input = shape.Input!;
            var absence = await target.CheckDatabaseAbsentAsync(input, cancellationToken);
            if (absence != DurableCreateAbsenceOutcome.Absent)
                return await FailBeforeDispatchAsync(key, attemptId, MapAbsenceFailure(absence), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await journal.TryCloseAttemptBeforeDispatchAsync(key, attemptId,
                DurableCreateAttemptOutcome.CanceledBeforeDispatch, clock.GetUtcNow(), CancellationToken.None);
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await hooks.BeforeDispatchMarkerAsync(cancellationToken);

        DurableCreateMarkerResult marker;
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

        if (marker is DurableCreateMarkerResult.Rejected rejected)
        {
            return rejected.Operation.State is DurableCreateOperationState.Succeeded
                    or DurableCreateOperationState.Failed or DurableCreateOperationState.NeedsAttention
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

        DurableCreateDispatchOutcome dispatch;
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
            case DurableCreateDispatchOutcome.Created:
                {
                    // Succeeded requires the confirmed Create response and an independent existence read.
                    DurableCreateAbsenceOutcome confirmed;
                    try
                    {
                        confirmed = await target.CheckDatabaseAbsentAsync(input, cancellationToken);
                    }
                    catch (Exception)
                    {
                        return await FinalizeUncertainAsync(key, attemptId, cancellationToken);
                    }

                    if (confirmed != DurableCreateAbsenceOutcome.Present)
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
                        ? new DurableCreateExecutionResult(DurableCreateExecutionOutcome.Succeeded,
                            await journal.FindAsync(key, CancellationToken.None) ?? operation)
                        : await UncertainResultAsync(key, cancellationToken);
                }
            case DurableCreateDispatchOutcome.Conflict:
                {
                    // A duplicate answer after dispatch is a definitive conflict; an existing database is
                    // never adopted. The post-marker outcome is recorded regardless of the caller token.
                    await journal.TryFinalizeFailureAsync(key, attemptId, DurableCreateOperationState.Failed,
                        DurableCreateOperationReason.Conflict, clock.GetUtcNow(), CancellationToken.None);
                    return await RereadResultAsync(key, cancellationToken);
                }
            case DurableCreateDispatchOutcome.ServerRejected:
                {
                    await journal.TryFinalizeFailureAsync(key, attemptId, DurableCreateOperationState.Failed,
                        DurableCreateOperationReason.ServerRejected, clock.GetUtcNow(), CancellationToken.None);
                    return await RereadResultAsync(key, cancellationToken);
                }
            default:
                {
                    return await FinalizeUncertainAsync(key, attemptId, cancellationToken);
                }
        }
    }

    private async ValueTask<DurableCreateExecutionResult> RecoverDispatchingAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken)
    {
        // A durably committed marker survived a restart. No SQL is sent, even when the database
        // exists; only manual recovery may resolve the uncertainty.
        var recovered = await journal.TryRecoverDispatchingAsync(key, clock.GetUtcNow(), cancellationToken);
        var operation = await journal.FindAsync(key, cancellationToken);
        if (operation is null) return DurableCreateExecutionResult.NotFound();
        return new DurableCreateExecutionResult(
            recovered && operation.State == DurableCreateOperationState.NeedsAttention
                ? DurableCreateExecutionOutcome.NeedsAttention
                : MapTerminal(operation.State), operation);
    }

    private async ValueTask<DurableCreateExecutionResult> FailBeforeDispatchAsync(DurableCreateOperationKey key,
        Guid attemptId, DurableCreateOperationReason reason, CancellationToken cancellationToken)
    {
        await journal.TryFinalizeFailureAsync(key, attemptId, DurableCreateOperationState.Failed, reason,
            clock.GetUtcNow(), cancellationToken);
        return await RereadResultAsync(key, cancellationToken);
    }

    private async ValueTask<DurableCreateExecutionResult> FinalizeUncertainAsync(DurableCreateOperationKey key,
        Guid attemptId, CancellationToken cancellationToken)
    {
        try
        {
            // The NeedsAttention bookkeeping for a dispatched window must survive caller cancellation.
            await journal.TryFinalizeFailureAsync(key, attemptId, DurableCreateOperationState.NeedsAttention,
                DurableCreateOperationReason.UncertainAfterDispatch, clock.GetUtcNow(), CancellationToken.None);
        }
        catch (Exception)
        {
            // The NeedsAttention write was itself interrupted; the recoverable marker stays in place.
        }

        return await RereadResultAsync(key, cancellationToken);
    }

    private async ValueTask<DurableCreateExecutionResult> UncertainResultAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken)
    {
        var operation = await journal.FindAsync(key, CancellationToken.None);
        return new DurableCreateExecutionResult(DurableCreateExecutionOutcome.DispatchUncertain, operation);
    }

    private async ValueTask<DurableCreateExecutionResult> RereadResultAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken)
    {
        var operation = await journal.FindAsync(key, CancellationToken.None);
        return operation is null
            ? DurableCreateExecutionResult.NotFound()
            : MapObserved(operation);
    }

    private async ValueTask<(DurableCreateTargetInput? Input, DurableCreateOperationReason? Reason)> ResolveInputAsync(
        DurableCreateOperationKey key, DurableCreateOperation operation, CancellationToken cancellationToken)
    {
        ManifestRevision? revision;
        try
        {
            revision = await manifests.FindAsync(operation.Revision, cancellationToken);
        }
        catch (InvalidDataException)
        {
            // A corrupt retained snapshot cannot be replaced by a current revision or alias.
            return (null, DurableCreateOperationReason.ProviderInputUnavailable);
        }
        if (revision is null)
            return (null, DurableCreateOperationReason.ProviderInputUnavailable);
        var spec = revision.Declaration.Spec;
        if (spec.Resources.Count != 1 || spec.Components.Count != 0 || spec.Exports.Count != 0 ||
            spec.Parameters.Count != 0)
            return (null, DurableCreateOperationReason.UnsupportedShape);
        var entry = spec.Resources.Single();
        if (entry.Key != key.ResourceId) return (null, DurableCreateOperationReason.UnsupportedShape);
        var resource = entry.Value;
        if (resource.Type != PostgreSqlDatabaseContract.Id || resource.Properties.Members.Count != 1 ||
            !resource.Properties.Members.TryGetValue("name", out var value) ||
            value is not ConfigurationString name)
            return (null, DurableCreateOperationReason.UnsupportedShape);
        if (!SafeDatabase.IsMatch(name.Value))
            return (null, DurableCreateOperationReason.DatabaseNameInvalid);
        return (new DurableCreateTargetInput(operation.Environment, operation.ProviderInstance,
            operation.ConfigurationRevision, operation.ImplementationId, name.Value), null);
    }

    private static DurableCreateOperationReason MapAbsenceFailure(DurableCreateAbsenceOutcome absence) =>
        absence switch
        {
            DurableCreateAbsenceOutcome.Present => DurableCreateOperationReason.Conflict,
            DurableCreateAbsenceOutcome.DatabaseNameInvalid => DurableCreateOperationReason.DatabaseNameInvalid,
            DurableCreateAbsenceOutcome.Connectivity => DurableCreateOperationReason.Connectivity,
            DurableCreateAbsenceOutcome.SecretUnavailable => DurableCreateOperationReason.SecretUnavailable,
            _ => DurableCreateOperationReason.ProviderInputUnavailable
        };

    private static DurableCreateExecutionResult MapObserved(DurableCreateOperation operation) => new(
        MapTerminal(operation.State), operation);

    private static DurableCreateExecutionOutcome MapTerminal(DurableCreateOperationState state) => state switch
    {
        DurableCreateOperationState.Succeeded => DurableCreateExecutionOutcome.Succeeded,
        DurableCreateOperationState.Failed => DurableCreateExecutionOutcome.Failed,
        DurableCreateOperationState.NeedsAttention => DurableCreateExecutionOutcome.NeedsAttention,
        _ => DurableCreateExecutionOutcome.DispatchUncertain
    };
}

public static class PostgreSqlDurableCreateOperationExecutorServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlDurableCreateOperationExecution(this IServiceCollection services)
    {
        services.TryAddSingleton<IDurableCreateDispatchFaultHooks, NullDurableCreateDispatchFaultHooks>();
        services.AddPostgreSqlDurableCreateTarget();
        services.TryAddSingleton<IDurableCreateOperationExecutor, PostgreSqlDurableCreateOperationExecutor>();
        return services;
    }
}
