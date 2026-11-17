using System.Collections.Immutable;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Haby.Configuration.Client;
using Haby.Configuration.Contracts;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class RuntimeBootstrapTests
{
    private static RuntimeBootstrap Bootstrap() => new(1, Guid.CreateVersion7().ToString("D"),
        ImmutableSortedDictionary<string, string>.Empty.Add("settings", Guid.CreateVersion7().ToString("D")).Add("other", Guid.CreateVersion7().ToString("D")),
        RuntimeBootstrap.TokenPath, new("https://configuration.example/", new("system"), new()));

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("format")]
    [InlineData("empty")]
    [InlineData("revision")]
    [InlineData("duplicate-revision")]
    [InlineData("path")]
    [InlineData("client-missing")]
    [InlineData("total")]
    [InlineData("rpc")]
    [InlineData("backoff")]
    [InlineData("bytes")]
    [InlineData("trust")]
    [InlineData("size")]
    public void Closed_wire_refuses_incomplete_ambiguous_or_unsafe_bootstrap(string fault)
    {
        var json = RuntimeBootstrapCodec.Encode(Bootstrap()); var node = JsonNode.Parse(json)!;
        switch (fault)
        {
            case "unknown": node["credential"] = "forbidden"; break;
            case "duplicate": json = "{\"format\":1," + json[1..]; break;
            case "null": node["delivery"] = null; break;
            case "format": node["format"] = 2; break;
            case "empty": node["documents"] = new JsonObject(); break;
            case "revision": node["documents"]!["settings"] = Guid.NewGuid().ToString("D"); break;
            case "duplicate-revision": node["documents"]!["settings"] = node["documents"]!["other"]!.DeepClone(); break;
            case "path": node["projectedTokenPath"] = "/tmp/token"; break;
            case "client-missing": node["client"]!.AsObject().Remove("rpcTimeoutMilliseconds"); break;
            case "total": node["client"]!["totalTimeoutSeconds"] = 0; break;
            case "rpc": node["client"]!["rpcTimeoutMilliseconds"] = 30001; break;
            case "backoff": node["client"]!["maximumBackoffMilliseconds"] = 249; break;
            case "bytes": node["client"]!["maximumAggregateBytes"] = 67108865; break;
            case "trust": node["delivery"]!["tls"]!["skipVerify"] = true; break;
            case "size": json = new string(' ', 65537); break;
        }
        if (fault is not ("duplicate" or "size")) json = node.ToJsonString();
        var failure = Assert.Throws<ArgumentException>(() => RuntimeBootstrapCodec.Decode(json));
        Assert.DoesNotContain("forbidden", failure.ToString());
    }

    [Fact]
    public void Complete_set_maps_by_exact_revision_rather_than_response_order()
    {
        var bootstrap = Bootstrap();
        var result = new RuntimeStartupDocuments(bootstrap.Documents.Values.Reverse().Select(x => new RuntimeStartupDocument(x,
            "sha256:configuration-revision-v2:" + new string('a', 64), "{}"u8.ToArray())).ToArray());
        using (result)
        {
            var map = RuntimeBootstrapChannel.Map(bootstrap, result);
            Assert.All(bootstrap.Documents, pair => Assert.Equal(pair.Value, map[pair.Key].RevisionId));
            Assert.Equal(RuntimeBootstrapCodec.Encode(bootstrap), RuntimeBootstrapCodec.Encode(RuntimeBootstrapCodec.Decode(RuntimeBootstrapCodec.Encode(bootstrap))));
        }
        Assert.Throws<ObjectDisposedException>(() => result.Documents);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("name")]
    [InlineData("expired")]
    [InlineData("root")]
    public void Custom_trust_validates_name_validity_chain_and_exclusive_root(string fault)
    {
        using var key = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
        using var otherKey = RSA.Create(2048);
        var otherRequest = new CertificateRequest("CN=Other", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        otherRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var other = otherRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
        using var leafKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=configuration.example", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var leaf = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), fault == "expired" ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        var pem = (fault == "root" ? other : ca).ExportCertificatePem();
        RuntimeBootstrapCodec.Validate(new RuntimeConfiguration("https://configuration.example/", new("custom-ca", pem), new()));
        Assert.Equal(fault == "valid", RuntimeBootstrapChannel.ValidateCustomRoot(leaf,
            fault == "name" ? SslPolicyErrors.RemoteCertificateNameMismatch : SslPolicyErrors.RemoteCertificateChainErrors, pem));
    }
}
