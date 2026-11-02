using System.Collections.Concurrent;
using Haby.Domain;

namespace Haby.Application;

/// <summary>One singleton gate owner for common dispatch, direct adapter calls and observation.</summary>
public sealed class DurableOperationSerialization
{
    private readonly ConcurrentDictionary<DurableCreateOperationKey, SemaphoreSlim> gates = new();
    public async ValueTask<T> RunAsync<T>(DurableCreateOperationKey key, Func<ValueTask<T>> call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var gate = gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await call(); }
        finally { gate.Release(); }
    }
}

/// <summary>Journal states precede metadata-only exact selection; no alias or configuration decode.</summary>
public sealed class ExactDurableCreateOperationExecutor(IDurableCreateOperationJournal journal,
    IManifestRevisionQuery manifests, IProviderPreflightRevisionReader providers, DurableActionRegistry registry,
    DurableOperationSerialization serialization, TimeProvider clock) : IDurableCreateOperationExecutor
{
    public ValueTask<DurableCreateExecutionResult> ExecuteAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken) => serialization.RunAsync(key, () => ExecuteCoreAsync(key, cancellationToken), cancellationToken);

    // Observation retains the existing journal-only read behavior. It never transitions
    // state and must remain available while an external request holds the execution gate.
    public async ValueTask<DurableCreateOperationView?> ObserveAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var operation = await journal.FindAsync(key, cancellationToken);
        return operation is null ? null : new DurableCreateOperationView(operation,
            await journal.FindAttemptsAsync(key, cancellationToken));
    }

    private async ValueTask<DurableCreateExecutionResult> ExecuteCoreAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken)
    {
        var operation = await journal.FindAsync(key, cancellationToken);
        if (operation is null) return DurableCreateExecutionResult.NotFound();
        if (operation.State == DurableCreateOperationState.Dispatching)
        {
            await journal.TryRecoverDispatchingAsync(key, clock.GetUtcNow(), cancellationToken);
            operation = await journal.FindAsync(key, cancellationToken);
            if (operation is null) return DurableCreateExecutionResult.NotFound();
        }
        var terminal = operation.State switch
        {
            DurableCreateOperationState.Succeeded => DurableCreateExecutionOutcome.Succeeded,
            DurableCreateOperationState.Failed => DurableCreateExecutionOutcome.Failed,
            DurableCreateOperationState.NeedsAttention => DurableCreateExecutionOutcome.NeedsAttention,
            DurableCreateOperationState.Dispatching => DurableCreateExecutionOutcome.DispatchUncertain,
            _ => (DurableCreateExecutionOutcome?)null
        };
        if (terminal is { } outcome) return new(outcome, operation);

        ManifestRevision? manifest;
        try { manifest = await manifests.FindAsync(operation.Revision, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (InvalidDataException) { return await InputFailureAsync(key, DurableCreateOperationReason.InputUnavailable, true, cancellationToken); }
        catch (Exception) { return await InputFailureAsync(key, DurableCreateOperationReason.InputUnavailable, false, cancellationToken); }
        if (manifest is null) return await InputFailureAsync(key, DurableCreateOperationReason.InputUnavailable, false, cancellationToken);
        if (!DurableActionAdmission.SupportsPilotShape(manifest.Declaration.Spec) ||
            !manifest.Declaration.Spec.Resources.TryGetValue(key.ResourceId, out var resource) ||
            key.Action != DurableOperationAction.Create || key.Generation.Value != 1)
            return await InputFailureAsync(key, DurableCreateOperationReason.InputInvalid, true, cancellationToken);
        var action = registry.HistoricalCreate(resource.Type);
        ProviderAdapterKey? provider;
        try
        {
            provider = await providers.FindKeyAsync(operation.Environment,
                new ProviderAliasBinding(operation.ProviderInstance, operation.ConfigurationRevision), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return await InputFailureAsync(key, DurableCreateOperationReason.InputUnavailable, false, cancellationToken); }
        if (provider is null || provider.ImplementationId != operation.ImplementationId)
            return await InputFailureAsync(key, DurableCreateOperationReason.InputUnavailable, false, cancellationToken);
        DurableActionAdmissionKey admissionKey;
        try { admissionKey = new(resource.Type, provider); }
        catch (ArgumentException) { return await InputFailureAsync(key, DurableCreateOperationReason.InputUnavailable, true, cancellationToken); }
        if (action is null || registry.Find(new DurableActionExecutionKey(admissionKey, action)) is not { } handler)
            return new(DurableCreateExecutionOutcome.HandlerUnavailable, operation);
        return await handler.ExecuteAsync(operation, resource, cancellationToken);
    }

    private async ValueTask<DurableCreateExecutionResult> InputFailureAsync(DurableCreateOperationKey key,
        DurableCreateOperationReason reason, bool definitive, CancellationToken cancellationToken)
    {
        var begun = await journal.TryBeginAttemptAsync(key, clock.GetUtcNow(), cancellationToken);
        if (begun is DurableCreateBeginAttemptResult.Started attempt)
        {
            if (definitive)
                await journal.TryFinalizeFailureAsync(key, attempt.AttemptId, DurableCreateOperationState.Failed,
                    reason, clock.GetUtcNow(), cancellationToken);
            else
                await journal.TryCloseAttemptBeforeDispatchAsync(key, attempt.AttemptId,
                    DurableCreateAttemptOutcome.FailedBeforeDispatch, reason, clock.GetUtcNow(), cancellationToken);
        }
        var operation = await journal.FindAsync(key, cancellationToken);
        return operation is null ? DurableCreateExecutionResult.NotFound() : new(
            operation.State == DurableCreateOperationState.Failed ? DurableCreateExecutionOutcome.Failed :
            operation.State == DurableCreateOperationState.NeedsAttention ? DurableCreateExecutionOutcome.NeedsAttention :
            operation.State == DurableCreateOperationState.Succeeded ? DurableCreateExecutionOutcome.Succeeded :
            operation.State == DurableCreateOperationState.Preparing ? DurableCreateExecutionOutcome.RetryableFailure :
            DurableCreateExecutionOutcome.DispatchUncertain, operation);
    }
}
