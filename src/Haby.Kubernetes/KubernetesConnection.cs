using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Haby.Application;
using Haby.Domain;
using k8s;
using static Haby.Kubernetes.DeploymentIntents;

namespace Haby.Kubernetes;

/// <summary>Explicit HTTPS and exactly one scoped credential alternative; no ambient configuration.</summary>
public sealed record KubernetesConnection(KubernetesBinding Binding, Uri Endpoint, string? ServerCaPem,
    SecretRef? BearerToken, SecretRef? ClientCertificate, SecretRef? ClientPrivateKey, KubernetesTlsPolicy? Tls = null)
{
    public override string ToString() => nameof(KubernetesConnection);
    internal void Validate()
    {
        DeploymentIntents.Binding(Binding);
        ValidateAccess(Endpoint, ServerCaPem, BearerToken, ClientCertificate, ClientPrivateKey, Tls);
    }
    internal static void ValidateAccess(Uri Endpoint, string? ServerCaPem, SecretRef? BearerToken, SecretRef? ClientCertificate, SecretRef? ClientPrivateKey, KubernetesTlsPolicy? Tls = null)
    {
        _ = KubernetesTlsPolicy.Normalize(Tls, ServerCaPem);
        Need(Endpoint is { IsAbsoluteUri: true, Scheme: "https" } && string.IsNullOrEmpty(Endpoint.UserInfo) &&
            string.IsNullOrEmpty(Endpoint.Query) && string.IsNullOrEmpty(Endpoint.Fragment) && Endpoint.AbsolutePath == "/", "endpoint");
        Need(BearerToken is not null && ClientCertificate is null && ClientPrivateKey is null ||
            BearerToken is null && ClientCertificate is not null && ClientPrivateKey is not null, "credential-alternative");
        Need(ServerCaPem is null || ServerCaPem.Length is > 0 and <= 1_048_576, "trust-roots");
        if (ServerCaPem is not null)
        {
            var certificates = new X509Certificate2Collection();
            try
            {
                var remaining = ServerCaPem.AsSpan();
                var count = 0;
                while (!remaining.Trim().IsEmpty)
                {
                    Need(PemEncoding.TryFind(remaining, out var pem) && remaining[..pem.Location.Start.Value].Trim().IsEmpty &&
                        remaining[pem.Label].SequenceEqual("CERTIFICATE"), "trust-roots");
                    remaining = remaining[pem.Location.End.Value..]; count++;
                }
                certificates.ImportFromPem(ServerCaPem);
                Need(count > 0 && certificates.Count == count, "trust-roots");
            }
            catch (CryptographicException) { throw new Contracts.WorkloadContractException("trust-roots"); }
            finally { foreach (var certificate in certificates) certificate.Dispose(); }
        }
    }
}

internal sealed class KernelClient(k8s.Kubernetes client, X509Certificate2Collection? roots = null, X509Certificate2? certificate = null) : IDisposable
{
    internal k8s.Kubernetes Client { get; } = client;
    public void Dispose()
    {
        Client.Dispose(); certificate?.Dispose();
        if (roots is not null) foreach (var root in roots) root.Dispose();
    }
}

internal static class KubernetesClientFactory
{
    internal static ValueTask<KernelClient> Create(KubernetesConnection configuration, ISecretStore secrets, CancellationToken token) =>
        CreateConnection(configuration, secrets, false, [], null, token);

    internal static ValueTask<KernelClient> CreateRuntime(KubernetesConnection configuration, ISecretStore secrets,
        DelegatingHandler[] handlers, CancellationToken token) => CreateConnection(configuration, secrets, false, handlers, null, token);

    internal static ValueTask<KernelClient> CreateAccess(EnvironmentRecordId environment,
        KubernetesProviderConfiguration configuration, ISecretStore secrets, CancellationToken token) =>
        CreateCore(environment, configuration, secrets, false, [], null, token);

    // This is not a configuration option. Only friend test/qualification assemblies can reach this seam.
    internal static ValueTask<KernelClient> CreateFixture(KubernetesConnection configuration, ISecretStore secrets,
        DelegatingHandler[] handlers, CancellationToken token, string? serverName = null) => CreateConnection(configuration, secrets, true, handlers, serverName, token);

    private static ValueTask<KernelClient> CreateConnection(KubernetesConnection c, ISecretStore secrets,
        bool disposableFixture, DelegatingHandler[] handlers, string? fixtureServerName, CancellationToken token)
    {
        c.Validate();
        return CreateCore(EnvironmentRecordId.Parse(c.Binding.Environment),
            new(c.Endpoint, c.ServerCaPem, c.BearerToken, c.ClientCertificate, c.ClientPrivateKey, c.Tls),
            secrets, disposableFixture, handlers, fixtureServerName, token);
    }

