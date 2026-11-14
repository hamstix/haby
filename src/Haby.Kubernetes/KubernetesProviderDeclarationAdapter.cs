using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;

namespace Haby.Kubernetes;

/// <summary>Closed administrative intake; exact secret names are resolved by the scoped catalog.</summary>
public sealed class KubernetesProviderDeclarationAdapter(KubernetesProviderConfigurationCodec codec) : IProviderDeclarationAdapter
{
    public ProviderAdapterKey Key { get; } = new(KubernetesProviderConfigurationCodec.Kind,
        KubernetesProviderConfigurationCodec.Shape, KubernetesProviderConfigurationCodec.Implementation);
    public IProviderConfigurationCodec Codec => codec;
    public IReadOnlyList<string> RequiredSecretSlots => [];
    public IReadOnlyList<string> AllowedSecretSlots => ["bearer-token", "client-certificate", "client-private-key"];

    public object ParseProperties(JsonElement properties)
    {
        var value = properties.Deserialize(DeclarationJson.Default.KubernetesDeclarationProperties)
            ?? throw new ArgumentException("Invalid Kubernetes properties.");
        if (properties.TryGetProperty("serverCaPem"u8, out var ca) && ca.ValueKind == JsonValueKind.Null)
            throw new ArgumentException("Invalid Kubernetes trust roots.");
        _ = codec.Encode(new(new Uri(value.Endpoint, UriKind.Absolute), value.ServerCaPem,
            new(SecretId.New(), SecretVersionId.New()), null, null));
        return value;
    }

    public object Bind(object properties, IReadOnlyDictionary<string, SecretRef> secrets)
    {
        var value = (KubernetesDeclarationProperties)properties;
        if (secrets.Keys.Any(slot => !AllowedSecretSlots.Contains(slot, StringComparer.Ordinal)))
            throw new ArgumentException("Invalid Kubernetes credential slots.");
        secrets.TryGetValue("bearer-token", out var bearer);
        secrets.TryGetValue("client-certificate", out var certificate);
        secrets.TryGetValue("client-private-key", out var key);
        var configuration = new KubernetesProviderConfiguration(new Uri(value.Endpoint, UriKind.Absolute),
            value.ServerCaPem, bearer, certificate, key);
        _ = codec.Encode(configuration);
        return configuration;
    }

    public bool CanReplace(ProviderAdapterKey previous) => previous == Key;
    public bool Equivalent(object previous, object candidate) =>
        previous is KubernetesProviderConfiguration first && candidate is KubernetesProviderConfiguration second && first == second;
    public bool SameEndpoint(object previous, object candidate) =>
        previous is KubernetesProviderConfiguration first && candidate is KubernetesProviderConfiguration second && first.Endpoint == second.Endpoint;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class KubernetesDeclarationProperties
{
    public required string Endpoint { get; init; }
    public string? ServerCaPem { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(KubernetesDeclarationProperties))]
internal partial class DeclarationJson : JsonSerializerContext;
