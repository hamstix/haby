using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;

namespace Haby.PostgreSql;

public sealed record PostgreSqlRoleEvidence(string RoleName, uint RoleOid);

public sealed class PostgreSqlRoleEvidenceContract : IExternalOperationOutputContract
{
    public const string Contract = "postgresql.role-evidence/v1";
    public const string Key = "role";
    public DurableActionExecutionKey Producer => PostgreSqlApplicationLoginAction.RegistrationKey;
    public static ExternalOperationOutput Encode(PostgreSqlRoleEvidence value) => new(Contract, Key,
        JsonSerializer.Serialize(value, PostgreSqlRoleJsonContext.Default.PostgreSqlRoleEvidence));
    public static PostgreSqlRoleEvidence? Decode(ExternalOperationOutput output)
    {
        if (output.Contract != Contract || output.Key != Key) return null;
        try
        {
            var value = JsonSerializer.Deserialize(output.Payload, PostgreSqlRoleJsonContext.Default.PostgreSqlRoleEvidence);
            return value is { RoleOid: > 0, RoleName: not null } &&
                System.Text.RegularExpressions.Regex.IsMatch(value.RoleName, @"\Ahaby_a_[a-f0-9]{48}\z") ? value : null;
        }
        catch (JsonException) { return null; }
    }
    public bool Validate(DurableOperationNode node, ExternalOperationOutput output) =>
        PostgreSqlApplicationLoginAction.Decode(node) is { } input && Decode(output) is { } evidence && input.RoleName == evidence.RoleName;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PostgreSqlRoleEvidence))]
internal partial class PostgreSqlRoleJsonContext : JsonSerializerContext;
