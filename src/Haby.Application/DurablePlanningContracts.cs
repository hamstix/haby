using Haby.Domain;

namespace Haby.Application;

/// <summary>Exact, non-secret inputs; the alias is retained only as selection provenance.</summary>
public sealed record DurablePlanningInputs(EnvironmentRecordId Environment, EnvironmentId EnvironmentKey,
    ManifestRevisionRef Revision, ProviderAlias Alias, ProviderInstanceId ProviderInstance,
    ProviderConfigurationRevisionNumber ConfigurationRevision, string ImplementationId);

/// <summary>A Plan is immutable; closure is stored separately from its original record.</summary>
public sealed record DurablePlan(DeploymentPlanId Id, DeploymentChangeSetId ChangeSetId,
    DurablePlanningInputs Inputs, string Fingerprint, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, string ActorId, DateTimeOffset? ClosedAt, string? ClosedByActorId);

public enum DurableChangeSetCreateStatus
{
    Created, EnvironmentNotFound, RevisionNotFound, RevisionNotOwnedByApplication,
    ExpectedGenerationUnsupported
}

public sealed record DurableChangeSetCreateResult(DurableChangeSetCreateStatus Status, DeploymentChangeSet? ChangeSet = null);

public enum DurablePlanStatus
{
    Planned, ChangeSetNotFound, ChangeSetStale, RevisionNotFound, AliasNotResolved,
    ProviderRevisionUnavailable, ProviderCapabilityMismatch, UnsupportedShape
}

public sealed record DurablePlanResult(DurablePlanStatus Status, DurablePlan? Plan = null);

public enum DurablePlanCloseStatus { Closed, AlreadyClosed, PlanNotFound }
public enum DurablePlanCurrentStatus
{
    Current, PlanNotFound, Closed, Expired, ChangeSetStale, PlanStale, PlanInputUnavailable
}

public interface IDurableDeploymentPlanner
{
    ValueTask<DurableChangeSetCreateResult> CreateChangeSetAsync(EnvironmentRecordId environment,
        ApplicationRecordId application, ManifestRevisionRef revision, DeploymentPrecondition precondition,
        string actorId, CancellationToken cancellationToken);
    ValueTask<DurablePlanResult> CreatePlanAsync(DeploymentChangeSetId changeSetId, string actorId,
        CancellationToken cancellationToken);
    ValueTask<DeploymentChangeSet?> FindChangeSetAsync(DeploymentChangeSetId id, CancellationToken cancellationToken);
    ValueTask<DurablePlan?> FindPlanAsync(DeploymentPlanId id, CancellationToken cancellationToken);
    ValueTask<DurablePlanCloseStatus> ClosePlanAsync(DeploymentPlanId id, string actorId,
        CancellationToken cancellationToken);
    ValueTask<DurablePlanCurrentStatus> CheckPlanAsync(DeploymentPlanId id, CancellationToken cancellationToken);
}
