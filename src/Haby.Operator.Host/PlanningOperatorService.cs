using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<PlanningReply> CreateChangeSet(CreateChangeSetRequest request, ServerCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        EnvironmentId key;
        ManifestRevisionRef reference;
        try
        {
            key = EnvironmentId.Create(request.EnvironmentId);
            if (request.Reference is null) return Result(PlanningStatus.InvalidRequest);
            reference = ManifestRevisionRef.Create(ApplicationRecordId.Parse(request.Reference.ApplicationRecordId),
                ManifestRevisionNumber.Create(request.Reference.RevisionNumber));
        }
        catch (ArgumentException) { return Result(PlanningStatus.InvalidRequest); }
        DeploymentPrecondition precondition;
        if (request.Precondition == PlanningPrecondition.ExpectedAbsence && !request.HasExpectedGeneration)
            precondition = new DeploymentPrecondition.ExpectedAbsence();
        else if (request.Precondition == PlanningPrecondition.ExpectedGeneration && request.HasExpectedGeneration && request.ExpectedGeneration > 0)
            precondition = new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(request.ExpectedGeneration));
        else return Result(PlanningStatus.InvalidRequest);
        var environment = await environments.FindAsync(key, context.CancellationToken);
        if (environment is null) return Result(PlanningStatus.EnvironmentNotFound);
        var result = await planner.CreateChangeSetAsync(environment, reference.ApplicationRecordId, reference,
            precondition, Actor, context.CancellationToken);
        return new PlanningReply
        {
            Status = result.Status switch
            {
                DurableChangeSetCreateStatus.Created => PlanningStatus.Created,
                DurableChangeSetCreateStatus.EnvironmentNotFound => PlanningStatus.EnvironmentNotFound,
                DurableChangeSetCreateStatus.RevisionNotFound => PlanningStatus.RevisionNotFound,
                DurableChangeSetCreateStatus.RevisionNotOwnedByApplication => PlanningStatus.RevisionNotOwnedByApplication,
                DurableChangeSetCreateStatus.ExpectedGenerationUnsupported => PlanningStatus.UnsupportedPrecondition,
                _ => throw new InvalidOperationException("Unknown ChangeSet outcome.")
            },
            // Read committed values, including the catalog's timestamp precision.
            ChangeSet = result.ChangeSet is null ? null : Summary(
                await planner.FindChangeSetAsync(result.ChangeSet.Id, context.CancellationToken)
                    ?? throw new InvalidOperationException("Committed ChangeSet was not found."))
        };
    }

    public override async Task<PlanningReply> GetChangeSet(ChangeSetRequest request, ServerCallContext context)
    {
        DeploymentChangeSetId id;
        try { id = DeploymentChangeSetId.Parse(request.ChangeSetId); }
        catch (ArgumentException) { return Result(PlanningStatus.InvalidRequest); }
        var value = await planner.FindChangeSetAsync(id, context.CancellationToken);
        return value is null ? Result(PlanningStatus.ChangeSetNotFound)
            : new PlanningReply { Status = PlanningStatus.Found, ChangeSet = Summary(value) };
    }

    public override async Task<PlanningReply> CreatePlan(ChangeSetRequest request, ServerCallContext context)
    {
        DeploymentChangeSetId id;
        try { id = DeploymentChangeSetId.Parse(request.ChangeSetId); }
        catch (ArgumentException) { return Result(PlanningStatus.InvalidRequest); }
        var result = await planner.CreatePlanAsync(id, Actor, context.CancellationToken);
        var reply = new PlanningReply
        {
            Status = result.Status switch
            {
                DurablePlanStatus.Planned => PlanningStatus.Planned,
                DurablePlanStatus.ChangeSetNotFound => PlanningStatus.ChangeSetNotFound,
                DurablePlanStatus.ChangeSetStale => PlanningStatus.ChangeSetStale,
                DurablePlanStatus.RevisionNotFound => PlanningStatus.RevisionNotFound,
                DurablePlanStatus.AliasNotResolved => PlanningStatus.AliasNotResolved,
                DurablePlanStatus.ProviderRevisionUnavailable => PlanningStatus.ProviderRevisionUnavailable,
                DurablePlanStatus.ProviderCapabilityMismatch => PlanningStatus.ProviderCapabilityMismatch,
                DurablePlanStatus.UnsupportedShape => PlanningStatus.UnsupportedShape,
                DurablePlanStatus.ConfigurationRefused => PlanningStatus.ConfigurationRefused,
                DurablePlanStatus.PlanInputUnavailable => PlanningStatus.PlanInputUnavailable,
                _ => throw new InvalidOperationException("Unknown Plan outcome.")
            },
            Plan = result.Plan is null ? null : await PlanSummaryAsync(
                await planner.FindPlanAsync(result.Plan.Id, context.CancellationToken)
                    ?? throw new InvalidOperationException("Committed Plan was not found."), context.CancellationToken)
        };
        if (result.ConfigurationReport is { } report)
            reply.ConfigurationErrorCodes.AddRange(report.Diagnostics.Select(x => x.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return reply;
    }

    public override Task<PlanningReply> GetPlan(PlanRequest request, ServerCallContext context) =>
        ReadPlan(request, context, close: false);

    public override Task<PlanningReply> CheckPlan(PlanRequest request, ServerCallContext context) =>
        ReadPlan(request, context, close: false);

    public override Task<PlanningReply> ClosePlan(PlanRequest request, ServerCallContext context) =>
        ReadPlan(request, context, close: true);

    private async Task<PlanningReply> ReadPlan(PlanRequest request, ServerCallContext context, bool close)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        DeploymentPlanId id;
        try { id = DeploymentPlanId.Parse(request.PlanId); }
        catch (ArgumentException) { return Result(PlanningStatus.InvalidRequest); }
        // Refuse unreadable saved state before checking it. A typed unavailable result
        // must not be followed by another decode of the same unsupported Plan.
        DurablePlan? plan;
        try { plan = await planner.FindPlanAsync(id, context.CancellationToken); }
        catch (InvalidDataException) { return Result(PlanningStatus.PlanInputUnavailable); }
        var status = close
            ? (await planner.ClosePlanAsync(id, Actor, context.CancellationToken)) switch
            {
                DurablePlanCloseStatus.Closed => PlanningStatus.Closed,
                DurablePlanCloseStatus.AlreadyClosed => PlanningStatus.AlreadyClosed,
                DurablePlanCloseStatus.PlanNotFound => PlanningStatus.PlanNotFound,
                _ => throw new InvalidOperationException("Unknown closure outcome.")
            }
            : (await planner.CheckPlanAsync(id, context.CancellationToken)) switch
            {
                DurablePlanCurrentStatus.Current => PlanningStatus.Current,
                DurablePlanCurrentStatus.PlanNotFound => PlanningStatus.PlanNotFound,
                DurablePlanCurrentStatus.Closed => PlanningStatus.Closed,
                DurablePlanCurrentStatus.Expired => PlanningStatus.Expired,
                DurablePlanCurrentStatus.ChangeSetStale => PlanningStatus.ChangeSetStale,
                DurablePlanCurrentStatus.PlanStale => PlanningStatus.PlanStale,
                DurablePlanCurrentStatus.PlanInputUnavailable => PlanningStatus.PlanInputUnavailable,
                _ => throw new InvalidOperationException("Unknown Plan status.")
            };
        if (close)
        {
            // Closure is a separate record: return its committed timestamp and actor.
            try { plan = await planner.FindPlanAsync(id, context.CancellationToken); }
            catch (InvalidDataException) { return Result(PlanningStatus.PlanInputUnavailable); }
        }
        return new PlanningReply { Status = status, Plan = plan is null ? null : await PlanSummaryAsync(plan, context.CancellationToken) };
    }

    private static PlanningReply Result(PlanningStatus status) => new() { Status = status };

    private static DurableChangeSetSummary Summary(DeploymentChangeSet value)
    {
        var result = new DurableChangeSetSummary
        {
            ChangeSetId = value.Id.ToString(),
            EnvironmentRecordId = value.EnvironmentRecordId.ToString(),
            Reference = Reference(value.Member.ManifestRevisionRef),
            Precondition = value.Member.Precondition is DeploymentPrecondition.ExpectedAbsence ? PlanningPrecondition.ExpectedAbsence : PlanningPrecondition.ExpectedGeneration,
            CreatedAtUtc = Utc(value.CreatedAt),
            TechnicalActorId = value.ActorId
        };
        if (value.Member.Precondition is DeploymentPrecondition.ExpectedGeneration expected) result.ExpectedGeneration = expected.Generation.Value;
        return result;
    }

    private async Task<DurablePlanSummary> PlanSummaryAsync(DurablePlan plan, CancellationToken cancellationToken)
    {
        var summary = Summary(plan);
        var description = await descriptions.ExecuteAsync(plan, cancellationToken);
        summary.IntendedWork = new PlanDescription
        {
            Status = description.Status switch
            {
                Application.PlanDescriptionStatus.Available => Contracts.PlanDescriptionStatus.Available,
                Application.PlanDescriptionStatus.RevisionNotFound => Contracts.PlanDescriptionStatus.RevisionNotFound,
                Application.PlanDescriptionStatus.InputCorrupt => Contracts.PlanDescriptionStatus.InputCorrupt,
                Application.PlanDescriptionStatus.OperationGraph => Contracts.PlanDescriptionStatus.OperationGraph,
                Application.PlanDescriptionStatus.UnsupportedShape => Contracts.PlanDescriptionStatus.UnsupportedShape,
                Application.PlanDescriptionStatus.PinnedInputMismatch => Contracts.PlanDescriptionStatus.PinnedInputMismatch,
                Application.PlanDescriptionStatus.ActionDescriberUnavailable => Contracts.PlanDescriptionStatus.ActionDescriberUnavailable,
                _ => throw new InvalidOperationException("Unknown description outcome.")
            }
        };
        if (description.Action is { } action)
            summary.IntendedWork.Action = new Contracts.IntendedPlanAction
            {
                Kind = action.Kind == Application.IntendedActionKind.Create ? Contracts.IntendedActionKind.Create
                    : throw new InvalidOperationException("Unknown intended action."),
                ResourceId = action.ResourceId.Value,
                ResourceType = action.ResourceType.Value,
                Target = new IntendedActionTarget { Name = action.Target.Name }
            };
        return summary;
    }

    private static DurablePlanSummary Summary(DurablePlan value)
    {
        var result = new DurablePlanSummary
        {
            PlanId = value.Id.ToString(),
            ChangeSetId = value.ChangeSetId.ToString(),
            EnvironmentRecordId = value.Inputs.Environment.ToString(),
            EnvironmentId = value.Inputs.EnvironmentKey.Value,
            Reference = Reference(value.Inputs.Revision),
            ProviderAlias = value.Inputs.Alias.Value,
            ProviderInstanceId = value.Inputs.ProviderInstance.ToString(),
            ConfigurationRevision = value.Inputs.ConfigurationRevision.Value,
            ImplementationId = value.Inputs.ImplementationId,
            Fingerprint = value.Fingerprint,
            CreatedAtUtc = Utc(value.CreatedAt),
            ExpiresAtUtc = Utc(value.ExpiresAt),
            TechnicalActorId = value.ActorId
        };
        result.Operations.AddRange(OperationSummaries(value.Graph));
        result.ReusedResources.AddRange(value.Graph.ReusedResources.Select(x => new ReusedResourceSummary
        { ResourceId = x.Resource.Value, ManagementId = x.ManagementId.ToString(), SourceGeneration = x.SourceGeneration }));
        if (value.ClosedAt is { } closed) result.ClosedAtUtc = Utc(closed);
        if (value.ClosedByActorId is { } actor) result.ClosedByTechnicalActorId = actor;
        return result;
    }

    private static IEnumerable<PlannedOperationSummary> OperationSummaries(DurableOperationGraph graph) =>
        graph.Nodes.Select(node =>
        {
            var summary = new PlannedOperationSummary
            {
                OperationId = node.Id.Value,
                ResourceId = node.ResourceId.Value,
                ActionContract = node.Action.ActionContract.Value
            };
            summary.Dependencies.AddRange(node.Dependencies.Select(d => d.Value));
            return summary;
        });

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O",
        System.Globalization.CultureInfo.InvariantCulture);
}
