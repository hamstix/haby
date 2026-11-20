using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haby.Domain;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class TlsPolicyTests
{
    private static readonly SecretRef Reference = new(SecretId.New(), SecretVersionId.New());
    [Theory]
    [InlineData("system", null, false, KubernetesTlsMode.System, KubernetesRevocationMode.Online)]
    [InlineData("system", "online", false, KubernetesTlsMode.System, KubernetesRevocationMode.Online)]
    [InlineData("system", "offline", false, KubernetesTlsMode.System, KubernetesRevocationMode.Offline)]
    [InlineData("system", "noCheck", false, KubernetesTlsMode.System, KubernetesRevocationMode.NoCheck)]
    [InlineData("customCa", null, true, KubernetesTlsMode.CustomCa, KubernetesRevocationMode.Online)]
    [InlineData("customCa", "online", true, KubernetesTlsMode.CustomCa, KubernetesRevocationMode.Online)]
    [InlineData("customCa", "offline", true, KubernetesTlsMode.CustomCa, KubernetesRevocationMode.Offline)]
    [InlineData("customCa", "noCheck", true, KubernetesTlsMode.CustomCa, KubernetesRevocationMode.NoCheck)]
    [InlineData("insecure", null, false, KubernetesTlsMode.Insecure, null)]
    public void Closed_explicit_policy_normalizes_and_survives_exact_saved_reload(string mode, string? revocation,
        bool custom, KubernetesTlsMode expectedMode, KubernetesRevocationMode? expectedRevocation)
    {
        using var ca = Authority();
        var tls = new JsonObject { ["mode"] = mode };
        if (revocation is not null) tls["revocation"] = revocation;
        var configuration = Bind(tls, custom ? ca.ExportCertificatePem() : null);
        Assert.Equal(new(expectedMode, expectedRevocation), configuration.Tls);
        var codec = new KubernetesProviderConfigurationCodec();
        var saved = codec.Encode(configuration);
        Assert.Equal(configuration, codec.DecodeTyped(saved.PayloadJson));
        Assert.Equal(2, saved.PayloadFormatVersion);
        Assert.Equal(saved.PayloadJson, codec.Encode(codec.DecodeTyped(saved.PayloadJson)).PayloadJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Omitted_and_explicit_defaults_are_semantically_and_canonically_equivalent(bool custom)
    {
        using var ca = Authority(); var pem = custom ? ca.ExportCertificatePem() : null;
        var omitted = Bind(null, pem);
        var explicitDefault = Bind(new JsonObject { ["mode"] = custom ? "customCa" : "system", ["revocation"] = "online" }, pem);
        Assert.Equal(omitted, explicitDefault);
        var codec = new KubernetesProviderConfigurationCodec();
        Assert.Equal(codec.Encode(omitted).PayloadJson, codec.Encode(explicitDefault).PayloadJson);
        Assert.True(new KubernetesProviderDeclarationAdapter(codec).Equivalent(omitted, explicitDefault));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"mode\":null}")]
    [InlineData("{\"mode\":1}")]
    [InlineData("{\"mode\":\"unknown\"}")]
    [InlineData("{\"mode\":\"system\",\"revocation\":null}")]
    [InlineData("{\"mode\":\"system\",\"revocation\":\"unknown\"}")]
    [InlineData("{\"mode\":\"system\",\"revocation\":1}")]
    [InlineData("{\"mode\":\"system\",\"extra\":true}")]
    [InlineData("{\"mode\":\"system\",\"mode\":\"insecure\"}")]
    [InlineData("{\"mode\":\"insecure\",\"revocation\":\"online\"}")]
    [InlineData("{\"mode\":\"insecure\",\"revocation\":\"noCheck\"}")]
    [InlineData("{\"mode\":\"customCa\"}")]
    public void Malformed_or_conflicting_policy_refuses_during_intake(string tls)
    {
        var adapter = new KubernetesProviderDeclarationAdapter(new());
        using var doc = JsonDocument.Parse("{\"endpoint\":\"https://api.example\",\"tls\":" + tls + "}");
        Assert.ThrowsAny<Exception>(() => adapter.ParseProperties(doc.RootElement));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("insecure")]
    public void Explicit_noncustom_modes_refuse_any_custom_bundle(string mode)
    {
        using var ca = Authority();
        Assert.ThrowsAny<ArgumentException>(() => Bind(new JsonObject { ["mode"] = mode }, ca.ExportCertificatePem()));
    }

    [Fact]
    public void NoCheck_preserves_trust_name_time_and_server_auth_checks()
    {
        using var ca = Authority(); using var foreign = Authority();
        using var leaf = Leaf(ca); using var expired = Leaf(ca, expired: true); using var wrongPurpose = Leaf(ca, serverAuth: false);
        var policy = new KubernetesTlsPolicy(KubernetesTlsMode.CustomCa, KubernetesRevocationMode.NoCheck);
        var roots = new X509Certificate2Collection(ca);
        Assert.True(KubernetesClientFactory.ValidateServer(leaf, SslPolicyErrors.RemoteCertificateChainErrors, roots, tls: policy));
        Assert.False(KubernetesClientFactory.ValidateServer(leaf, SslPolicyErrors.RemoteCertificateNameMismatch, roots, tls: policy));
        Assert.False(KubernetesClientFactory.ValidateServer(leaf, SslPolicyErrors.RemoteCertificateChainErrors, new(foreign), tls: policy));
        Assert.False(KubernetesClientFactory.ValidateServer(expired, SslPolicyErrors.None, roots, tls: policy));
        Assert.False(KubernetesClientFactory.ValidateServer(wrongPurpose, SslPolicyErrors.None, roots, tls: policy));
    }

    [Theory]
    [InlineData(KubernetesRevocationMode.Online, X509RevocationMode.Online)]
    [InlineData(KubernetesRevocationMode.Offline, X509RevocationMode.Offline)]
    [InlineData(KubernetesRevocationMode.NoCheck, X509RevocationMode.NoCheck)]
    public void Chain_receives_exact_revocation_policy_without_fallback(KubernetesRevocationMode mode, X509RevocationMode expected)
    {
        using var ca = Authority(); using var chain = new X509Chain();
        KubernetesClientFactory.ConfigureChain(chain.ChainPolicy, new(ca), tls: new(KubernetesTlsMode.CustomCa, mode));
        Assert.Equal(expected, chain.ChainPolicy.RevocationMode);
        Assert.Equal(X509VerificationFlags.NoFlag, chain.ChainPolicy.VerificationFlags);
        Assert.Contains(chain.ChainPolicy.ApplicationPolicy.Cast<Oid>(), oid => oid.Value == "1.3.6.1.5.5.7.3.1");
    }

    internal static KubernetesProviderConfiguration Bind(JsonObject? tls, string? pem)
    {
        var input = new JsonObject { ["endpoint"] = "https://api.example" };
        if (tls is not null) input["tls"] = tls;
        if (pem is not null) input["serverCaPem"] = pem;
        using var document = JsonDocument.Parse(input.ToJsonString());
        var adapter = new KubernetesProviderDeclarationAdapter(new());
        return (KubernetesProviderConfiguration)adapter.Bind(adapter.ParseProperties(document.RootElement),
            new Dictionary<string, SecretRef> { ["bearer-token"] = Reference });
    }

    internal static X509Certificate2 Authority()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Local TLS test authority", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
    }

    internal static X509Certificate2 Leaf(X509Certificate2 ca, bool expired = false, bool serverAuth = true, string? distributionPoint = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(serverAuth ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
        if (distributionPoint is not null)
            request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([distributionPoint]));
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); request.CertificateExtensions.Add(names.Build());
        using var certificate = request.Create(ca, expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddMinutes(-1),
            expired ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = certificate.CopyWithPrivateKey(key);
        // Schannel cannot use the ephemeral keys returned by CopyWithPrivateKey.
        // DefaultKeySet creates a temporary key association, removed on disposal.
        var pfx = withKey.Export(X509ContentType.Pkcs12);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet | X509KeyStorageFlags.Exportable); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
}
