using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using k8s;

internal static class ClientFactory
{
    internal static Kubernetes Create(Uri endpoint, string directory, string? bearer = null, string? tlsName = null, bool untrusted = false, bool wrongKey = false, params DelegatingHandler[] handlers)
    {
        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(Path.Combine(directory, "ca.crt"));
        using var key = System.Security.Cryptography.RSA.Create(2048);
        if (untrusted)
        {
            roots.Clear();
            var request = new CertificateRequest("CN=Untrusted Qualification CA", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            roots.Add(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1)));
        }
        return new Kubernetes(new KubernetesClientConfiguration
        {
            Host = endpoint.AbsoluteUri,
            TlsServerName = tlsName,
            // 19.0.2's SslCaCerts callback ignores a simultaneous hostname mismatch.
            // Leave it unset and install an explicit, strict custom-root policy.
            FirstMessageHandlerSetup = handler =>
            {
                handler.AllowAutoRedirect = false;
                handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.AddRange(roots);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
                    return chain.Build((X509Certificate2)certificate);
                };
            },
            AccessToken = bearer,
            ClientCertificateFilePath = bearer is null ? Path.Combine(directory, "admin.crt") : null,
            ClientKeyFilePath = bearer is null && !wrongKey ? Path.Combine(directory, "admin.key") : null,
            ClientCertificateKeyData = wrongKey ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem())) : null,
            HttpClientTimeout = TimeSpan.FromSeconds(10),
        }, handlers);
    }
}