    private static async ValueTask<KernelClient> CreateCore(EnvironmentRecordId environment,
        KubernetesProviderConfiguration c, ISecretStore secrets,
        bool disposableFixture, DelegatingHandler[] handlers, string? fixtureServerName, CancellationToken token)
    {
        KubernetesConnection.ValidateAccess(c.Endpoint, c.ServerCaPem, c.BearerToken, c.ClientCertificate, c.ClientPrivateKey, c.Tls);
        var policy = KubernetesTlsPolicy.Normalize(c.Tls, c.ServerCaPem);
        token.ThrowIfCancellationRequested();
        var definition = new SecretDefinition(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator);
        async ValueTask<byte[]> Resolve(SecretRef reference)
        {
            using var material = await secrets.ResolveBoundedAsync(definition, reference, 1_048_576, token);
            Need(material is not null, "credential-unavailable"); var bytes = material!.CopyBytes();
            if (bytes.Length is 0 or > 1_048_576) { CryptographicOperations.ZeroMemory(bytes); throw new Contracts.WorkloadContractException("credential-size"); }
            return bytes;
        }
        var buffers = new List<byte[]>(); var roots = new X509Certificate2Collection(); X509Certificate2? certificate = null;
        try
        {
            if (c.ServerCaPem is not null) { roots.ImportFromPem(c.ServerCaPem); Need(roots.Count > 0, "trust-roots"); }
            string? bearer = null;
            if (c.BearerToken is not null)
            {
                var bytes = await Resolve(c.BearerToken); buffers.Add(bytes); bearer = new UTF8Encoding(false, true).GetString(bytes);
                Need(bearer.Length > 0 && bearer.All(ch => char.IsAsciiLetterOrDigit(ch) || "-._~+/=".Contains(ch)), "credential-format");
            }
            else
            {
                var cert = await Resolve(c.ClientCertificate!); buffers.Add(cert);
                var key = await Resolve(c.ClientPrivateKey!); buffers.Add(key);
                certificate = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(cert), Encoding.UTF8.GetString(key));
                Need(certificate.HasPrivateKey, "credential-key");
                if (OperatingSystem.IsWindows())
                {
                    // Schannel requires a temporary platform key association. No PersistKeySet:
                    // disposing the lease removes it; the intermediate private buffer is erased.
                    var pkcs12 = certificate.Export(X509ContentType.Pkcs12);
                    try
                    {
                        var platformCertificate = X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.DefaultKeySet);
                        certificate.Dispose(); certificate = platformCertificate;
                    }
                    finally { CryptographicOperations.ZeroMemory(pkcs12); }
                }
            }
            var clientCertificate = certificate;
            var client = new k8s.Kubernetes(new KubernetesClientConfiguration
            {
                Host = c.Endpoint.AbsoluteUri,
                AccessToken = bearer,
                HttpClientTimeout = TimeSpan.FromSeconds(10),
                TlsServerName = fixtureServerName,
                // Never set SslCaCerts/SkipTlsVerify: SDK initialization would overwrite the qualified callback.
                FirstMessageHandlerSetup = handler =>
                {
                    handler.AllowAutoRedirect = false;
                    handler.SslOptions.CertificateRevocationCheckMode = disposableFixture ? X509RevocationMode.NoCheck : policy.CertificateRevocation;
                    handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    {
                        if (policy.Mode == KubernetesTlsMode.Insecure) return true;
                        if (ServerFailure(cert, errors, roots, disposableFixture, policy) is { } failure)
                            throw new KubernetesTlsFailure(failure);
                        return true;
                    };
                    if (clientCertificate is not null)
                    {
                        handler.SslOptions.ClientCertificates!.Add(clientCertificate);
                        handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate;
                    }
                },
            }, handlers);
            return new KernelClient(client, roots, certificate);
        }
        catch
        {
            certificate?.Dispose(); foreach (var root in roots) root.Dispose(); throw;
        }
        finally { foreach (var buffer in buffers) CryptographicOperations.ZeroMemory(buffer); }
    }

    internal static bool ValidateServer(X509Certificate? certificate, SslPolicyErrors errors,
        X509Certificate2Collection roots, bool disposableFixture = false, KubernetesTlsPolicy? tls = null) =>
        ServerFailure(certificate, errors, roots, disposableFixture, tls) is null;

    private static ProviderConnectionDiagnostic? ServerFailure(X509Certificate? certificate, SslPolicyErrors errors,
        X509Certificate2Collection roots, bool disposableFixture, KubernetesTlsPolicy? tls)
    {
        if (certificate is not X509Certificate2 leaf || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            return ProviderConnectionDiagnostic.TlsValidationFailed;
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return ProviderConnectionDiagnostic.TlsNameMismatch;
        try
        {
            using var chain = new X509Chain();
            ConfigureChain(chain.ChainPolicy, roots, disposableFixture, tls);
            if (chain.Build(leaf)) return disposableFixture || chain.ChainElements.Count > 1 ? null : ProviderConnectionDiagnostic.TlsTrustFailed;
            var flags = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, item) => all | item.Status);
            if ((flags & X509ChainStatusFlags.Revoked) != 0) return ProviderConnectionDiagnostic.TlsCertificateRevoked;
            if ((flags & X509ChainStatusFlags.NotTimeValid) != 0) return ProviderConnectionDiagnostic.TlsCertificateTimeInvalid;
            if ((flags & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain | X509ChainStatusFlags.NotSignatureValid | X509ChainStatusFlags.ExplicitDistrust)) != 0) return ProviderConnectionDiagnostic.TlsTrustFailed;
            if ((flags & (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != 0)
                return ProviderConnectionDiagnostic.TlsRevocationUnavailable;
            return ProviderConnectionDiagnostic.TlsValidationFailed;
        }
        catch (CryptographicException) { return ProviderConnectionDiagnostic.TlsValidationFailed; }
    }

    internal static void ConfigureChain(X509ChainPolicy policy, X509Certificate2Collection roots, bool disposableFixture = false, KubernetesTlsPolicy? tls = null)
    {
        if (roots.Count > 0) { policy.TrustMode = X509ChainTrustMode.CustomRootTrust; policy.CustomTrustStore.AddRange(roots); }
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        policy.RevocationMode = disposableFixture ? X509RevocationMode.NoCheck : (tls ?? new(KubernetesTlsMode.System, KubernetesRevocationMode.Online)).CertificateRevocation;
        policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        policy.VerificationFlags = X509VerificationFlags.NoFlag;
        policy.UrlRetrievalTimeout = TimeSpan.FromSeconds(2);
    }
}
