using System.Text.Json;
using System.Text.Json.Nodes;
using Haby.Domain;
using Haby.Kubernetes;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class AccessConfigurationTests
{
    [Fact]
    public void Explicit_offline_policy_is_an_operator_capability_and_is_captured()
    {
        var codec = new KubernetesProviderConfigurationCodec();
        var adapter = new KubernetesProviderDeclarationAdapter(codec);
        using var declaration = JsonDocument.Parse("""{"endpoint":"https://api.example","tls":{"mode":"system","revocation":"offline"}}""");
        var configuration = (KubernetesProviderConfiguration)adapter.Bind(adapter.ParseProperties(declaration.RootElement),
            new Dictionary<string, SecretRef> { ["bearer-token"] = new(SecretId.New(), SecretVersionId.New()) });
        using var saved = JsonDocument.Parse(codec.Encode(configuration).PayloadJson);
        Assert.Equal("system", saved.RootElement.GetProperty("tls"u8).GetProperty("mode"u8).GetString());
        Assert.Equal("offline", saved.RootElement.GetProperty("tls"u8).GetProperty("revocation"u8).GetString());
    }

    [Fact]
    public void Current_saved_configuration_is_format_two_and_refuses_format_one()
    {
        var codec = new KubernetesProviderConfigurationCodec();
        var prepared = codec.Encode(new(new Uri("https://api.example"), null,
            new(SecretId.New(), SecretVersionId.New()), null, null));
        Assert.Equal(2, prepared.PayloadFormatVersion);
        using var saved = JsonDocument.Parse(prepared.PayloadJson);
        Assert.Equal(2, saved.RootElement.GetProperty("format"u8).GetInt32());
        var legacy = JsonNode.Parse(prepared.PayloadJson)!;
        legacy["format"] = 1;
        Assert.Throws<InvalidDataException>(() => codec.DecodeTyped(legacy.ToJsonString()));
    }

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
        Assert.Contains("\"revocation\":\"online\"", prepared.PayloadJson, StringComparison.Ordinal);
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
    public void Unknown_duplicate_and_credential_conflicts_refuse()
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
