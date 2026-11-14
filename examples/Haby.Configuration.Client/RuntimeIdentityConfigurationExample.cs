using Grpc.Core;
using Haby.Configuration.Contracts;

namespace Haby.Configuration.Example;

/// <summary>One exact generated RPC. The caller owns TLS, transient credential and response handling.</summary>
public static class RuntimeIdentityConfigurationExample
{
    public static async Task<DeliverConfigurationReply> FetchAsync(ConfigurationDelivery.ConfigurationDeliveryClient client,
        string bindingId, string revisionId, string projectedCredential, DateTime deadlineUtc, CancellationToken token)
    {
        var response = await client.DeliverRuntimeAsync(new DeliverRuntimeConfigurationRequest { BindingId = bindingId, RevisionId = revisionId },
            new Metadata { { "authorization", "Bearer " + projectedCredential } }, deadline: deadlineUtc, cancellationToken: token);
        if (response.RevisionId != revisionId || response.Json.Length > 4 * 1024 * 1024) throw new InvalidDataException("Runtime configuration reply refused.");
        return response;
    }
}
