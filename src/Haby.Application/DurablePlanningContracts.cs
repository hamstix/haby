using Haby.Domain;

namespace Haby.Application;

/// <summary>Exact non-secret inputs; aliases retain selection provenance under each declaration key.</summary>
public sealed record DurablePlanningInputs(EnvironmentRecordId Environment, EnvironmentId EnvironmentKey,
    ManifestRevisionRef Revision, ExactProviderResolutionSet Providers)
{
    public ConfigurationPlanningSnapshot? Configuration { get; init; }
    private ResourceImportIntent? import;
    public ResourceAcquisitionIntent Acquisitions { get; init; } = ResourceAcquisitionIntent.Provision(Providers.Declaration.Spec.Resources.Keys);
    public ResourceImportIntent? Import
    {
        get => import;
        init { import = value; if (value is not null) Acquisitions = ResourceAcquisitionIntent.Import(value); }
    }

    public void Validate()
    {
        Acquisitions.Validate(Providers.Declaration.Spec);
        if (Import is not null && !Acquisitions.Equals(ResourceAcquisitionIntent.Import(Import)))
            throw new InvalidDataException("Import intake differs from acquisition set.");
        if (Providers.Environment != Environment || Configuration is { } frozen &&
            (frozen.Revision != Revision || !frozen.Manifest.Equals(Providers.Declaration)))
            throw new InvalidDataException("Durable Provider inputs do not match the accepted declaration.");
        foreach (var entry in Providers.Entries.Values.Where(x => x.Input is ProviderInput.Resource))
            _ = entry.ResourceNaming();
    }
}

/// <summary>A Plan is immutable; closure is stored separately from its original record.</summary>
public sealed record DurablePlan(DeploymentPlanId Id, DeploymentChangeSetId ChangeSetId,
    DurablePlanningInputs Inputs, string Fingerprint, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, string ActorId, DateTimeOffset? ClosedAt, string? ClosedByActorId)
{
    public DurableOperationGraph Graph { get; init; } = null!;
}

public enum DurableChangeSetCreateStatus
{
    Created, EnvironmentNotFound, RevisionNotFound, RevisionNotOwnedByApplication,
    ExpectedGenerationUnsupported, ImportNotSupported, InvalidInput, ReferenceNotSupported,
    AliasNotResolved, ProviderRevisionUnavailable, ProviderCapabilityMismatch, UnsupportedShape
}

public sealed record DurableChangeSetCreateResult(DurableChangeSetCreateStatus Status, DeploymentChangeSet? ChangeSet = null);

public enum DurablePlanStatus
{
    Planned, ChangeSetNotFound, ChangeSetStale, RevisionNotFound, AliasNotResolved,
    ProviderRevisionUnavailable, ProviderCapabilityMismatch, UnsupportedShape, ConfigurationRefused, PlanInputUnavailable, ImportNotSupported, ReferenceNotSupported
}

public sealed record DurablePlanResult(DurablePlanStatus Status, DurablePlan? Plan = null, ProfileExpansionResult? ConfigurationReport = null)
{
    internal WorkloadUpdateRefusal? UpdateRefusal { get; init; }
}

public enum DurablePlanCloseStatus { Closed, AlreadyClosed, PlanNotFound }
public enum DurablePlanCurrentStatus
{
    Current, PlanNotFound, Closed, Expired, ChangeSetStale, PlanStale, PlanInputUnavailable
}

public interface IDurableDeploymentPlanner
{
    /// <summary>Explicit Provision/Reference intake. Initial sets cover every Resource; additive
    /// Reference transitions supply new Resources only and retain server-derived baseline inputs.
    /// Protected Import has its own intake.</summary>
    ValueTask<DurableChangeSetCreateResult> CreateAcquisitionChangeSetAsync(EnvironmentRecordId environment,
        ApplicationRecordId application, ManifestRevisionRef revision, DeploymentPrecondition precondition,
        ResourceAcquisitionIntent acquisitions, string actor, CancellationToken token) =>
        ValueTask.FromResult(new DurableChangeSetCreateResult(DurableChangeSetCreateStatus.ReferenceNotSupported));

    ValueTask<DurableChangeSetCreateResult> CreateImportChangeSetAsync(EnvironmentRecordId environment,
        ApplicationRecordId application, ManifestRevisionRef revision, ResourceImportIntent intent,
        string actorId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DurableChangeSetCreateResult(DurableChangeSetCreateStatus.ImportNotSupported));

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

/// <summary>An immutable desired generation and its exact non-secret Plan inputs.</summary>
public sealed record DurableDesiredGeneration(ApplicationDeploymentId DeploymentId,
    DeploymentGeneration Generation, DesiredPresence Presence, DurablePlanningInputs Inputs, DeploymentPlanId PlanId,
    DeploymentChangeSetId ChangeSetId, DateTimeOffset AcceptedAt, string ActorId)
{
    public DurableOperationGraph Graph { get; init; } = null!;
}

public sealed record DurableChangeSetCommit(DeploymentChangeSetId ChangeSetId, DeploymentPlanId PlanId,
    ApplicationDeploymentId DeploymentId, DeploymentGeneration Generation, DateTimeOffset CommittedAt,
    string ActorId);

public abstract record DurableCommitResult
{
    private DurableCommitResult() { }
    public sealed record Committed(DurableChangeSetCommit Commit) : DurableCommitResult;
    public sealed record ChangeSetNotFound : DurableCommitResult;
    public sealed record PlanNotFound : DurableCommitResult;
    public sealed record PlanDoesNotBelongToChangeSet : DurableCommitResult;
    public sealed record Closed : DurableCommitResult;
    public sealed record Expired : DurableCommitResult;
    public sealed record ChangeSetStale(DeploymentGeneration? ActualGeneration) : DurableCommitResult;
    public sealed record PlanStale : DurableCommitResult;
    public sealed record PlanInputUnavailable : DurableCommitResult;
    public sealed record UnsupportedPrecondition : DurableCommitResult;
    public sealed record UnsupportedShape : DurableCommitResult;
}

/// <summary>Internal durable desired-state boundary; it does not execute Providers.</summary>
public interface IDurableDesiredStateCommitter
{
    ValueTask<DurableCommitResult> CommitAsync(DeploymentChangeSetId changeSetId,
        DeploymentPlanId planId, string actorId, CancellationToken cancellationToken);
    ValueTask<DurableChangeSetCommit?> FindCommitAsync(DeploymentChangeSetId changeSetId,
        CancellationToken cancellationToken);
    ValueTask<DurableDesiredGeneration?> FindGenerationAsync(ApplicationDeploymentId deploymentId,
        DeploymentGeneration generation, CancellationToken cancellationToken);
}
