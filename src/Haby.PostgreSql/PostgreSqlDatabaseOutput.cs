using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;

namespace Haby.PostgreSql;

/// <summary>Ordinary database identity from confirmed acquisition; lifecycle authority is separate.</summary>
public sealed record PostgreSqlDatabaseOutput(string DatabaseName, uint DatabaseOid, uint PublicSchemaOid);

public sealed class PostgreSqlDatabaseOutputContract : IExternalOperationOutputContract
{
    public const string Contract = "postgresql.database-output/v2";
    public const string Key = "database";
    public DurableActionExecutionKey Producer => PostgreSqlDatabaseCreateAction.RegistrationKey;
    public ExternalOperationOutputIdentity Identity => new(Contract, Key);
    public static ExternalOperationOutput Encode(PostgreSqlDatabaseOutput output) => new(Contract, Key,
        JsonSerializer.Serialize(output, PostgreSqlDatabaseOutputJsonContext.Default.PostgreSqlDatabaseOutput));
    public static PostgreSqlDatabaseOutput? Decode(ExternalOperationOutput output)
    {
        if (output.Contract != Contract || output.Key != Key) return null;
        try
        {
            using var document = JsonDocument.Parse(output.Payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var members = document.RootElement.EnumerateObject().ToArray();
            if (members.Length != 3 || members.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 3) return null;
            var value = JsonSerializer.Deserialize(output.Payload, PostgreSqlDatabaseOutputJsonContext.Default.PostgreSqlDatabaseOutput);
            return value is not null && value.DatabaseName is not null && PostgreSqlDatabaseGateway.SafeDatabase.IsMatch(value.DatabaseName) &&
                value.DatabaseOid != 0 && value.PublicSchemaOid != 0 ? value : null;
        }
        catch (JsonException) { return null; }
    }
    public bool Validate(DurableOperationNode node, ExternalOperationOutput output)
    {
        if (node.Action != Producer || node.Input.Format != PostgreSqlDatabaseCreateAction.Format || Decode(output) is not { } evidence) return false;
        try
        {
            return JsonSerializer.Deserialize(node.Input.Json, PostgreSqlActionJsonContext.Default.PostgreSqlDatabaseActionInput)?.DatabaseName == evidence.DatabaseName;
        }
        catch (JsonException) { return false; }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PostgreSqlDatabaseOutput))]
internal partial class PostgreSqlDatabaseOutputJsonContext : JsonSerializerContext;
