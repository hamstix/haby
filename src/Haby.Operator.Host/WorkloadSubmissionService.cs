using Grpc.Core;
using Google.Protobuf;
using Haby.Domain;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<WorkloadSubmissionReply> SubmitGeneration(GenerationRequest request, ServerCallContext context)
    {
        ApplicationDeploymentId deployment;
        DeploymentGeneration generation;
        try { deployment = ApplicationDeploymentId.Parse(request.DeploymentId); generation = DeploymentGeneration.Create(request.Generation); }
        catch (ArgumentException) { return new() { Outcome = "invalidRequest" }; }
        WorkloadSubmissionAudit.Started(submissionLogger, Actor, deployment.ToString(), generation.Value);
        var result = await OperationCall(() => workloadSubmission.SubmitAsync(deployment, generation, Actor, context.CancellationToken), context);
        WorkloadSubmissionAudit.Finished(submissionLogger, Actor, deployment.ToString(), generation.Value, result.Outcome, result.RequiresInspection);
        var reply = new WorkloadSubmissionReply { Outcome = result.Outcome, RequiresInspection = result.RequiresInspection };
        if (result.Progress is { } progress) reply.Progress = SubmissionSummary(progress);
        if (result.Stage is not null) reply.Stage = result.Stage;
        if (result.Subject is not null) reply.Subject = result.Subject;
        if (result.Reason is not null) reply.Reason = result.Reason;
        if (reply.CalculateSize() > 2097152) throw new RpcException(new Status(StatusCode.ResourceExhausted, "Submission response exceeds its bound."));
        return reply;
    }
    private static WorkloadSubmissionProgress SubmissionSummary(Haby.Application.WorkloadSubmissionProgress value)
    {
        var progress = new WorkloadSubmissionProgress
        {
            DeploymentId = value.Deployment,
            Generation = value.Generation,
            DesiredGeneration = value.DesiredGeneration,
            Submission = value.Submission,
            Completed = value.Completed,
            Total = value.Total
        };
        if (value.AppliedGeneration is { } applied) progress.AppliedGeneration = applied;
        if (value.ResourcesReady is { } resources) progress.ResourcesReadyId = resources.ToString();
        if (value.Submitted is { } submitted) progress.SubmittedId = submitted.ToString();
        foreach (var member in value.Members)
        {
            var item = new WorkloadSubmissionMember
            {
                Component = member.Component,
                Workload = member.Workload,
                Documents = member.Documents,
                Identity = member.Identity,
                Publication = member.Publication,
                Submission = member.Submission,
                RuntimeAuthority = member.RuntimeAuthority
            };
            if (member.OperationId is not null) item.OperationId = member.OperationId;
            progress.Members.Add(item);
        }
        return progress;
    }
}

internal static partial class WorkloadSubmissionAudit
{
    [LoggerMessage(2290, LogLevel.Information, "Operator submission requested: actor {Actor}, deployment {Deployment}, generation {Generation}")]
    internal static partial void Started(ILogger logger, string actor, string deployment, long generation);
    [LoggerMessage(2291, LogLevel.Information, "Operator submission response: actor {Actor}, deployment {Deployment}, generation {Generation}, outcome {Outcome}, requires inspection {RequiresInspection}")]
    internal static partial void Finished(ILogger logger, string actor, string deployment, long generation, string outcome, bool requiresInspection);
}
