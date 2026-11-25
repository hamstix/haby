using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Security.Cryptography;
using k8s;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class TlsTransportTests
{
    [Theory]
    [InlineData("access")]
    [InlineData("execution")]
    [InlineData("token-review")]
    public async Task Every_production_factory_uses_captured_custom_NoCheck_and_authentication(string factory)
    {
        using var ca = TlsPolicyTests.Authority(); using var leaf = TlsPolicyTests.Leaf(ca);
        await using var endpoint = await LocalTlsEndpoint.Start(leaf);
        var configuration = Configuration(endpoint.Address, ca.ExportCertificatePem(), new(KubernetesTlsMode.CustomCa, KubernetesRevocationMode.NoCheck));
        var codec = new KubernetesProviderConfigurationCodec();
        var captured = codec.DecodeTyped(codec.Encode(configuration).PayloadJson);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = await Connect(factory, captured, timeout.Token);
        var result = await client.Client.AuthenticationV1.CreateSelfSubjectReviewAsync(new(), cancellationToken: timeout.Token);
        Assert.Equal("fixture-user", result.Status.UserInfo.Username);
        Assert.Equal(1, endpoint.Requests); Assert.True(endpoint.Authenticated);
    }

    [Theory]
    [InlineData("unknown-root")]
    [InlineData("system-trust")]
    [InlineData("wrong-name")]
    [InlineData("expired")]
    [InlineData("online-unavailable")]
    [InlineData("offline-unavailable")]
    public async Task Actual_HTTPS_refuses_invalid_server_or_unavailable_revocation_without_downgrade(string failure)
    {
        using var ca = TlsPolicyTests.Authority(); using var foreign = TlsPolicyTests.Authority();
        using var leaf = TlsPolicyTests.Leaf(ca, expired: failure == "expired");
        await using var endpoint = await LocalTlsEndpoint.Start(leaf);
        var address = failure == "wrong-name" ? new UriBuilder(endpoint.Address) { Host = "127.0.0.1" }.Uri : endpoint.Address;
        var mode = failure == "online-unavailable" ? KubernetesRevocationMode.Online :
            failure == "offline-unavailable" ? KubernetesRevocationMode.Offline : KubernetesRevocationMode.NoCheck;
        var configuration = Configuration(address, failure == "system-trust" ? null : (failure == "unknown-root" ? foreign : ca).ExportCertificatePem(),
            new(failure == "system-trust" ? KubernetesTlsMode.System : KubernetesTlsMode.CustomCa, mode));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = await KubernetesClientFactory.CreateAccess(EnvironmentRecordId.New(), configuration, new FixtureSecrets(), timeout.Token);
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.Client.AuthenticationV1.CreateSelfSubjectReviewAsync(new(), cancellationToken: timeout.Token));
        var expected = failure switch
        {
            "unknown-root" or "system-trust" => ProviderConnectionDiagnostic.TlsTrustFailed,
            "wrong-name" => ProviderConnectionDiagnostic.TlsNameMismatch,
            "expired" => ProviderConnectionDiagnostic.TlsCertificateTimeInvalid,
            _ => ProviderConnectionDiagnostic.TlsRevocationUnavailable,
        };
        Assert.Equal(expected, KubernetesTlsFailure.Find(error));
        Assert.Equal(0, endpoint.Requests);
    }

    [Theory]
    [InlineData(200, ProviderPreflightOutcome.Ready)]
    [InlineData(401, ProviderPreflightOutcome.AuthenticationFailed)]
    [InlineData(403, ProviderPreflightOutcome.InsufficientPrivilege)]
    public async Task Insecure_is_explicit_HTTPS_only_and_does_not_bypass_API_authentication(int status, ProviderPreflightOutcome expected)
    {
        using var ca = TlsPolicyTests.Authority(); using var leaf = TlsPolicyTests.Leaf(ca, expired: true);
        await using var endpoint = await LocalTlsEndpoint.Start(leaf, status);
        var configuration = Configuration(endpoint.Address, null, new(KubernetesTlsMode.Insecure, null));
        var handler = new KubernetesPreflightHandler(new(), new FixtureSecrets());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await handler.CheckTypedAsync(new(EnvironmentRecordId.New(), ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1)), configuration, timeout.Token);
        Assert.Equal(expected, result.Outcome); Assert.True(result.ServerCertificateValidationDisabled);
        Assert.Null(result.ConnectionDiagnostic);
        Assert.Equal(1, endpoint.Requests); Assert.True(endpoint.Authenticated);
        Assert.ThrowsAny<ArgumentException>(() => new KubernetesProviderConfigurationCodec().Encode(configuration with { Endpoint = new("http://localhost") }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validating_and_insecure_modes_never_follow_cross_origin_redirect_or_forward_credentials(bool insecure)
    {
        using var ca = TlsPolicyTests.Authority(); using var leaf = TlsPolicyTests.Leaf(ca);
        await using var target = await LocalTlsEndpoint.Start(leaf);
        await using var source = await LocalTlsEndpoint.Start(leaf, 302, target.Address);
        var policy = insecure ? new KubernetesTlsPolicy(KubernetesTlsMode.Insecure, null) : new(KubernetesTlsMode.CustomCa, KubernetesRevocationMode.NoCheck);
        var configuration = Configuration(source.Address, insecure ? null : ca.ExportCertificatePem(), policy);
        var handler = new KubernetesPreflightHandler(new(), new FixtureSecrets());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.False((await handler.CheckTypedAsync(new(EnvironmentRecordId.New(), ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1)), configuration, timeout.Token)).IsReady);
        Assert.Equal(1, source.Requests); Assert.True(source.Authenticated); Assert.Equal(0, target.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_trust_and_insecure_keep_the_configured_client_certificate_authentication(bool insecure)
    {
        using var ca = TlsPolicyTests.Authority(); using var server = TlsPolicyTests.Leaf(ca);
        using var identity = TlsPolicyTests.Leaf(ca, serverAuth: false);
        await using var endpoint = await LocalTlsEndpoint.Start(server, clientCertificate: identity);
        var certificate = new SecretRef(SecretId.New(), SecretVersionId.New());
        var key = new SecretRef(SecretId.New(), SecretVersionId.New());
        using var rsa = identity.GetRSAPrivateKey()!;
        using var secrets = new CertificateSecrets(certificate, Encoding.ASCII.GetBytes(identity.ExportCertificatePem()),
            key, Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem()));
        var configuration = new KubernetesProviderConfiguration(endpoint.Address, insecure ? null : ca.ExportCertificatePem(),
            null, certificate, key, insecure ? new(KubernetesTlsMode.Insecure, null) : new(KubernetesTlsMode.CustomCa, KubernetesRevocationMode.NoCheck));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = await KubernetesClientFactory.CreateAccess(EnvironmentRecordId.New(), configuration, secrets, timeout.Token);
        var result = await client.Client.AuthenticationV1.CreateSelfSubjectReviewAsync(new(), cancellationToken: timeout.Token);
        Assert.Equal("fixture-user", result.Status.UserInfo.Username); Assert.True(endpoint.Authenticated); Assert.Equal(1, endpoint.Requests);
    }

    private sealed class CertificateSecrets(SecretRef certificate, byte[] pem, SecretRef key, byte[] keyPem) : ISecretStore, IDisposable
    {
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes, CancellationToken token)
        {
            Assert.Equal(SecretPurpose.ProviderAdministrator, definition.Purpose);
            Assert.True(reference == certificate || reference == key);
            return ValueTask.FromResult<SecretMaterial?>(new(reference == certificate ? pem : keyPem));
        }
        public void Dispose() { CryptographicOperations.ZeroMemory(pem); CryptographicOperations.ZeroMemory(keyPem); }
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
    }

    private static KubernetesProviderConfiguration Configuration(Uri endpoint, string? ca, KubernetesTlsPolicy policy) =>
        new(endpoint, ca, new(SecretId.New(), SecretVersionId.New()), null, null, policy);

    private static ValueTask<KernelClient> Connect(string factory, KubernetesProviderConfiguration c, CancellationToken token)
    {
        var environment = EnvironmentRecordId.New();
        var connection = new KubernetesConnection(new(environment.ToString(), ProviderInstanceId.New().ToString(), 1, KubernetesProviderConfigurationCodec.Implementation),
            c.Endpoint, c.ServerCaPem, c.BearerToken, c.ClientCertificate, c.ClientPrivateKey, c.Tls);
        return factory switch
        {
            "execution" => KubernetesClientFactory.Create(connection, new FixtureSecrets(), token),
            "token-review" => KubernetesClientFactory.CreateRuntime(connection, new FixtureSecrets(), [], token),
            _ => KubernetesClientFactory.CreateAccess(environment, c, new FixtureSecrets(), token),
        };
    }

    private sealed class FixtureSecrets : ISecretStore
    {
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes, CancellationToken token)
        {
            Assert.Equal(SecretPurpose.ProviderAdministrator, definition.Purpose); token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SecretMaterial?>(new(Encoding.ASCII.GetBytes("test-only")));
        }
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
    }
}

