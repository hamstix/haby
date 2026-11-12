using Haby.Application;
using Haby.Domain;

namespace Haby.PostgreSql;

/// <summary>Pure PostgreSQL checkpoint description; no Provider or secret access.</summary>
public sealed class PostgreSqlPlanActionDescriber : IPlanActionDescriber, IPlanOperationTargetDescriber
{
    public IReadOnlyList<PlannedOperationTarget> DescribeTargets(DurableOperationGraph graph) => graph.Nodes.Select(node =>
    {
        if (PostgreSqlApplicationLoginAction.Decode(node) is { } login) return new PlannedOperationTarget(node.Id, login.RoleName);
        if (node.Action == PostgreSqlDatabaseCreateAction.RegistrationKey && node.Input.Format == PostgreSqlDatabaseCreateAction.Format)
        {
            var input = System.Text.Json.JsonSerializer.Deserialize(node.Input.Json, PostgreSqlActionJsonContext.Default.PostgreSqlDatabaseActionInput);
            if (input?.DatabaseName is { } name && PostgreSqlDatabaseGateway.SafeDatabase.IsMatch(name)) return new PlannedOperationTarget(node.Id, name);
        }
        return null;
    }).OfType<PlannedOperationTarget>().ToArray();
    public string ImplementationId => PostgreSqlDatabaseCheckpointCapability.ImplementationId;

    public DurablePlanDescription Describe(ManifestRevision revision, DurablePlanningInputs inputs, DurableOperationGraph graph)
    {
        if (!CheckpointDatabaseShape.IsSupported(revision.Declaration.Spec))
            return new(PostgreSqlPairResourceAdmission.DescribeGraph(revision.Declaration.Spec, inputs, graph));
        var resource = revision.Declaration.Spec.Resources.Single();
        if (resource.Value.ProviderRef != inputs.Providers.Resource(resource.Key).Alias || inputs.Providers.Resource(resource.Key).Implementation.Value != ImplementationId)
            return new(PlanDescriptionStatus.PinnedInputMismatch);
        var name = DescribeTargets(graph).SingleOrDefault(x => x.Operation.Value == resource.Key.Value + "-create")?.Name;
        if (name is null) return new(PlanDescriptionStatus.PinnedInputMismatch);
        var detail = new PostgreSqlDatabaseActionDetail(name);
        return new(PlanDescriptionStatus.Available, new IntendedPlanAction(IntendedActionKind.Create,
            resource.Key, resource.Value.Type, new IntendedResourceTarget(detail.DatabaseName)));
    }

    private sealed record PostgreSqlDatabaseActionDetail(string DatabaseName);
}
