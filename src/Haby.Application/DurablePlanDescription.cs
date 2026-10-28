using Haby.Domain;

namespace Haby.Application;

public enum PlanDescriptionStatus
{
    Available, RevisionNotFound, InputCorrupt, UnsupportedShape, PinnedInputMismatch, ActionDescriberUnavailable
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
    DurablePlanDescription Describe(ManifestRevision revision, DurablePlanningInputs inputs);
}

/// <summary>Reads the exact revision from trusted immutable storage and dispatches by the pinned implementation.
/// Structural validation does not authenticate historical content or detect arbitrary valid-content mutation.</summary>
public sealed class DescribeDurablePlan(IManifestRevisionQuery revisions, IEnumerable<IPlanActionDescriber> describers)
{
    private readonly IPlanActionDescriber[] _describers = describers.ToArray();

    public async ValueTask<DurablePlanDescription> ExecuteAsync(DurablePlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ManifestRevision? revision;
        try { revision = await revisions.FindAsync(plan.Inputs.Revision, cancellationToken); }
        catch (InvalidDataException) { return new(PlanDescriptionStatus.InputCorrupt); }
        if (revision is null) return new(PlanDescriptionStatus.RevisionNotFound);
        if (revision.Reference != plan.Inputs.Revision) return new(PlanDescriptionStatus.PinnedInputMismatch);
        var matches = _describers.Where(item => item.ImplementationId == plan.Inputs.ImplementationId).Take(2).ToArray();
        if (matches.Length != 1) return new(PlanDescriptionStatus.ActionDescriberUnavailable);
        return matches[0].Describe(revision, plan.Inputs);
    }
}
