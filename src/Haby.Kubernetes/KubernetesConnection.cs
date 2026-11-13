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
    SecretRef? BearerToken, SecretRef? ClientCertificate, SecretRef? ClientPrivateKey)
{
    public override string ToString() => nameof(KubernetesConnection);
    internal void Validate()
    {
        DeploymentIntents.Binding(Binding);
        ValidateAccess(Endpoint, ServerCaPem, BearerToken, ClientCertificate, ClientPrivateKey);
    }
    internal static void ValidateAccess(Uri Endpoint, string? ServerCaPem, SecretRef? BearerToken, SecretRef? ClientCertificate, SecretRef? ClientPrivateKey)
    {
        Need(Endpoint is { IsAbsoluteUri: true, Scheme: "https" } && string.IsNullOrEmpty(Endpoint.UserInfo) &&
            string.IsNullOrEmpty(Endpoint.Query) && string.IsNullOrEmpty(Endpoint.Fragment) && Endpoint.AbsolutePath == "/", "endpoint");
        Need(BearerToken is not null && ClientCertificate is null && ClientPrivateKey is null ||
            BearerToken is null && ClientCertificate is not null && ClientPrivateKey is not null, "credential-alternative");
        Need(ServerCaPem is null || ServerCaPem.Length is > 0 and <= 1_048_576, "trust-roots");
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
        CreateCore(configuration, secrets, false, [], null, token);

    // This is not a configuration option. Only friend test/qualification assemblies can reach this seam.
    internal static ValueTask<KernelClient> CreateFixture(KubernetesConnection configuration, ISecretStore secrets,
        DelegatingHandler[] handlers, CancellationToken token, string? serverName = null) => CreateCore(configuration, secrets, true, handlers, serverName, token);

    private static async ValueTask<KernelClient> CreateCore(KubernetesConnection c, ISecretStore secrets,
        bool disposableFixture, DelegatingHandler[] handlers, string? fixtureServerName, CancellationToken token)
    {
        c.Validate(); token.ThrowIfCancellationRequested();
        var definition = new SecretDefinition(new SecretOwner.Environment(EnvironmentRecordId.Parse(c.Binding.Environment)), SecretPurpose.ProviderAdministrator);
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
                    handler.SslOptions.CertificateRevocationCheckMode = disposableFixture ? X509RevocationMode.NoCheck : X509RevocationMode.Online;
                    handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                        ValidateServer(cert, errors, roots, disposableFixture);
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
        X509Certificate2Collection roots, bool disposableFixture = false)
    {
        if (certificate is not X509Certificate2 leaf || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
        try
        {
            using var chain = new X509Chain();
            ConfigureChain(chain.ChainPolicy, roots, disposableFixture);
            return chain.Build(leaf) && (disposableFixture || chain.ChainElements.Count > 1);
        }
        catch (CryptographicException) { return false; }
    }

    internal static void ConfigureChain(X509ChainPolicy policy, X509Certificate2Collection roots, bool disposableFixture = false)
    {
        if (roots.Count > 0) { policy.TrustMode = X509ChainTrustMode.CustomRootTrust; policy.CustomTrustStore.AddRange(roots); }
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        policy.RevocationMode = disposableFixture ? X509RevocationMode.NoCheck : X509RevocationMode.Online;
        policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        policy.VerificationFlags = X509VerificationFlags.NoFlag;
        policy.UrlRetrievalTimeout = TimeSpan.FromSeconds(2);
    }
}
