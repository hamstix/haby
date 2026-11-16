using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<CommitReply> CommitDesiredState(CommitDesiredStateRequest request, ServerCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        DeploymentChangeSetId change;
        DeploymentPlanId plan;
        try
        {
            change = DeploymentChangeSetId.Parse(request.ChangeSetId);
            plan = DeploymentPlanId.Parse(request.PlanId);
        }
        catch (ArgumentException) { return CommitResult(CommitStatus.InvalidRequest); }
        // The committer alone owns retry ordering, pinned inputs, locks and the transaction.
        var result = await deploymentAdmission.CommitAsync(change, plan, Actor,
            context.CancellationToken);
        if (result is DurableCommitResult.Committed)
        {
            // Reread PostgreSQL timestamp precision so first response and retry are identical.
            var saved = await committer.FindCommitAsync(change, context.CancellationToken)
                ?? throw new InvalidOperationException("Committed desired state was not found.");
            return await CommitSnapshotAsync(saved, CommitStatus.Committed, context.CancellationToken);
        }
        var reply = CommitResult(result switch
        {
            DurableCommitResult.ChangeSetNotFound => CommitStatus.ChangeSetNotFound,
            DurableCommitResult.PlanNotFound => CommitStatus.PlanNotFound,
            DurableCommitResult.PlanDoesNotBelongToChangeSet => CommitStatus.PlanDoesNotBelongToChangeSet,
            DurableCommitResult.Closed => CommitStatus.Closed,
            DurableCommitResult.Expired => CommitStatus.Expired,
            DurableCommitResult.ChangeSetStale => CommitStatus.ChangeSetStale,
            DurableCommitResult.PlanStale => CommitStatus.PlanStale,
            DurableCommitResult.PlanInputUnavailable => CommitStatus.PlanInputUnavailable,
            DurableCommitResult.UnsupportedPrecondition => CommitStatus.UnsupportedPrecondition,
            DurableCommitResult.UnsupportedShape => CommitStatus.UnsupportedShape,
            _ => throw new InvalidOperationException("Unknown commit outcome.")
        });
        if (result is DurableCommitResult.ChangeSetStale { ActualGeneration: { } actual })
            reply.ActualGeneration = actual.Value;
        return reply;
    }

    public override async Task<CommitReply> GetCommit(ChangeSetRequest request, ServerCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        DeploymentChangeSetId id;
        try { id = DeploymentChangeSetId.Parse(request.ChangeSetId); }
        catch (ArgumentException) { return CommitResult(CommitStatus.InvalidRequest); }
        var saved = await committer.FindCommitAsync(id, context.CancellationToken);
        return saved is null ? CommitResult(CommitStatus.CommitNotFound)
            : await CommitSnapshotAsync(saved, CommitStatus.Found, context.CancellationToken);
    }

    private async Task<CommitReply> CommitSnapshotAsync(DurableChangeSetCommit saved, CommitStatus status,
        CancellationToken cancellationToken)
    {
        // Project the exact accepted graph, never current aliases or a newly calculated Plan.
        var generation = await committer.FindGenerationAsync(saved.DeploymentId, saved.Generation, cancellationToken)
            ?? throw new InvalidOperationException("Committed desired generation was not found.");
        return new CommitReply { Status = status, Commit = Summary(saved), DesiredGeneration = await GenerationSummaryAsync(generation, cancellationToken), WorkloadStage = await StageSummaryAsync(generation, cancellationToken) };
    }

    public override async Task<CommitReply> GetGeneration(GenerationRequest request, ServerCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        ApplicationDeploymentId id;
        DeploymentGeneration generation;
        try
        {
            id = ApplicationDeploymentId.Parse(request.DeploymentId);
            generation = DeploymentGeneration.Create(request.Generation);
        }
        catch (ArgumentException) { return CommitResult(CommitStatus.InvalidRequest); }
        var saved = await committer.FindGenerationAsync(id, generation, context.CancellationToken);
        return saved is null ? CommitResult(CommitStatus.GenerationNotFound)
            : new CommitReply
            {
                Status = CommitStatus.Found,
                DesiredGeneration = await GenerationSummaryAsync(saved, context.CancellationToken),
                WorkloadStage = await StageSummaryAsync(saved, context.CancellationToken),
                Progress = await OperationCall(async () =>
                {
                    var progress = await generationRunner.ProgressAsync(id, generation, context.CancellationToken);
                    return progress is null ? null : Summary(progress);
                }, context)
            };
    }

    private static CommitReply CommitResult(CommitStatus status) => new() { Status = status };

    private static DurableCommitSummary Summary(DurableChangeSetCommit value) => new()
    {
        ChangeSetId = value.ChangeSetId.ToString(),
        PlanId = value.PlanId.ToString(),
        DeploymentId = value.DeploymentId.ToString(),
        Generation = value.Generation.Value,
        CommittedAtUtc = Utc(value.CommittedAt),
        TechnicalActorId = value.ActorId
    };

    private async Task<WorkloadAcceptanceStage?> StageSummaryAsync(DurableDesiredGeneration value, CancellationToken token)
    {
        var stage = value.Inputs.Providers.Entries.Keys.Any(x => x is ProviderInput.Workload)
            ? await deploymentAdmission.ReadStageAsync(value.DeploymentId, value.Generation, token) : null;
        if (stage is null) return null;
        var result = new WorkloadAcceptanceStage
        {
            Submission = stage.Submission switch
            {
                OperatorWorkloadSubmission.NotSubmitted => "notSubmitted",
                OperatorWorkloadSubmission.Unknown => "unknown",
                _ => throw new InvalidDataException("Unsupported submission summary.")
            }
        };
        if (stage.AppliedGeneration is { } applied) result.AppliedGeneration = applied;
        return result;
    }

    private async Task<DesiredGenerationSummary> GenerationSummaryAsync(DurableDesiredGeneration value, CancellationToken token)
    {
        var summary = new DesiredGenerationSummary
        {
            DeploymentId = value.DeploymentId.ToString(),
            Generation = value.Generation.Value,
            DesiredPresence = value.Presence.ToString(),
            ChangeSetId = value.ChangeSetId.ToString(),
            PlanId = value.PlanId.ToString(),
            AcceptedAtUtc = Utc(value.AcceptedAt),
            TechnicalActorId = value.ActorId,
            EnvironmentRecordId = value.Inputs.Environment.ToString(),
            EnvironmentId = value.Inputs.EnvironmentKey.Value,
            Reference = Reference(value.Inputs.Revision),
        };
        summary.WorkloadDescription = await WorkloadSummaryAsync(value.Inputs, token);
        summary.ProviderInputs.AddRange(ProviderInputSummaries(value.Inputs));
        summary.Operations.AddRange(OperationSummaries(value.Graph));
        var names = descriptions.Targets(value.Inputs, value.Graph);
        foreach (var operation in summary.Operations)
            if (names.SingleOrDefault(n => n.Operation.Value == operation.OperationId) is { } name) operation.TargetName = name.Name;
        summary.ReusedResources.AddRange(value.Graph.ReusedResources.Select(x => new ReusedResourceSummary
        { ResourceId = x.Resource.Value, ManagementId = x.ManagementId.ToString(), SourceGeneration = x.SourceGeneration, Reintroduced = x.Reintroduced }));
        summary.RetainedResources.AddRange(value.Graph.RetainedResources.Select(x => new ReusedResourceSummary
        { ResourceId = x.Resource.Value, ManagementId = x.ManagementId.ToString(), SourceGeneration = x.SourceGeneration }));
        return summary;
    }
}
