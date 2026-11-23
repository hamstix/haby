using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<SharedCredentialReply> GetSharedCredentialBindings(SharedCredentialRequest request, ServerCallContext context)
    {
        var target = await SharedTargetAsync(request, context.CancellationToken);
        if (target is null) return new() { Status = "InvalidRequest" };
        var reply = new SharedCredentialReply { Status = "Listed" };
        reply.Bindings.AddRange((await sharedCredentials.InspectAsync(target.Value.Environment, target.Value.Provider,
            target.Value.Revision, context.CancellationToken)).Select(BindingMetadata));
        return reply;
    }
    public override async Task<SharedCredentialReply> RevokeSharedCredentialUse(SharedCredentialRequest request, ServerCallContext context)
    {
        var target = await SharedTargetAsync(request, context.CancellationToken);
        if (target is null || request.Use is not ("SystemAccess" or "ApplicationDelivery")) return new() { Status = "InvalidRequest" };
        SecretRef reference;
        try { reference = new(SecretId.Parse(request.SecretId), SecretVersionId.Parse(request.SecretVersionId)); }
        catch (Exception error) when (error is ArgumentException or FormatException) { return new() { Status = "InvalidRequest" }; }
        var scope = new SharedCredentialScope(target.Value.Environment, target.Value.Provider, target.Value.Revision, request.Slot,
            request.Use == "SystemAccess" ? SharedCredentialUse.SystemAccess : SharedCredentialUse.ApplicationDelivery, reference);
        return new() { Status = (await sharedCredentials.RevokeAsync(scope, Actor, context.CancellationToken)).ToString() };
    }
    private async Task<(EnvironmentRecordId Environment, ProviderInstanceId Provider, ProviderConfigurationRevisionNumber Revision)?> SharedTargetAsync(
        SharedCredentialRequest request, CancellationToken token)
    {
        EnvironmentId name;
        ProviderInstanceId provider;
        ProviderConfigurationRevisionNumber revision;
        try
        {
            name = EnvironmentId.Create(request.Environment);
            provider = ProviderInstanceId.Parse(request.ProviderInstanceId);
            revision = ProviderConfigurationRevisionNumber.Create(request.Revision);
        }
        catch (Exception error) when (error is ArgumentException or FormatException) { return null; }
        var environment = await environments.FindAsync(name, token);
        return environment is null ? null : (environment, provider, revision);
    }
    private static SharedCredentialBindingMetadata BindingMetadata(SharedCredentialBinding binding)
    {
        var metadata = new SharedCredentialBindingMetadata()
        {
            EnvironmentRecordId = binding.Scope.Environment.ToString(),
            ProviderInstanceId = binding.Scope.Provider.ToString(),
            Revision = binding.Scope.Revision.Value,
            Slot = binding.Scope.Slot,
            Use = binding.Scope.Use.ToString(),
            SecretId = binding.Scope.Reference.SecretId.ToString(),
            SecretVersionId = binding.Scope.Reference.SecretVersionId.ToString(),
            IssuedBy = binding.IssuedBy,
            IssuedAtUtc = binding.IssuedAt.ToString("O")
        };
        if (binding.RevokedBy is { } actor) metadata.RevokedBy = actor;
        if (binding.RevokedAt is { } time) metadata.RevokedAtUtc = time.ToString("O");
        return metadata;
    }
}
