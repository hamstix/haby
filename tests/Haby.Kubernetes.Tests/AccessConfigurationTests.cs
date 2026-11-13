using System.Text.Json.Nodes;
using Haby.Domain;
using Haby.Kubernetes;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class AccessConfigurationTests
{
    [Fact]
    public void Exact_configuration_roundtrips_only_explicit_secret_references()
    {
        var codec = new KubernetesProviderConfigurationCodec();
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        var configuration = new KubernetesProviderConfiguration(new Uri("https://api.example:6443"), null, reference, null, null);
        var prepared = codec.Encode(configuration);
        Assert.Equal(configuration, codec.DecodeTyped(prepared.PayloadJson));
        Assert.Equal(reference, Assert.Single(prepared.SecretReferences).Reference);
        Assert.Equal("https://api.example:6443/", prepared.TargetIdentity.Value);
        Assert.DoesNotContain("revocation", prepared.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData("http://api.example")]
    [InlineData("https://user:password@api.example")]
    [InlineData("https://api.example/path")]
    [InlineData("https://api.example?token=value")]
    public void Unsafe_transport_addresses_refuse_before_material_resolution(string endpoint)
    {
        var codec = new KubernetesProviderConfigurationCodec();
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        Assert.ThrowsAny<ArgumentException>(() => codec.Encode(new(new Uri(endpoint), null, reference, null, null)));
    }
    [Fact]
    public void Unknown_duplicate_and_insecure_trust_options_are_not_configuration_capabilities()
    {
        var codec = new KubernetesProviderConfigurationCodec();
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        var json = codec.Encode(new(new Uri("https://api.example"), null, reference, null, null)).PayloadJson;
        var root = JsonNode.Parse(json)!; root["revocation"] = "NoCheck";
        Assert.Throws<InvalidDataException>(() => codec.DecodeTyped(root.ToJsonString()));
        Assert.Throws<InvalidDataException>(() => codec.DecodeTyped(json.Insert(1, "\"format\":1,")));
        Assert.ThrowsAny<ArgumentException>(() => codec.Encode(new(new Uri("https://api.example"), null, reference, reference, reference)));
    }
}
