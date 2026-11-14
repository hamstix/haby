using System.Text;
using System.Text.Json;
using Haby.Application;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Haby.Kubernetes;

internal sealed class KubernetesRuntimeAuthenticator(ISecretStore secrets,
    Func<KubernetesConnection, DelegatingHandler[], CancellationToken, ValueTask<KernelClient>>? fixture = null) : IRuntimeAuthenticator
{
    public RuntimeAuthenticatorContract Contract { get; } = new("haby.kubernetes.token-review/v1", RuntimeIdentityCodec.ValidationContract, 1,
        KubernetesProviderConfigurationCodec.Kind, KubernetesProviderConfigurationCodec.Shape, 1, KubernetesProviderConfigurationCodec.Implementation);

    private static (RuntimeValidationContext Validation, RuntimeAccountReceipt Receipt, KubernetesConnection Connection) Decode(RuntimeTrust trust)
    {
        if (trust.Binding == Guid.Empty || trust.Installation == Guid.Empty || trust.Environment == Guid.Empty || trust.ManagedWorkload == Guid.Empty ||
            trust.Provider == Guid.Empty || trust.Payload.Length is 0 or > 16384 || Encoding.UTF8.GetByteCount(trust.Payload) > 16384 ||
            trust.InventoryContract != "haby.kubernetes.runtime-account-receipt/v1" || trust.InventoryFormat != 1)
            throw new InvalidDataException("Invalid runtime trust metadata.");
        var value = JsonSerializer.Deserialize(trust.Payload, RuntimeIdentityJson.Default.RuntimeValidationContext)
            ?? throw new InvalidDataException("Invalid runtime validation metadata.");
        var receipt = RuntimeIdentityCodec.Receipt(trust.InventoryPayload);
        var original = receipt.Intent;
        if (value.Format != 1 || value.Binding is null || value.Binding != original.Binding ||
            value.Binding.Environment != trust.Environment.ToString() || value.Binding.Provider != trust.Provider.ToString() ||
            value.Binding.Configuration != trust.Configuration || value.Binding.Implementation != trust.Implementation ||
            original.Installation != trust.Installation || original.ManagedWorkload != trust.ManagedWorkload ||
            value.Audience != trust.Audience || value.Audience != RuntimeIdentityCodec.Audience(original) ||
            value.Namespace != original.Namespace || value.ServiceAccountUid != receipt.Uid || value.Mode != "service-account-bound")
            throw new InvalidDataException("Runtime trust metadata mismatch.");
        var config = new KubernetesProviderConfigurationCodec().DecodeTyped(trust.ConfigurationPayload);
        var connection = new KubernetesConnection(value.Binding, config.Endpoint, config.ServerCaPem, config.BearerToken, config.ClientCertificate, config.ClientPrivateKey);
        connection.Validate();
        return (value, receipt, connection);
    }
    public bool Validate(RuntimeTrust trust)
    {
        try { _ = Decode(trust); return true; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or JsonException or NullReferenceException) { return false; }
    }
    public async ValueTask<RuntimeAuthenticationOutcome> AuthenticateAsync(RuntimeTrust trust, ReadOnlyMemory<byte> credential, CancellationToken token)
    {
        try
        {
            var (expected, receipt, connection) = Decode(trust);
            DelegatingHandler[] handlers = [new BoundedTokenReviewResponse()];
            using var lease = fixture is null ? await KubernetesClientFactory.CreateRuntime(connection, secrets, handlers, token) : await fixture(connection, handlers, token);
            // One explicit POST, no application retry, no response/token retained in the result.
            using var response = await lease.Client.AuthenticationV1.CreateTokenReviewWithHttpMessagesAsync(new V1TokenReview
            {
                ApiVersion = "authentication.k8s.io/v1",
                Kind = "TokenReview",
                Spec = new V1TokenReviewSpec { Audiences = [expected.Audience], Token = Encoding.ASCII.GetString(credential.Span) }
            }, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            if (response.Body?.ApiVersion != "authentication.k8s.io/v1" || response.Body.Kind != "TokenReview" ||
                response.Body.Status is null || response.Body.Status.Authenticated == true && response.Body.Status.User is null)
                return RuntimeAuthenticationOutcome.Unavailable;
            if (!string.IsNullOrEmpty(response.Body?.Status?.Error)) return RuntimeAuthenticationOutcome.Unavailable;
            return Matches(response.Body, expected, receipt.Intent.Name) ? RuntimeAuthenticationOutcome.Authenticated : RuntimeAuthenticationOutcome.Unauthenticated;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpOperationException) { return RuntimeAuthenticationOutcome.Unavailable; }
        catch (Exception) { return RuntimeAuthenticationOutcome.Unavailable; }
    }
    internal static bool Matches(V1TokenReview? response, RuntimeValidationContext expected, string account)
    {
        var status = response?.Status; var user = status?.User;
        return response?.ApiVersion == "authentication.k8s.io/v1" && response.Kind == "TokenReview" &&
            status?.Authenticated == true && string.IsNullOrEmpty(status.Error) &&
            status.Audiences is { Count: > 0 and <= 16 } && status.Audiences.All(x => x is { Length: > 0 and <= 256 }) &&
            status.Audiences.Contains(expected.Audience, StringComparer.Ordinal) &&
            user?.Uid == expected.ServiceAccountUid && user.Username == $"system:serviceaccount:{expected.Namespace}:{account}" &&
            Extra(user, "authentication.kubernetes.io/pod-name", 253, name: true) &&
            Extra(user, "authentication.kubernetes.io/pod-uid", 128, name: false);
    }
    private static bool Extra(V1UserInfo user, string key, int maximum, bool name) =>
        user.Extra is not null && user.Extra.TryGetValue(key, out var values) && values is [var value] &&
        value is { Length: > 0 } && value.Length <= maximum && value.All(c => (name ? c is >= 'a' and <= 'z' or >= '0' and <= '9' : char.IsAsciiLetterOrDigit(c)) || c == '-' || name && c == '.') &&
        char.IsAsciiLetterOrDigit(value[0]) && char.IsAsciiLetterOrDigit(value[^1]);
}

/// <summary>Enforce a byte cap before SDK buffering/deserialization, including error and chunked bodies.</summary>
internal sealed class BoundedTokenReviewResponse : DelegatingHandler
{
    internal const int MaximumBytes = 65536;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        try
        {
            if (response.Content.Headers.ContentLength is > MaximumBytes) throw new IOException("Runtime API response exceeds limit.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumBytes + 1 - (int)output.Length)), cancellationToken);
                if (read == 0) break;
                output.Write(buffer, 0, read);
                if (output.Length > MaximumBytes) throw new IOException("Runtime API response exceeds limit.");
            }
            var content = new ByteArrayContent(output.ToArray());
            if (response.Content.Headers.ContentType is { } type) content.Headers.ContentType = type;
            response.Content.Dispose(); response.Content = content;
            return response;
        }
        catch { response.Dispose(); throw; }
    }
}
