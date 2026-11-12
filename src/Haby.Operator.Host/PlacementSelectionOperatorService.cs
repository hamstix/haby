using Grpc.Core;
using Haby.Application;
using Haby.Operator.Contracts;
using Target = Haby.Application.PlacementSelectionTarget;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override Task<PlacementSelectionReply> GetPlacementSelection(GetPlacementSelectionRequest request, ServerCallContext context)
        => SafeSelection(async () =>
        {
            Target target;
            try { target = ReadSelectionTarget(request.Target); }
            catch (ArgumentException) { return new(PlacementSelectionOutcome.InvalidContext); }
            return await placementSelections.GetAsync(request.Environment, request.ProviderAlias, target, context.CancellationToken);
        }, request.Environment, context.CancellationToken);
    public override Task<PlacementSelectionReply> ApplyPlacementSelection(ApplyPlacementSelectionRequest request, ServerCallContext context)
        => SafeSelection(async () =>
        {
            Target target; PlacementSelectionPrecondition precondition;
            try
            {
                target = ReadSelectionTarget(request.Target);
                precondition = (request.HasExpectedAbsence, request.HasExpectedToken) switch
                {
                    (true, false) when request.ExpectedAbsence => new PlacementSelectionPrecondition.ExpectedAbsence(),
                    (false, true) => new PlacementSelectionPrecondition.ExpectedToken(PlacementSelectionToken.Parse(request.ExpectedToken)),
                    _ => throw new ArgumentException("Invalid precondition.")
                };
            }
            catch (ArgumentException) { return new(PlacementSelectionOutcome.InvalidContext); }
            return await placementSelections.ApplyAsync(request.Environment, request.ProviderAlias, target, request.PlacementName, request.Revision,
                request.HasApplicationVersion ? request.ApplicationVersion : null, precondition, Actor, context.CancellationToken);
        }, request.Environment, context.CancellationToken);
    private static Target ReadSelectionTarget(Haby.Operator.Contracts.PlacementSelectionTarget? target)
    {
        if (target is null || !target.Equals(new Haby.Operator.Contracts.PlacementSelectionTarget
        { Kind = target.Kind, Selector = target.Selector, ApplicationId = target.ApplicationId, ComponentRef = target.ComponentRef }))
            throw new ArgumentException("Invalid target.");
        return Target.Create(target.Kind, target.Selector, target.ApplicationId, target.ComponentRef);
    }
    private static async Task<PlacementSelectionReply> SafeSelection(Func<ValueTask<PlacementSelectionResult>> action, string environment, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await action();
            var reply = new PlacementSelectionReply
            {
                Status = result.Outcome switch
                {
                    PlacementSelectionOutcome.Created => PlacementSelectionStatus.Created,
                    PlacementSelectionOutcome.Updated => PlacementSelectionStatus.Updated,
                    PlacementSelectionOutcome.Unchanged => PlacementSelectionStatus.Unchanged,
                    PlacementSelectionOutcome.Found => PlacementSelectionStatus.Found,
                    PlacementSelectionOutcome.NotFound => PlacementSelectionStatus.NotFound,
                    PlacementSelectionOutcome.Conflict => PlacementSelectionStatus.Conflict,
                    PlacementSelectionOutcome.InvalidContext => PlacementSelectionStatus.InvalidContext,
                    PlacementSelectionOutcome.EnvironmentNotFound => PlacementSelectionStatus.EnvironmentNotFound,
                    PlacementSelectionOutcome.ApplicationNotFound => PlacementSelectionStatus.ApplicationNotFound,
                    PlacementSelectionOutcome.ApplicationVersionNotFound => PlacementSelectionStatus.ApplicationVersionNotFound,
                    PlacementSelectionOutcome.ComponentNotFound => PlacementSelectionStatus.ComponentNotFound,
                    PlacementSelectionOutcome.RevisionNotFound => PlacementSelectionStatus.RevisionNotFound,
                    _ => throw new InvalidOperationException("Unknown selection outcome.")
                }
            };
            if (result.Selection is { } s) reply.Selection = new()
            {
                Target = new() { Kind = s.Target.Kind, Selector = s.Target.Selector, ApplicationId = s.Target.Application, ComponentRef = s.Target.Component },
                Reference = new() { Environment = environment, ProviderAlias = s.Reference.Alias.Value, Name = s.Reference.Name.Value, Revision = s.Reference.Revision },
                EnvironmentRecordId = s.Reference.Environment.ToString(),
                ConcurrencyToken = s.Token.ToString(),
                TechnicalActorId = s.Audit.ActorId,
                ChangedAtUtc = s.Audit.ChangedAtUtc.ToString("O"),
                CheckedRevision = s.Audit.CheckedRevision is { } r ? new()
                { ApplicationRecordId = r.ApplicationRecordId.ToString(), RevisionNumber = r.RevisionNumber.Value } : null
            };
            return reply;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new RpcException(new(StatusCode.Internal, "Placement selection operation failed.")); }
    }
}
