using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override Task<ResourceNamingReply> CreateResourceNaming(CreateResourceNamingRequest request, ServerCallContext context) =>
        NamingOperation(request.Reference?.Environment, async (environment, token) =>
            await naming.CreateAsync(environment, NamingReference(request.Reference!.Name, request.Reference.Revision),
                NamingPolicy(request.DeclarationJson), Actor, token), context.CancellationToken);

    public override Task<ResourceNamingReply> GetResourceNaming(ResourceNamingRequest request, ServerCallContext context) =>
        NamingOperation(request.Environment, (environment, token) => naming.GetAsync(environment,
            NamingReference(request.Name, request.Revision), token), context.CancellationToken);

    public override Task<ResourceNamingReply> GetResourceNamingSelection(ResourceNamingSelectionRequest request, ServerCallContext context) =>
        NamingOperation(request.Environment, (environment, token) => naming.GetSelectionAsync(environment,
            ProviderAlias.Create(request.Alias), token), context.CancellationToken);

    public override Task<ResourceNamingReply> SelectResourceNaming(SelectResourceNamingRequest request, ServerCallContext context) =>
        NamingOperation(request.Binding?.Environment, (environment, token) =>
        {
            var expected = request.PreconditionCase switch
            {
                SelectResourceNamingRequest.PreconditionOneofCase.ExpectedAbsence when request.ExpectedAbsence => null,
                SelectResourceNamingRequest.PreconditionOneofCase.ExpectedToken when !string.IsNullOrEmpty(request.ExpectedToken) => request.ExpectedToken,
                _ => throw new ArgumentException("Missing precondition.")
            };
            return naming.SelectAsync(environment, ProviderAlias.Create(request.Binding!.Alias),
                new(ProviderInstanceId.Parse(request.ExpectedProviderInstanceId), ProviderConfigurationRevisionNumber.Create(request.ExpectedConfigurationRevision)),
                expected, NamingReference(request.Name, request.Revision), Actor, token);
        }, context.CancellationToken);

    private static ResourceNamingReference NamingReference(string name, long revision)
    {
        var result = new ResourceNamingReference(name, revision);
        try { result.Validate(); return result; }
        catch (InvalidDataException) { throw new ArgumentException("Invalid naming reference."); }
    }
    private static ResourceNamingPolicy NamingPolicy(string json)
    {
        try { return ResourceNamingCodec.ParsePolicy(json); }
        catch (InvalidDataException) { throw new ArgumentException("Invalid naming declaration."); }
    }

    private async Task<ResourceNamingReply> NamingOperation(string? environment,
        Func<EnvironmentRecordId, CancellationToken, ValueTask<ResourceNamingResult>> action, CancellationToken token)
    {
        try
        {
            if (environment is null) throw new ArgumentException("Missing Environment.");
            var id = await environments.FindAsync(EnvironmentId.Create(environment), token);
            var result = id is null ? new(ResourceNamingOutcome.EnvironmentNotFound) : await action(id, token);
            var reply = new ResourceNamingReply { Status = (ResourceNamingStatus)((int)result.Outcome + 1) };
            if (result.State is { } state)
            {
                if (state.Snapshot.Reference is { } reference) { reply.Name = reference.Name; reply.Revision = reference.Revision; }
                reply.Mode = state.Snapshot.Policy.Mode;
                if (state.Snapshot.Policy.Prefix is { } prefix) reply.Prefix = prefix;
                if (state.Token is { } selectionToken) reply.Token = selectionToken;
                reply.Actor = state.Actor;
                reply.ChangedAtUtc = state.ChangedAt == default ? "" : state.ChangedAt.ToString("O");
            }
            return reply;
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        { return new() { Status = ResourceNamingStatus.InvalidDeclaration }; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new() { Status = ResourceNamingStatus.StoreUnavailable }; }
    }
}
