using Haby.Domain;

namespace Haby.Application;

public sealed record DurableNodeProgress(DurableOperation Operation, IReadOnlyList<OperationId> BlockingDependencies, DurableAttemptOutcome? LastAttemptOutcome = null);
public sealed record DurableGenerationProgress(ApplicationDeploymentId DeploymentId, DeploymentGeneration Generation,
    long DesiredGeneration, long? AppliedGeneration, IReadOnlyList<DurableNodeProgress> Nodes)
{
    public int Reused { get; init; }
    public int Completed => Nodes.Count(n => n.Operation.State == DurableOperationState.Succeeded);
    public int Total => Nodes.Count;
    public DurableNodeProgress? StoppedAt => Nodes.FirstOrDefault(n => n.Operation.State is
        DurableOperationState.Dispatching or DurableOperationState.NeedsAttention or DurableOperationState.Failed ||
        n.Operation.State == DurableOperationState.Preparing && n.LastAttemptOutcome is
            DurableAttemptOutcome.FailedBeforeDispatch or DurableAttemptOutcome.CanceledBeforeDispatch);
}
public sealed record DurableGenerationRunResult(DurableGenerationProgress? Progress,
    OperationId? StoppedAt = null, DurableExecutionOutcome? StopOutcome = null, bool Canceled = false);

/// <summary>Explicit sequential pass; one owner of the generation gate, no recursive locking.</summary>
public sealed class DurableGenerationRunner(ExactDurableOperationExecutor executor,
    IDurableOperationJournal journal, DurableOperationSerialization serialization)
{
    public ValueTask<DurableGenerationProgress?> ProgressAsync(ApplicationDeploymentId deployment,
        DeploymentGeneration generation, CancellationToken cancellationToken) => journal.ProgressAsync(deployment, generation, cancellationToken);

    public async ValueTask<DurableGenerationRunResult> RunAsync(ApplicationDeploymentId deployment,
        DeploymentGeneration generation, CancellationToken cancellationToken)
    {
        try
        {
            return await serialization.RunAsync(new(deployment, generation, OperationId.Create("generation")),
                () => RunCoreAsync(deployment, generation, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(await ProgressAsync(deployment, generation, CancellationToken.None), Canceled: true); }
    }

    private async ValueTask<DurableGenerationRunResult> RunCoreAsync(ApplicationDeploymentId deployment,
        DeploymentGeneration generation, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = await ProgressAsync(deployment, generation, cancellationToken);
            if (progress is null) return new(null);
            if (progress.Reused > 0 && progress.Total == 0)
            {
                var complete = await journal.TryCompleteReuseAsync(deployment, generation, cancellationToken);
                return new(await ProgressAsync(deployment, generation, CancellationToken.None),
                    StopOutcome: complete ? null : DurableExecutionOutcome.InputUnavailable);
            }
            if (progress.Completed == progress.Total) return new(progress);
            // Existing uncertainty/failure stops the pass even if independent pending work is ready.
            var stop = progress.Nodes.FirstOrDefault(n => n.Operation.State is DurableOperationState.Dispatching
                or DurableOperationState.NeedsAttention or DurableOperationState.Failed);
            var next = stop ?? progress.Nodes.FirstOrDefault(n => n.Operation.State != DurableOperationState.Succeeded &&
                n.BlockingDependencies.Count == 0);
            if (next is null) return new(progress, StopOutcome: DurableExecutionOutcome.Blocked);
            var result = await executor.ExecuteCoreAsync(next.Operation.Key, cancellationToken);
            if (result.Outcome != DurableExecutionOutcome.Succeeded)
                return new(await ProgressAsync(deployment, generation, CancellationToken.None), next.Operation.Key.OperationId, result.Outcome, cancellationToken.IsCancellationRequested);
        }
    }
}
