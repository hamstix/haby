using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliGenerationNode(CliOperation Operation, string[] BlockingDependencies, string? LastAttemptOutcome);
internal sealed record CliGenerationProgress(string DeploymentId, long Generation, long DesiredGeneration,
    long? AppliedGeneration, int Completed, int Total, CliGenerationNode[] Nodes, string? StoppedOperationId)
{ public int ReusedResources { get; init; } }
internal sealed record CliGenerationExecutionData(CliGenerationProgress? Progress, string? StoppedOperationId, string? StopOutcome);

internal static class GenerationPresentation
{
    internal static string Outcome(GenerationExecutionStatus status) => status switch
    {
        GenerationExecutionStatus.Succeeded => "succeeded",
        GenerationExecutionStatus.InvalidRequest => "invalidRequest",
        GenerationExecutionStatus.UnsupportedGeneration => "unsupportedGeneration",
        GenerationExecutionStatus.NotFound => "notFound",
        GenerationExecutionStatus.Blocked => "blocked",
        GenerationExecutionStatus.Failed => "failed",
        GenerationExecutionStatus.RetryableFailure => "retryableFailure",
        GenerationExecutionStatus.NeedsAttention => "needsAttention",
        GenerationExecutionStatus.DispatchUncertain => "dispatchUncertain",
        GenerationExecutionStatus.InputUnavailable => "inputUnavailable",
        GenerationExecutionStatus.HandlerUnavailable => "handlerUnavailable",
        GenerationExecutionStatus.Canceled => "canceled",
        _ => throw new InvalidDataException("Unknown server outcome.")
    };

    internal static int Exit(GenerationExecutionReply reply)
    {
        _ = Outcome(reply.Status);
        if (reply.Status == GenerationExecutionStatus.Succeeded)
        {
            if (reply.Progress is not { } p || (p.Total <= 0 && p.ReusedResources <= 0) || p.Completed != p.Total ||
                !p.HasAppliedGeneration || p.AppliedGeneration < p.Generation ||
                p.Nodes.Count != p.Total || p.Nodes.Any(n => n.Operation?.State != OperationState.Succeeded) ||
                reply.HasStopOutcome || reply.HasStoppedOperationId)
                throw new InvalidDataException("Incomplete generation success snapshot.");
            return CliApplication.Success;
        }
        return reply.Status switch
        {
            GenerationExecutionStatus.HandlerUnavailable => CliApplication.UsageError,
            GenerationExecutionStatus.Canceled => CliApplication.TransportError,
            _ => CliApplication.Rejected
        };
    }

    internal static CliGenerationProgress? Progress(GenerationProgress? p) => p is null ? null : new(
        p.DeploymentId, p.Generation, p.DesiredGeneration, p.HasAppliedGeneration ? p.AppliedGeneration : null,
        p.Completed, p.Total, [.. p.Nodes.Select(n => new CliGenerationNode(
            OperationPresentation.Data(new OperationReply { Operation = n.Operation }).Operation
                ?? throw new InvalidDataException("Missing operation snapshot."),
            [.. n.BlockingDependencies], n.HasLastAttemptOutcome ? OperationPresentation.AttemptOutcome(n.LastAttemptOutcome) : null))],
        p.HasStoppedOperationId ? p.StoppedOperationId : null)
    { ReusedResources = p.ReusedResources };
}

internal sealed partial class CliJsonPresenter
{
    internal int PrintGeneration(GenerationExecutionReply reply)
    {
        var exit = GenerationPresentation.Exit(reply);
        Write("deployment generation execute", GenerationPresentation.Outcome(reply.Status),
            new CliGenerationExecutionData(GenerationPresentation.Progress(reply.Progress),
                reply.HasStoppedOperationId ? reply.StoppedOperationId : null,
                reply.HasStopOutcome ? OperationPresentation.Outcome(reply.StopOutcome) : null), NoDiagnostics,
            CliOutputJsonContext.Default.CliEnvelopeCliGenerationExecutionData);
        return exit;
    }
}

internal sealed partial class CliTextPresenter
{
    internal void PrintProgress(CliGenerationProgress? p)
    {
        if (p is null) return;
        _output.WriteLine($"Deployment: {p.DeploymentId}; generation: {p.Generation}; desired: {p.DesiredGeneration}; applied: {p.AppliedGeneration?.ToString() ?? "(none)"}; completed: {p.Completed}/{p.Total}");
        if (p.ReusedResources > 0) _output.WriteLine($"Reused Resources: {p.ReusedResources}; external operations: {p.Total}");
        foreach (var n in p.Nodes)
            _output.WriteLine($"Operation: {n.Operation.Key.OperationId}; Resource: {n.Operation.ResourceId}; action: {n.Operation.ActionContract}; state: {n.Operation.State}; reason: {n.Operation.Reason ?? "(none)"}; last attempt: {n.LastAttemptOutcome ?? "(none)"}; blocking dependencies: {string.Join(", ", n.BlockingDependencies)}");
        if (p.StoppedOperationId is { } stopped) _output.WriteLine($"Persisted stop: {stopped}");
    }

    internal int PrintGeneration(GenerationExecutionReply reply)
    {
        var exit = GenerationPresentation.Exit(reply);
        _output.WriteLine(GenerationPresentation.Outcome(reply.Status));
        PrintProgress(GenerationPresentation.Progress(reply.Progress));
        if (reply.HasStoppedOperationId) _output.WriteLine($"Stopped operation: {reply.StoppedOperationId}");
        if (reply.HasStopOutcome) _output.WriteLine($"Stop outcome: {OperationPresentation.Outcome(reply.StopOutcome)}");
        if (exit != CliApplication.Success)
        {
            _output.WriteLine("Execution incomplete. Stop and inspect persisted progress before any explicit repeat; uncertainty does not authorize retry or recovery.");
            if (reply.Progress is { } p)
                _output.WriteLine($"Inspect: habyctl deployment generation get {p.DeploymentId} {p.Generation}");
        }
        return exit;
    }
}
