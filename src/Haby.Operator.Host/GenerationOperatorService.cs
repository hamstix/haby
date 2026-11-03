using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<GenerationExecutionReply> ExecuteGeneration(GenerationRequest request, ServerCallContext context)
    {
        ApplicationDeploymentId deployment;
        DeploymentGeneration generation;
        try
        {
            deployment = ApplicationDeploymentId.Parse(request.DeploymentId);
            generation = DeploymentGeneration.Create(request.Generation);
        }
        catch (ArgumentException) { return new() { Status = GenerationExecutionStatus.InvalidRequest }; }
        if (generation.Value != 1) return new() { Status = GenerationExecutionStatus.UnsupportedGeneration };
        // The real runner owns the shared generation gate and calls the exact executor without reacquiring it.
        var result = await OperationCall(() => generationRunner.RunAsync(deployment, generation, context.CancellationToken), context);
        var reply = new GenerationExecutionReply
        {
            Status = result.Canceled ? GenerationExecutionStatus.Canceled : result.Progress is null
                ? GenerationExecutionStatus.NotFound : result.StopOutcome switch
                {
                    DurableExecutionOutcome.Blocked => GenerationExecutionStatus.Blocked,
                    DurableExecutionOutcome.Failed => GenerationExecutionStatus.Failed,
                    DurableExecutionOutcome.RetryableFailure => GenerationExecutionStatus.RetryableFailure,
                    DurableExecutionOutcome.NeedsAttention => GenerationExecutionStatus.NeedsAttention,
                    DurableExecutionOutcome.DispatchUncertain => GenerationExecutionStatus.DispatchUncertain,
                    DurableExecutionOutcome.InputUnavailable => GenerationExecutionStatus.InputUnavailable,
                    DurableExecutionOutcome.HandlerUnavailable => GenerationExecutionStatus.HandlerUnavailable,
                    DurableExecutionOutcome.OperationNotFound => GenerationExecutionStatus.NotFound,
                    null when result.Progress.Total > 0 && result.Progress.Completed == result.Progress.Total &&
                        result.Progress.AppliedGeneration == generation.Value => GenerationExecutionStatus.Succeeded,
                    null => GenerationExecutionStatus.Blocked,
                    _ => throw new InvalidOperationException("Unknown generation outcome.")
                },
            Progress = result.Progress is { } progress ? Summary(progress) : null
        };
        if (result.StoppedAt is { } stopped) reply.StoppedOperationId = stopped.Value;
        if (result.StopOutcome is { } outcome) reply.StopOutcome = ExecutionStatus(outcome);
        return reply;
    }

    private static GenerationProgress Summary(DurableGenerationProgress value)
    {
        var progress = new GenerationProgress
        {
            DeploymentId = value.DeploymentId.ToString(),
            Generation = value.Generation.Value,
            DesiredGeneration = value.DesiredGeneration,
            Completed = value.Completed,
            Total = value.Total
        };
        if (value.AppliedGeneration is { } applied) progress.AppliedGeneration = applied;
        if (value.StoppedAt is { } stopped) progress.StoppedOperationId = stopped.Operation.Key.OperationId.Value;
        foreach (var node in value.Nodes)
        {
            var item = new GenerationNodeProgress { Operation = Summary(node.Operation) };
            item.BlockingDependencies.AddRange(node.BlockingDependencies.Select(d => d.Value));
            if (node.LastAttemptOutcome is { } outcome) item.LastAttemptOutcome = AttemptOutcome(outcome);
            progress.Nodes.Add(item);
        }
        return progress;
    }
}
