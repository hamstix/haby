using Google.Protobuf;
using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Configuration.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace Haby.Operator.Host;

internal sealed class ConfigurationDeliveryService(DeliverConfiguration delivery, DeliverRuntimeConfiguration runtime,
    RuntimeDeliveryLifetime lifetime, TimeProvider clock) : ConfigurationDelivery.ConfigurationDeliveryBase
{
    public override async Task<DeliverConfigurationReply> DeliverRuntime(DeliverRuntimeConfigurationRequest request, ServerCallContext context)
    {
        byte[]? credential = null;
        try
        {
            var headers = context.RequestHeaders.Where(x => x.Key == "authorization").Take(2).ToArray();
            if (headers.Length != 1 || headers[0].IsBinary || !ValidRuntimeHeader(headers[0].Value) ||
                !TryRuntimeId(request.BindingId, out var binding) || !TryRuntimeRevision(request.RevisionId, out var revision))
                throw new RpcException(new(StatusCode.Unauthenticated, "Runtime identity refused."));
            using var lease = lifetime.Enter();
            if (lease is null) throw RuntimeFailure(RuntimeDeliveryOutcome.AdmissionOverload);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(lifetime.Options.RequestTimeoutSeconds), clock);
            using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, deadline.Token);
            var token = requestLifetime.Token;
            token.ThrowIfCancellationRequested();
            credential = new byte[headers[0].Value.Length - 7];
            Encoding.ASCII.GetBytes(headers[0].Value.AsSpan(7), credential);
            using var result = await runtime.ExecuteAsync(binding, revision, credential, token);
            token.ThrowIfCancellationRequested();
            if (result.Outcome != RuntimeDeliveryOutcome.Delivered) throw RuntimeFailure(result.Outcome);
            var document = result.Document!;
            var reply = new DeliverConfigurationReply { RevisionId = revision.ToString(), SourceFingerprint = document.SourceFingerprint ?? "", Json = ByteString.CopyFrom(document.Json.Span) };
            ValidateRuntimeResponseSize(reply);
            token.ThrowIfCancellationRequested();
            return reply;
        }
        catch (RpcException) { throw; }
        catch (OperationCanceledException)
        {
            var code = context.Deadline != DateTime.MaxValue && context.Deadline <= clock.GetUtcNow().UtcDateTime ? StatusCode.DeadlineExceeded :
                context.CancellationToken.IsCancellationRequested ? StatusCode.Cancelled : StatusCode.Unavailable;
            throw new RpcException(new(code, "Runtime delivery request ended."));
        }
        catch (Exception) { throw new RpcException(new(StatusCode.Unavailable, "Runtime delivery unavailable.")); }
        finally { if (credential is not null) CryptographicOperations.ZeroMemory(credential); }
    }

    internal static bool TryRuntimeId(string value, out Guid id) => Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;
    private static bool TryRuntimeRevision(string value, out ConfigurationDocumentRevisionId revision)
    {
        revision = null!;
        if (value.Length != 36) return false;
        try { revision = ConfigurationDocumentRevisionId.Parse(value); return true; }
        catch (ArgumentException) { return false; }
    }
    internal static bool ValidRuntimeHeader(string value)
    {
        if (value.Length is <= 7 or > RuntimeAuthentication.MaximumCredentialBytes + 7 || !value.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        foreach (var c in value.AsSpan(7))
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9') && !"-._~+/=".Contains(c, StringComparison.Ordinal)) return false;
        return true;
    }
    internal static RpcException RuntimeFailure(RuntimeDeliveryOutcome outcome) => new(new(outcome switch
    {
        RuntimeDeliveryOutcome.Unauthenticated => StatusCode.Unauthenticated,
        RuntimeDeliveryOutcome.PermissionDenied => StatusCode.PermissionDenied,
        RuntimeDeliveryOutcome.AdmissionOverload or RuntimeDeliveryOutcome.OutputLimit => StatusCode.ResourceExhausted,
        RuntimeDeliveryOutcome.FailedPrecondition => StatusCode.FailedPrecondition,
        _ => StatusCode.Unavailable
    }, outcome switch
    {
        RuntimeDeliveryOutcome.Unauthenticated => "Runtime identity refused.",
        RuntimeDeliveryOutcome.PermissionDenied => "Runtime configuration access denied.",
        RuntimeDeliveryOutcome.AdmissionOverload or RuntimeDeliveryOutcome.OutputLimit => "Runtime delivery limit exceeded.",
        RuntimeDeliveryOutcome.FailedPrecondition => "Runtime configuration unavailable.",
        _ => "Runtime delivery unavailable."
    }), outcome switch
    {
        RuntimeDeliveryOutcome.AdmissionOverload => new Metadata { { RuntimeDeliveryReasons.Header, RuntimeDeliveryReasons.AdmissionOverload } },
        RuntimeDeliveryOutcome.OutputLimit => new Metadata { { RuntimeDeliveryReasons.Header, RuntimeDeliveryReasons.OutputLimit } },
        _ => new Metadata()
    });

    internal static void ValidateRuntimeResponseSize(DeliverConfigurationReply reply)
    {
        if (reply.CalculateSize() > 8 * 1024 * 1024) throw RuntimeFailure(RuntimeDeliveryOutcome.OutputLimit);
    }

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
