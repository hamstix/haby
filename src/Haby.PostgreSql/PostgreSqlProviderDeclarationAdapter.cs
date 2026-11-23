using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;

namespace Haby.PostgreSql;

public sealed class PostgreSqlProviderDeclarationAdapter(PostgreSqlProviderConfigurationCodec codec) : IProviderDeclarationAdapter
{
    public ProviderAdapterKey Key { get; } = new(PostgreSqlProviderConfigurationCodec.Kind,
        PostgreSqlProviderConfigurationCodec.Shape, PostgreSqlDatabaseCheckpointCapability.ImplementationId);
    public IProviderConfigurationCodec Codec => codec;
    public IReadOnlyList<string> RequiredSecretSlots => [PostgreSqlProviderConfigurationCodec.AdministratorPasswordSlot];

    public IReadOnlyList<string> AllowedSecretSlots => [PostgreSqlProviderConfigurationCodec.AdministratorPasswordSlot,
        PostgreSqlProviderConfigurationCodec.SystemAccessCredentialSlot, PostgreSqlProviderConfigurationCodec.ApplicationCredentialSlot];
    public IReadOnlyDictionary<string, SharedCredentialUse> SharedCredentialSlots { get; } = new Dictionary<string, SharedCredentialUse>
    {
        [PostgreSqlProviderConfigurationCodec.SystemAccessCredentialSlot] = SharedCredentialUse.SystemAccess,
        [PostgreSqlProviderConfigurationCodec.ApplicationCredentialSlot] = SharedCredentialUse.ApplicationDelivery
    };

    public object ParseProperties(JsonElement properties)
    {
        var value = properties.Deserialize(PostgreSqlDeclarationJsonContext.Default.PostgreSqlDeclarationProperties)
            ?? throw new ArgumentException("Invalid Provider properties.");
        // Materialize the fresh-intake default explicitly; missing stored snapshot fields never use this path.
        var mode = properties.TryGetProperty("transportMode"u8, out var selected)
            ? selected.ValueKind == JsonValueKind.String ? selected.GetString()! : throw new ArgumentException("Invalid PostgreSQL transport mode.")
            : "verify-full";
        value = value with { TransportMode = PostgreSqlProviderTransport.Encode(PostgreSqlProviderTransport.Parse(mode)) };
        // Validate with the same typed rules as the persisted snapshot, before any database work.
        _ = Configuration(value, new SecretRef(SecretId.New(), SecretVersionId.New()));
        return value;
    }

    public object Bind(object properties, IReadOnlyDictionary<string, SecretRef> secrets)
    {
        var configuration = Configuration((PostgreSqlDeclarationProperties)properties, secrets[PostgreSqlProviderConfigurationCodec.AdministratorPasswordSlot]);
        secrets.TryGetValue(PostgreSqlProviderConfigurationCodec.SystemAccessCredentialSlot, out var system);
        secrets.TryGetValue(PostgreSqlProviderConfigurationCodec.ApplicationCredentialSlot, out var application);
        return configuration with { SystemAccessCredential = system, ApplicationCredential = application };
    }
    public bool CanReplace(ProviderAdapterKey previous) => previous == Key;
    public bool Equivalent(object previous, object candidate) =>
        previous is PostgreSqlProviderConfiguration first && candidate is PostgreSqlProviderConfiguration second && first == second;
    public bool SameEndpoint(object previous, object candidate) =>
        previous is PostgreSqlProviderConfiguration first && candidate is PostgreSqlProviderConfiguration second &&
        first.Host == second.Host && first.Port == second.Port;

    private PostgreSqlProviderConfiguration Configuration(PostgreSqlDeclarationProperties value, SecretRef secret) =>
        new(2, value.Host, value.Port, value.MaintenanceDatabase, value.AdministratorUserName, secret, Key.ImplementationId,
            PostgreSqlProviderTransport.Parse(value.TransportMode));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PostgreSqlDeclarationProperties
{
    public required string Host { get; init; }
    [JsonConverter(typeof(ProviderInteger32Converter))]
    public required int Port { get; init; }
    public required string MaintenanceDatabase { get; init; }
    public required string AdministratorUserName { get; init; }
    public string TransportMode { get; init; } = "verify-full";
    public override string ToString() => nameof(PostgreSqlDeclarationProperties);
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, AllowDuplicateProperties = false, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(PostgreSqlDeclarationProperties))]
internal partial class PostgreSqlDeclarationJsonContext : JsonSerializerContext;
