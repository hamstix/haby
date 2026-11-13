using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;

namespace Haby.Kubernetes;

/// <summary>Closed non-secret access configuration. Production trust is always Online fail-closed.</summary>
public sealed record KubernetesProviderConfiguration(Uri Endpoint, string? ServerCaPem, SecretRef? BearerToken,
    SecretRef? ClientCertificate, SecretRef? ClientPrivateKey)
{
    public override string ToString() => nameof(KubernetesProviderConfiguration);
}

public sealed class KubernetesProviderConfigurationCodec : ProviderConfigurationCodec<KubernetesProviderConfiguration>
{
    public const string Kind = "kubernetes/v1";
    public const string Shape = "haby.kubernetes.connection/v1";
    public const string Implementation = "haby.kubernetes/v1";
    public override string ProviderKindId => Kind;
    public override string ShapeId => Shape;
    public override int PayloadFormatVersion => 1;
    public override PreparedProviderConfiguration Encode(KubernetesProviderConfiguration configuration)
    {
        KubernetesConnection.ValidateAccess(configuration.Endpoint, configuration.ServerCaPem, configuration.BearerToken,
            configuration.ClientCertificate, configuration.ClientPrivateKey);
        string? Ref(SecretRef? value) => value is null ? null : value.SecretId + "/" + value.SecretVersionId;
        var snapshot = new KubernetesConfigurationSnapshot(1, configuration.Endpoint.AbsoluteUri, configuration.ServerCaPem,
            Ref(configuration.BearerToken), Ref(configuration.ClientCertificate), Ref(configuration.ClientPrivateKey));
        var json = JsonSerializer.Serialize(snapshot, AccessJson.Default.KubernetesConfigurationSnapshot);
        var refs = new List<ProviderConfigurationSecretReference>();
        if (configuration.BearerToken is { } bearer) refs.Add(new("bearer-token", bearer));
        if (configuration.ClientCertificate is { } cert) refs.Add(new("client-certificate", cert));
        if (configuration.ClientPrivateKey is { } key) refs.Add(new("client-private-key", key));
        return new(Kind, new(1, configuration.Endpoint.AbsoluteUri), Implementation, Shape, 1, json, refs.AsReadOnly());
    }
    public override KubernetesProviderConfiguration DecodeTyped(string json)
    {
        try
        {
            if (json.Length is 0 or > 2_097_152) throw new InvalidDataException("Invalid Kubernetes configuration.");
            var value = JsonSerializer.Deserialize(json, AccessJson.Default.KubernetesConfigurationSnapshot);
            if (value is not { Format: 1 }) throw new InvalidDataException("Invalid Kubernetes configuration.");
            SecretRef? Ref(string? text)
            {
                if (text is null) return null;
                var parts = text.Split('/');
                if (parts.Length != 2) throw new InvalidDataException("Invalid Kubernetes SecretRef.");
                return new(SecretId.Parse(parts[0]), SecretVersionId.Parse(parts[1]));
            }
            var result = new KubernetesProviderConfiguration(new Uri(value.Endpoint, UriKind.Absolute), value.ServerCaPem,
                Ref(value.BearerToken), Ref(value.ClientCertificate), Ref(value.ClientPrivateKey));
            if (Encode(result).PayloadJson != json) throw new InvalidDataException("Noncanonical Kubernetes configuration.");
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException)
        { throw new InvalidDataException("Invalid Kubernetes configuration."); }
    }
}

internal sealed record KubernetesConfigurationSnapshot(int Format, string Endpoint, string? ServerCaPem, string? BearerToken,
    string? ClientCertificate, string? ClientPrivateKey);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(KubernetesConfigurationSnapshot))]
internal partial class AccessJson : JsonSerializerContext;
