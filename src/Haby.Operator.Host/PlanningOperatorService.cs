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
        if (request.Precondition != PlanningPrecondition.ExpectedAbsence)
            return Result(PlanningStatus.UnsupportedPrecondition);
        var environment = await environments.FindAsync(key, context.CancellationToken);
        if (environment is null) return Result(PlanningStatus.EnvironmentNotFound);
        var result = await planner.CreateChangeSetAsync(environment, reference.ApplicationRecordId, reference,
            new DeploymentPrecondition.ExpectedAbsence(), OperatorHostApplication.TechnicalActorId, context.CancellationToken);
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
        var result = await planner.CreatePlanAsync(id, OperatorHostApplication.TechnicalActorId, context.CancellationToken);
        return new PlanningReply
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
                _ => throw new InvalidOperationException("Unknown Plan outcome.")
            },
            Plan = result.Plan is null ? null : Summary(
                await planner.FindPlanAsync(result.Plan.Id, context.CancellationToken)
                    ?? throw new InvalidOperationException("Committed Plan was not found."))
        };
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
        var status = close
            ? (await planner.ClosePlanAsync(id, OperatorHostApplication.TechnicalActorId, context.CancellationToken)) switch
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
        var plan = await planner.FindPlanAsync(id, context.CancellationToken);
        return new PlanningReply { Status = status, Plan = plan is null ? null : Summary(plan) };
    }

    private static PlanningReply Result(PlanningStatus status) => new() { Status = status };

    private static DurableChangeSetSummary Summary(DeploymentChangeSet value) => new()
    {
        ChangeSetId = value.Id.ToString(),
        EnvironmentRecordId = value.EnvironmentRecordId.ToString(),
        Reference = Reference(value.Member.ManifestRevisionRef),
        Precondition = PlanningPrecondition.ExpectedAbsence,
        CreatedAtUtc = Utc(value.CreatedAt),
        TechnicalActorId = value.ActorId
    };

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
        if (value.ClosedAt is { } closed) result.ClosedAtUtc = Utc(closed);
        if (value.ClosedByActorId is { } actor) result.ClosedByTechnicalActorId = actor;
        return result;
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O",
        System.Globalization.CultureInfo.InvariantCulture);
}
