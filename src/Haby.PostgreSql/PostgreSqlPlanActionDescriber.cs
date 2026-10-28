using Haby.Application;
using Haby.Domain;

namespace Haby.PostgreSql;

/// <summary>Pure PostgreSQL checkpoint description; no Provider or secret access.</summary>
public sealed class PostgreSqlPlanActionDescriber : IPlanActionDescriber
{
    public string ImplementationId => PostgreSqlDatabaseCheckpointCapability.ImplementationId;

    public DurablePlanDescription Describe(ManifestRevision revision, DurablePlanningInputs inputs)
    {
        if (!CheckpointDatabaseShape.IsSupported(revision.Declaration.Spec))
            return new(PlanDescriptionStatus.UnsupportedShape);
        var resource = revision.Declaration.Spec.Resources.Single();
        if (resource.Value.ProviderRef != inputs.Alias || inputs.ImplementationId != ImplementationId)
            return new(PlanDescriptionStatus.PinnedInputMismatch);
        var detail = new PostgreSqlDatabaseActionDetail(((ConfigurationString)resource.Value.Properties.Members["name"]).Value);
        return new(PlanDescriptionStatus.Available, new IntendedPlanAction(IntendedActionKind.Create,
            resource.Key, resource.Value.Type, new IntendedResourceTarget(detail.DatabaseName)));
    }

    private sealed record PostgreSqlDatabaseActionDetail(string DatabaseName);
}
