using System.Text.RegularExpressions;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Application;

public enum PlanDescriptionStatus { Available, RevisionNotFound, InputCorrupt, UnsupportedShape, PinnedInputMismatch }
public enum IntendedActionKind { Create }

/// <summary>Provider-neutral intended work, never an observation or execution guarantee.</summary>
public sealed record IntendedPlanAction(IntendedActionKind Kind, ManifestLocalId ResourceId,
    ResourceTypeId ResourceType, IntendedActionDetail Detail);
public abstract record IntendedActionDetail
{
    private IntendedActionDetail() { }
    public sealed record PostgreSqlDatabase(string DatabaseName) : IntendedActionDetail;
}
public sealed record DurablePlanDescription(PlanDescriptionStatus Status, IntendedPlanAction? Action = null);

/// <summary>Reads only the exact immutable revision. No alias, Provider or secret resolution.</summary>
public sealed class DescribeDurablePlan(IManifestRevisionQuery revisions)
{
    public async ValueTask<DurablePlanDescription> ExecuteAsync(DurablePlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ManifestRevision? revision;
        try { revision = await revisions.FindAsync(plan.Inputs.Revision, cancellationToken); }
        catch (InvalidDataException) { return new(PlanDescriptionStatus.InputCorrupt); }
        if (revision is null) return new(PlanDescriptionStatus.RevisionNotFound);
        if (revision.Reference != plan.Inputs.Revision) return new(PlanDescriptionStatus.PinnedInputMismatch);
        if (!CheckpointDatabaseShape.IsSupported(revision.Declaration.Spec))
            return new(PlanDescriptionStatus.UnsupportedShape);
        var resource = revision.Declaration.Spec.Resources.Single();
        if (resource.Value.ProviderRef != plan.Inputs.Alias ||
            plan.Inputs.ImplementationId != PostgreSqlDatabaseCheckpointCapability.ImplementationId)
            return new(PlanDescriptionStatus.PinnedInputMismatch);
        var name = (ConfigurationString)resource.Value.Properties.Members["name"];
        return new(PlanDescriptionStatus.Available, new IntendedPlanAction(IntendedActionKind.Create,
            resource.Key, resource.Value.Type, new IntendedActionDetail.PostgreSqlDatabase(name.Value)));
    }
}

/// <summary>The accepted durable checkpoint shape and name rules, shared with planning and commit.</summary>
public static class CheckpointDatabaseShape
{
    private static readonly Regex SafeDatabase = new(@"\A[a-z][a-z0-9_]{0,62}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool IsSupported(NormalizedManifestSpec spec) =>
        spec.Resources.Count == 1 && spec.Components.Count == 0 && spec.Exports.Count == 0 &&
        spec.Parameters.Count == 0 && IsSupportedResource(spec.Resources.Single().Value);

    public static bool IsSupportedResource(NormalizedResource resource) =>
        resource.Type == PostgreSqlDatabaseContract.Id && resource.Properties.Members.Count == 1 &&
        resource.Properties.Members.TryGetValue("name", out var value) &&
        value is ConfigurationString name && SafeDatabase.IsMatch(name.Value);
}
