using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Haby.Configuration.Contracts;

namespace Haby.Configuration.Client;

/// <summary>Dedicated bootstrap transport; no ambient credentials, proxy, redirects or retry policies.</summary>
public static class RuntimeBootstrapChannel
{
    public static GrpcChannel Create(RuntimeConfiguration configuration)
    {
        RuntimeBootstrapCodec.Validate(configuration);
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        // Match the existing delivery channel's platform default: no explicit online revocation requirement.
        // Kubernetes administrator/API transports retain their independent Online policy.
        if (configuration.Tls.Mode == "custom-ca")
        {
            var pem = configuration.Tls.CaPem!;
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, peerChain, errors) => ValidateCustomRoot(certificate, errors, pem, peerChain);
        }
        try
        {
            return GrpcChannel.ForAddress(configuration.Endpoint, new GrpcChannelOptions
            { HttpHandler = handler, DisposeHttpClient = true, MaxReceiveMessageSize = 8 * 1024 * 1024 });
        }
        catch { handler.Dispose(); throw new RuntimeStartupException(RuntimeStartupFailure.InvalidBootstrap); }
    }

    internal static bool ValidateCustomRoot(X509Certificate? certificate, SslPolicyErrors errors, string pem, X509Chain? peerChain = null)
    {
        if (certificate is not X509Certificate2 leaf ||
            (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
        var roots = new X509Certificate2Collection();
        try
        {
            roots.ImportFromPem(pem);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            // Peer certificates are untrusted chain-building candidates, never trust anchors.
            // Only the explicitly configured roots above can terminate a trusted chain.
            if (peerChain is not null)
                foreach (var element in peerChain.ChainElements)
                    if (!element.Certificate.Equals(leaf)) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            return roots.Count > 0 && chain.Build(leaf);
        }
        catch (CryptographicException) { return false; }
        finally { foreach (var root in roots) root.Dispose(); }
    }

    public static RuntimeStartupOptions Options(RuntimeClientSettings settings) => new()
    {
        TotalTimeout = TimeSpan.FromSeconds(settings.TotalTimeoutSeconds),
        RpcTimeout = TimeSpan.FromMilliseconds(settings.RpcTimeoutMilliseconds),
        InitialBackoff = TimeSpan.FromMilliseconds(settings.InitialBackoffMilliseconds),
        MaximumBackoff = TimeSpan.FromMilliseconds(settings.MaximumBackoffMilliseconds),
        MaximumAggregateBytes = settings.MaximumAggregateBytes
    };

    /// <summary>Associate by exact revision identity only after the complete fetch succeeded.</summary>
    public static IReadOnlyDictionary<string, RuntimeStartupDocument> Map(RuntimeBootstrap bootstrap, RuntimeStartupDocuments complete)
    {
        RuntimeBootstrapCodec.Validate(bootstrap);
        if (complete.Documents.Count != bootstrap.Documents.Count) throw new RuntimeStartupException(RuntimeStartupFailure.InvalidResponse);
        var byRevision = complete.Documents.ToDictionary(x => x.RevisionId, StringComparer.Ordinal);
        var result = new SortedDictionary<string, RuntimeStartupDocument>(StringComparer.Ordinal);
        foreach (var (name, id) in bootstrap.Documents)
            if (byRevision.TryGetValue(id, out var document)) result.Add(name, document);
            else throw new RuntimeStartupException(RuntimeStartupFailure.InvalidResponse);
        return result;
    }
}