/// <summary>One test-owned loopback TLS endpoint; no cluster, public PKI or external configuration.</summary>
internal sealed class LocalTlsEndpoint(WebApplication application) : IAsyncDisposable
{
    internal Uri Address { get; private set; } = null!;
    internal int Requests;
    internal bool Authenticated;

    internal static async Task<LocalTlsEndpoint> Start(X509Certificate2 certificate, int status = 200, Uri? redirect = null, X509Certificate2? clientCertificate = null)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(new HttpsConnectionAdapterOptions
        {
            ServerCertificate = certificate,
            ClientCertificateMode = clientCertificate is null ? ClientCertificateMode.NoCertificate : ClientCertificateMode.RequireCertificate,
            ClientCertificateValidation = (presented, _, _) => presented.Thumbprint == clientCertificate?.Thumbprint,
        })));
        var application = builder.Build(); var endpoint = new LocalTlsEndpoint(application);
        application.Run(async context =>
        {
            Interlocked.Increment(ref endpoint.Requests);
            endpoint.Authenticated = clientCertificate is null ? context.Request.Headers.Authorization == "Bearer test-only" :
                context.Connection.ClientCertificate?.Thumbprint == clientCertificate.Thumbprint;
            context.Response.StatusCode = status;
            if (redirect is not null) context.Response.Headers.Location = redirect.AbsoluteUri;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(status == 200 ?
                """{"apiVersion":"authentication.k8s.io/v1","kind":"SelfSubjectReview","status":{"userInfo":{"username":"fixture-user","groups":["system:authenticated"]}}}""" :
                """{"apiVersion":"v1","kind":"Status","message":"fixture refusal","code":401}""", context.RequestAborted);
        });
        try
        {
            await application.StartAsync();
            endpoint.Address = new UriBuilder(application.Urls.Single()) { Host = "localhost" }.Uri;
            return endpoint;
        }
        catch { await application.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await application.StopAsync(timeout.Token); }
        finally { await application.DisposeAsync(); }
    }
}
