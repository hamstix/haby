using Haby.Domain;

namespace Haby.Application;

/// <summary>Read-only access to accepted immutable revisions.</summary>
public interface IManifestRevisionReader
{
    ManifestRevision? Find(ManifestRevisionRef reference);
}

/// <summary>Operator-owned Environment used by the in-memory planning boundary.</summary>
public sealed record EnvironmentRecord(EnvironmentRecordId RecordId, EnvironmentId Id, string DisplayName,
    long ConcurrencyToken);

/// <summary>An exact fake Provider selection, independent of the alias that selected it.</summary>
public sealed record ProviderResolution(string ProviderInstanceId, long ConfigurationRevision, string ImplementationId);

public sealed record ResolvedProviderAlias(ProviderAlias Alias, ProviderResolution Resolution);

/// <summary>One exact member. Other Applications are unaffected.</summary>
public sealed record DeploymentChangeSetMember(ApplicationRecordId ApplicationRecordId,
    ManifestRevisionRef ManifestRevisionRef, DeploymentPrecondition Precondition);

public abstract record DeploymentPrecondition
{
    private DeploymentPrecondition() { }
    public sealed record ExpectedAbsence : DeploymentPrecondition;
    public sealed record ExpectedGeneration(DeploymentGeneration Generation) : DeploymentPrecondition;
}

/// <summary>Immutable intent for one Application in one explicit Environment.</summary>
public sealed record DeploymentChangeSet(DeploymentChangeSetId Id, EnvironmentRecordId EnvironmentRecordId,
    DeploymentChangeSetMember Member, DateTimeOffset CreatedAt, string ActorId);

/// <summary>Exact typed inputs captured by planning, with no live alias pointer.</summary>
public sealed class DeploymentPlanningInputs : IEquatable<DeploymentPlanningInputs>
{
    public DeploymentPlanningInputs(EnvironmentRecordId environmentRecordId, EnvironmentId environmentId,
        ManifestRevisionRef manifestRevisionRef, IEnumerable<ResolvedProviderAlias> providers)
    {
        EnvironmentRecordId = environmentRecordId ?? throw new ArgumentNullException(nameof(environmentRecordId));
        EnvironmentId = environmentId ?? throw new ArgumentNullException(nameof(environmentId));
        ManifestRevisionRef = manifestRevisionRef ?? throw new ArgumentNullException(nameof(manifestRevisionRef));
        ArgumentNullException.ThrowIfNull(providers);
        var snapshot = providers.ToArray();
        if (snapshot.Any(item => item is null || item.Alias is null || item.Resolution is null))
            throw new ArgumentException("Provider resolutions must be non-null and complete.", nameof(providers));
        Providers = Array.AsReadOnly(snapshot.OrderBy(item => item.Alias.Value, StringComparer.Ordinal).ToArray());
    }
    public EnvironmentRecordId EnvironmentRecordId { get; }
    public EnvironmentId EnvironmentId { get; }
    public ManifestRevisionRef ManifestRevisionRef { get; }
    public IReadOnlyList<ResolvedProviderAlias> Providers { get; }
    public bool Equals(DeploymentPlanningInputs? other) => other is not null &&
        EnvironmentRecordId == other.EnvironmentRecordId && EnvironmentId == other.EnvironmentId &&
        ManifestRevisionRef == other.ManifestRevisionRef && Providers.SequenceEqual(other.Providers);
    public override bool Equals(object? obj) => obj is DeploymentPlanningInputs other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EnvironmentRecordId); hash.Add(EnvironmentId); hash.Add(ManifestRevisionRef);
        foreach (var provider in Providers) hash.Add(provider);
        return hash.ToHashCode();
    }
}

public sealed record DeploymentPlan(DeploymentPlanId Id, DeploymentChangeSetId ChangeSetId,
    EnvironmentRecordId EnvironmentRecordId, DeploymentChangeSetMember Member,
    IReadOnlyList<ApplicationRecordId> DependencyOrder, DeploymentPlanningInputs Inputs,
    string PlanningFingerprint, DateTimeOffset CreatedAt, string ActorId);

public enum DesiredPresence { Present, Absent }

public sealed record ApplicationDeployment(ApplicationDeploymentId Id, EnvironmentRecordId EnvironmentRecordId,
    ApplicationRecordId ApplicationRecordId, DeploymentGeneration DesiredGeneration,
    DeploymentGeneration? AppliedGeneration, long ConcurrencyToken);

/// <summary>Historical accepted desired state; Provider inputs are copied from its Plan.</summary>
public sealed record ApplicationDeploymentGeneration(ApplicationDeploymentId DeploymentId,
    DeploymentGeneration Generation, DesiredPresence Presence, ManifestRevisionRef ManifestRevisionRef,
    DeploymentPlanningInputs Inputs, DeploymentPlanId PlanId, DeploymentChangeSetId ChangeSetId,
    DateTimeOffset AcceptedAt, string ActorId);

public sealed record ChangeSetCommit(DeploymentChangeSetId ChangeSetId, DeploymentPlanId PlanId,
    DeploymentGeneration Generation, DateTimeOffset CommittedAt, string ActorId, bool NoOp);

public abstract record CreateChangeSetResult
{
    private CreateChangeSetResult() { }
    public sealed record Created(DeploymentChangeSet ChangeSet) : CreateChangeSetResult;
    public sealed record RevisionNotFound : CreateChangeSetResult;
    public sealed record RevisionNotOwnedByApplication : CreateChangeSetResult;
    public sealed record EnvironmentNotFound : CreateChangeSetResult;
}

public abstract record PlanChangeSetResult
{
    private PlanChangeSetResult() { }
    public sealed record Planned(DeploymentPlan Plan) : PlanChangeSetResult;
    public sealed record ChangeSetNotFound : PlanChangeSetResult;
    public sealed record AlreadyCommitted(ChangeSetCommit Commit) : PlanChangeSetResult;
    public sealed record ChangeSetStale(DeploymentChangeSetMember Member, DeploymentGeneration? ActualGeneration) : PlanChangeSetResult;
    public sealed record ProviderAliasNotResolved(ProviderAlias Alias) : PlanChangeSetResult;
    /// <summary>A dependency outside this checkpoint's one-Application resolver; no public error code is assigned.</summary>
    public sealed record UnsupportedDependency : PlanChangeSetResult;
}

public abstract record CommitDesiredStateResult
{
    private CommitDesiredStateResult() { }
    public sealed record Committed(ChangeSetCommit Commit) : CommitDesiredStateResult;
    public sealed record ChangeSetNotFound : CommitDesiredStateResult;
    public sealed record PlanNotFound : CommitDesiredStateResult;
    public sealed record PlanDoesNotBelongToChangeSet : CommitDesiredStateResult;
    public sealed record ChangeSetStale(DeploymentChangeSetMember Member, DeploymentGeneration? ActualGeneration) : CommitDesiredStateResult;
    public sealed record PlanStale : CommitDesiredStateResult;
}
