using System.Collections.Concurrent;
using Haby.Domain;

namespace Haby.Application;

/// <summary>One singleton gate owner for common dispatch, direct adapter calls and observation.</summary>
public sealed class DurableOperationSerialization
{
    private readonly ConcurrentDictionary<(ApplicationDeploymentId, DeploymentGeneration), SemaphoreSlim> gates = new();
    public async ValueTask<T> RunAsync<T>(DurableOperationKey key, Func<ValueTask<T>> call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var gate = gates.GetOrAdd((key.DeploymentId, key.Generation), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await call(); }
        finally { gate.Release(); }
    }
}

/// <summary>Journal states precede metadata-only exact selection; no alias or configuration decode.</summary>
public sealed class ExactDurableOperationExecutor(IDurableOperationJournal journal,
    DurableActionRegistry registry,
    DurableOperationSerialization serialization, TimeProvider clock) : IDurableOperationExecutor
{
    public ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperationKey key,
        CancellationToken cancellationToken) => serialization.RunAsync(key, () => ExecuteCoreAsync(key, cancellationToken), cancellationToken);

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

    internal async ValueTask<DurableExecutionResult> ExecuteCoreAsync(DurableOperationKey key,
        CancellationToken cancellationToken)
    {
        var operation = await journal.FindAsync(key, cancellationToken);
        if (operation is null) return DurableExecutionResult.NotFound();
        if (operation.State == DurableOperationState.Dispatching)
        {
            await journal.TryRecoverDispatchingAsync(key, clock.GetUtcNow(), cancellationToken);
            operation = await journal.FindAsync(key, cancellationToken);
            if (operation is null) return DurableExecutionResult.NotFound();
        }
        var terminal = operation.State switch
        {
            DurableOperationState.Succeeded => DurableExecutionOutcome.Succeeded,
            DurableOperationState.Failed => DurableExecutionOutcome.Failed,
            DurableOperationState.NeedsAttention => DurableExecutionOutcome.NeedsAttention,
            DurableOperationState.Dispatching => DurableExecutionOutcome.DispatchUncertain,
            _ => (DurableExecutionOutcome?)null
        };
        if (terminal is { } outcome) return new(outcome, operation);

        var progress = await journal.ProgressAsync(key.DeploymentId, key.Generation, cancellationToken);
        if (progress is null) return DurableExecutionResult.NotFound();
        if (progress.Reused > 0 && !await journal.ValidateReuseAsync(key.DeploymentId, key.Generation, new DurableActionAdmission(registry), cancellationToken))
            return new(DurableExecutionOutcome.InputUnavailable, operation);
        if (operation.Node.Dependencies.Any(dependency => !progress.Nodes.Any(n =>
            n.Operation.Key.OperationId == dependency && n.Operation.State == DurableOperationState.Succeeded)))
            return new(DurableExecutionOutcome.Blocked, operation);
        // Admission metadata is not execution capability. Refuse the whole required graph
        // before any effect if even one mandatory exact handler is absent or cannot decode it.
        if (progress.Nodes.Any(n => registry.Find(n.Operation.Node.Action) is null))
            return new(DurableExecutionOutcome.HandlerUnavailable, operation);
        if (progress.Nodes.Any(n => !registry.MatchesEvidence(n.Operation.Node) || registry.Find(n.Operation.Node.Action)!.Supports(n.Operation.Node) != true))
            return new(DurableExecutionOutcome.InputUnavailable, operation);
        var handler = registry.Find(operation.Node.Action);
        if (handler is null) return new(DurableExecutionOutcome.HandlerUnavailable, operation);
        if (!handler.Supports(operation.Node)) return new(DurableExecutionOutcome.InputUnavailable, operation);
        return await handler.ExecuteAsync(operation, cancellationToken);
    }

}
