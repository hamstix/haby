using Google.Protobuf;
using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Configuration.Contracts;

namespace Haby.Operator.Host;

public sealed class ConfigurationDeliveryService(DeliverConfiguration delivery) : ConfigurationDelivery.ConfigurationDeliveryBase
{
    public override async Task<DeliverConfigurationReply> Deliver(DeliverConfigurationRequest request, ServerCallContext context)
    {
        try
        {
            var headers = context.RequestHeaders.Where(x => x.Key == "authorization").ToArray();
            if (headers.Length != 1 || !headers[0].Value.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !ConfigurationDeliveryCredential.TryIdentify(headers[0].Value[7..], out _)) throw Denied();
            ConfigurationDocumentRevisionId revision;
            try { revision = ConfigurationDocumentRevisionId.Parse(request.RevisionId); }
            catch (ArgumentException) { throw Denied(); }
            using var result = await delivery.ExecuteAsync(headers[0].Value[7..], revision, context.CancellationToken);
            if (result.Status == ConfigurationDeliveryStatus.Denied) throw Denied();
            if (result.Status == ConfigurationDeliveryStatus.StorageUnavailable) throw new RpcException(new(StatusCode.Unavailable, "Delivery storage is unavailable."));
            if (result.Status == ConfigurationDeliveryStatus.LimitExceeded) throw new RpcException(new(StatusCode.ResourceExhausted, "Configuration exceeds delivery limits."));
            if (result.Status != ConfigurationDeliveryStatus.Delivered) throw new RpcException(new(StatusCode.FailedPrecondition, "Configuration is unavailable."));
            var reply = new DeliverConfigurationReply { RevisionId = revision.ToString(), SourceFingerprint = result.SourceFingerprint ?? "", Json = ByteString.CopyFrom(result.Json.Span) };
            ValidateResponseSize(reply);
            context.CancellationToken.ThrowIfCancellationRequested();
            return reply;
        }
        catch (RpcException) { throw; }
        catch (OperationCanceledException) { throw new RpcException(new(StatusCode.Cancelled, "Delivery canceled.")); }
        catch (Exception) { throw new RpcException(new(StatusCode.Unavailable, "Delivery storage is unavailable.")); }
    }
    private static RpcException Denied() => new(new(StatusCode.PermissionDenied, "Configuration access denied."));
    internal static void ValidateResponseSize(DeliverConfigurationReply reply)
    {
        if (reply.CalculateSize() > 8 * 1024 * 1024) throw new RpcException(new(StatusCode.ResourceExhausted, "Configuration exceeds delivery limits."));
    }
}
