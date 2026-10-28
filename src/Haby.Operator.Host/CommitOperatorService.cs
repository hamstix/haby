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
        var result = await committer.CommitAsync(change, plan, OperatorHostApplication.TechnicalActorId,
            context.CancellationToken);
        if (result is DurableCommitResult.Committed)
        {
            // Reread PostgreSQL timestamp precision so first response and retry are identical.
            var saved = await committer.FindCommitAsync(change, context.CancellationToken)
                ?? throw new InvalidOperationException("Committed desired state was not found.");
            return new CommitReply { Status = CommitStatus.Committed, Commit = Summary(saved) };
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
            : new CommitReply { Status = CommitStatus.Found, Commit = Summary(saved) };
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
            : new CommitReply { Status = CommitStatus.Found, DesiredGeneration = Summary(saved) };
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

    private static DesiredGenerationSummary Summary(DurableDesiredGeneration value) => new()
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
        ProviderAlias = value.Inputs.Alias.Value,
        ProviderInstanceId = value.Inputs.ProviderInstance.ToString(),
        ConfigurationRevision = value.Inputs.ConfigurationRevision.Value,
        ImplementationId = value.Inputs.ImplementationId
    };
}
