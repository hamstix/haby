using Haby.Domain;

namespace Haby.Application;

public enum PlanDescriptionStatus
{
    Available, RevisionNotFound, InputCorrupt, UnsupportedShape, PinnedInputMismatch, ActionDescriberUnavailable, OperationGraph
}
public enum IntendedActionKind { Create }

/// <summary>Provider-neutral intended work, never an observation or execution guarantee.</summary>
public sealed record IntendedPlanAction(IntendedActionKind Kind, ManifestLocalId ResourceId,
    ResourceTypeId ResourceType, IntendedResourceTarget Target);
public sealed record IntendedResourceTarget(string Name);
public sealed record DurablePlanDescription(PlanDescriptionStatus Status, IntendedPlanAction? Action = null);

/// <summary>A build-time registered, pure projection of an exact implementation's supported actions.
/// It must emit only validated non-secret review fields and must not perform external I/O.</summary>
public interface IPlanActionDescriber
{
    string ImplementationId { get; }
    DurablePlanDescription Describe(ManifestRevision revision, DurablePlanningInputs inputs, DurableOperationGraph graph);
}

public sealed record PlannedOperationTarget(OperationId Operation, string Name);
public interface IPlanOperationTargetDescriber
{
    IReadOnlyList<PlannedOperationTarget> DescribeTargets(DurableOperationGraph graph);
}

/// <summary>Reads the exact revision from trusted immutable storage and dispatches by the pinned implementation.
/// Structural validation does not authenticate historical content or detect arbitrary valid-content mutation.</summary>
public sealed class DescribeDurablePlan(IManifestRevisionQuery revisions, IEnumerable<IPlanActionDescriber> describers)
{
    private readonly IPlanActionDescriber[] _describers = describers.ToArray();

    public IReadOnlyList<PlannedOperationTarget> Targets(DurablePlan plan) => Targets(plan.Inputs, plan.Graph);
    public IReadOnlyList<PlannedOperationTarget> Targets(DurablePlanningInputs inputs, DurableOperationGraph graph)
    {
        graph.ValidateReferences(inputs);
        return graph.Nodes.GroupBy(n => inputs.Providers.Resource(n.ResourceId).Implementation.Value)
            .SelectMany(group => Targets(group.Key, graph).Where(t => group.Any(n => n.Id == t.Operation)))
            .OrderBy(t => t.Operation.Value, StringComparer.Ordinal).ToArray();
    }
    private IReadOnlyList<PlannedOperationTarget> Targets(string implementation, DurableOperationGraph graph) =>
        _describers.Where(d => d.ImplementationId == implementation).Take(2).ToArray() is [IPlanOperationTargetDescriber names]
            ? names.DescribeTargets(graph) : [];

    public async ValueTask<DurablePlanDescription> ExecuteAsync(DurablePlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ManifestRevision? revision;
        try { revision = await revisions.FindAsync(plan.Inputs.Revision, cancellationToken); }
        catch (InvalidDataException) { return new(PlanDescriptionStatus.InputCorrupt); }
        if (revision is null) return new(PlanDescriptionStatus.RevisionNotFound);
        if (revision.Reference != plan.Inputs.Revision) return new(PlanDescriptionStatus.PinnedInputMismatch);
        if (!revision.Declaration.Equals(plan.Inputs.Providers.Declaration)) return new(PlanDescriptionStatus.PinnedInputMismatch);
        if (plan.Graph?.Baseline is not null) return new(PlanDescriptionStatus.OperationGraph);
        // The existing intended-action field is deliberately bounded to one Resource.
        // Larger exact sets are described by their keyed inputs and operation inventory.
        if (plan.Inputs.Providers.Entries.Count == 0) return new(PlanDescriptionStatus.UnsupportedShape);
        if (plan.Inputs.Providers.Entries.Count != 1 || plan.Inputs.Providers.Entries.Keys.Any(x => x is ProviderInput.Workload))
            return new(PlanDescriptionStatus.OperationGraph);
        var implementations = plan.Inputs.Providers.Entries.Values.Select(x => x.Implementation.Value).Distinct().ToArray();
        if (implementations.Length == 0) return new(PlanDescriptionStatus.UnsupportedShape);
        if (implementations is not [var implementation]) return new(PlanDescriptionStatus.OperationGraph);
        var matches = _describers.Where(item => item.ImplementationId == implementation).Take(2).ToArray();
        if (matches.Length != 1) return new(PlanDescriptionStatus.ActionDescriberUnavailable);
        return matches[0].Describe(revision, plan.Inputs, plan.Graph!);
    }
}
