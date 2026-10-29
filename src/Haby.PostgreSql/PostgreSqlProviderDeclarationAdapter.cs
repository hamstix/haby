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

    public object ParseProperties(JsonElement properties)
    {
        var value = properties.Deserialize(PostgreSqlDeclarationJsonContext.Default.PostgreSqlDeclarationProperties)
            ?? throw new ArgumentException("Invalid Provider properties.");
        // Validate with the same typed rules as the persisted snapshot, before any database work.
        _ = Configuration(value, new SecretRef(SecretId.New(), SecretVersionId.New()));
        return value;
    }

    public object Bind(object properties, IReadOnlyDictionary<string, SecretRef> secrets) =>
        Configuration((PostgreSqlDeclarationProperties)properties, secrets[PostgreSqlProviderConfigurationCodec.AdministratorPasswordSlot]);
    public bool CanReplace(ProviderAdapterKey previous) => previous == Key;
    public bool Equivalent(object previous, object candidate) =>
        previous is PostgreSqlProviderConfiguration first && candidate is PostgreSqlProviderConfiguration second && first == second;
    public bool SameEndpoint(object previous, object candidate) =>
        previous is PostgreSqlProviderConfiguration first && candidate is PostgreSqlProviderConfiguration second &&
        first.Host == second.Host && first.Port == second.Port;

    private PostgreSqlProviderConfiguration Configuration(PostgreSqlDeclarationProperties value, SecretRef secret) =>
        new(1, value.Host, value.Port, value.MaintenanceDatabase, value.AdministratorUserName, secret, Key.ImplementationId);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class PostgreSqlDeclarationProperties
{
    public required string Host { get; init; }
    [JsonConverter(typeof(ProviderInteger32Converter))]
    public required int Port { get; init; }
    public required string MaintenanceDatabase { get; init; }
    public required string AdministratorUserName { get; init; }
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PostgreSqlDeclarationProperties))]
internal partial class PostgreSqlDeclarationJsonContext : JsonSerializerContext;
