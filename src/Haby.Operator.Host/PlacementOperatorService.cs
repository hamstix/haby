using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Haby.Application;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    // Validation is portable and performs no catalog queries. Scope is needed only for import/get.
    public override Task<PlacementReply> ValidatePlacement(PlacementPayloadRequest request, ServerCallContext context)
        => SafePlacement(() => ValueTask.FromResult(placements.Validate(request.Payload.Memory)), "", "", context.CancellationToken);

    public override Task<PlacementReply> ImportPlacement(PlacementPayloadRequest request, ServerCallContext context)
        => SafePlacement(() => placements.ImportAsync(request.Environment, request.ProviderAlias, request.Payload.Memory, Actor,
            context.CancellationToken), request.Environment, request.ProviderAlias, context.CancellationToken);

    public override Task<PlacementReply> GetPlacementRevision(PlacementRevisionRequest request, ServerCallContext context)
        => SafePlacement(() => placements.GetAsync(request.Environment, request.ProviderAlias, request.Name, request.Revision,
            context.CancellationToken), request.Environment, request.ProviderAlias, context.CancellationToken);

    private static async Task<PlacementReply> SafePlacement(Func<ValueTask<PlacementResult>> action, string environment, string alias, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await action();
            var reply = new PlacementReply
            {
                Status = result.Outcome switch
                {
                    PlacementOutcome.Valid => PlacementStatus.Valid,
                    PlacementOutcome.Created => PlacementStatus.Created,
                    PlacementOutcome.ExistingEquivalent => PlacementStatus.ExistingEquivalent,
                    PlacementOutcome.Found => PlacementStatus.Found,
                    PlacementOutcome.NotFound => PlacementStatus.NotFound,
                    PlacementOutcome.InvalidContext => PlacementStatus.InvalidContext,
                    PlacementOutcome.EnvironmentNotFound => PlacementStatus.EnvironmentNotFound,
                    PlacementOutcome.InvalidDeclaration => PlacementStatus.InvalidDeclaration,
                    PlacementOutcome.RevisionContentConflict => PlacementStatus.RevisionContentConflict,
                    _ => throw new InvalidOperationException("Unknown Placement outcome.")
                },
                DiagnosticCode = result.DiagnosticCode ?? ""
            };
            var declaration = result.Revision?.Declaration ?? result.Declaration;
            if (declaration is not null) reply.Reference = new()
            { Environment = environment, ProviderAlias = alias, Name = declaration.Name.Value, Revision = declaration.Revision };
            if (result.Revision is { } accepted && result.Outcome != PlacementOutcome.RevisionContentConflict)
                reply.Metadata = new()
                {
                    RecordId = accepted.Id.ToString("D"),
                    EnvironmentRecordId = accepted.Key.Environment.ToString(),
                    TechnicalActorId = accepted.Audit.ActorId,
                    ImportedAtUtc = accepted.Audit.ImportedAt.ToUniversalTime().ToString("O")
                };
            if (result.Outcome == PlacementOutcome.Found)
                reply.DeclarationJson = ByteString.CopyFrom(Encoding.UTF8.GetBytes(declaration!.Payload));
            return reply;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new RpcException(new(StatusCode.Internal, "Placement operation failed.")); }
    }
}
