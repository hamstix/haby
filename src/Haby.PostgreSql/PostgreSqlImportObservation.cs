using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;

namespace Haby.PostgreSql;

/// <summary>Read-only evidence of the accepted database-owner contract. Raw nullable ACL text
/// distinguishes defaults from explicit grants; no normalized/private ownership claim is made.</summary>
public sealed record PostgreSqlExistingPairObservation(string DatabaseName, uint DatabaseOid, string LoginName, uint LoginOid,
    string Schema, uint SchemaOid, uint PublicSchemaOid, string DatabaseOwner, uint DatabaseOwnerOid,
    string SchemaOwner, uint SchemaOwnerOid, string PublicSchemaOwner, uint PublicSchemaOwnerOid,
    string? DatabaseAcl, string? SchemaAcl, string? PublicSchemaAcl,
    bool CanLogin, bool SuperUser, bool CreateDatabase, bool CreateRole, bool Replication, bool BypassRowSecurity,
    bool HasRoleMembership, bool CredentialAuthenticated, bool EffectiveConnect, bool EffectiveDatabaseCreate,
    bool EffectiveSchemaUsage, bool EffectiveSchemaCreate, bool AdministratorTls, bool ApplicationTls);

public enum PostgreSqlImportVerificationStatus { Confirmed, Conflict, InputUnavailable, SecretUnavailable, Connectivity }

public static class PostgreSqlImportObservation
{
    public const string Format = "haby.postgresql.existing-pair-observation/v1";

    public static PostgreSqlImportVerificationStatus Verify(PostgreSqlExistingPairInput input, PostgreSqlExistingPairObservation observed)
    {
        if (PostgreSqlImportContracts.Decode(PostgreSqlImportContracts.Encode(input)) is null || !Bounded(observed))
            return PostgreSqlImportVerificationStatus.InputUnavailable;
        // This is deliberately separate from fresh provisioning's private ACL/schema-ownership policy.
        // Import never repairs owners, memberships, ACLs, search_path or existing object ownership.
        return observed.DatabaseName == input.DatabaseName && observed.DatabaseOid == input.DatabaseOid &&
            observed.LoginName == input.LoginName && observed.LoginOid == input.LoginOid && observed.Schema == input.Schema &&
            observed.SchemaOid == input.SchemaOid && observed.PublicSchemaOid == input.PublicSchemaOid &&
            observed.DatabaseOwner == input.LoginName && observed.DatabaseOwnerOid == input.LoginOid &&
            observed.CanLogin && !observed.SuperUser && !observed.CreateDatabase && !observed.CreateRole &&
            !observed.Replication && !observed.BypassRowSecurity && !observed.HasRoleMembership &&
            observed.CredentialAuthenticated && observed.EffectiveConnect && observed.EffectiveDatabaseCreate &&
            observed.EffectiveSchemaUsage && observed.EffectiveSchemaCreate && observed.AdministratorTls == observed.ApplicationTls
                ? PostgreSqlImportVerificationStatus.Confirmed : PostgreSqlImportVerificationStatus.Conflict;
    }

    public static DurableActionPayload Encode(PostgreSqlExistingPairObservation observation)
    {
        if (!Bounded(observation)) throw new InvalidDataException("Unsupported import observation.");
        var json = JsonSerializer.Serialize(observation, PostgreSqlImportObservationJsonContext.Default.PostgreSqlExistingPairObservation);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 65536) throw new InvalidDataException("Import observation exceeds bound.");
        return new(Format, json);
    }

    public static PostgreSqlExistingPairObservation? Decode(DurableActionPayload payload)
    {
        if (payload.Format != Format || System.Text.Encoding.UTF8.GetByteCount(payload.Json) > 65536) return null;
        try
        {
            var value = JsonSerializer.Deserialize(payload.Json, PostgreSqlImportObservationJsonContext.Default.PostgreSqlExistingPairObservation);
            return value is not null && Bounded(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool Bounded(PostgreSqlExistingPairObservation value) =>
        value.DatabaseOid != 0 && value.LoginOid != 0 && value.SchemaOid != 0 && value.PublicSchemaOid != 0 &&
        value.DatabaseOwnerOid != 0 && value.SchemaOwnerOid != 0 && value.PublicSchemaOwnerOid != 0 &&
        PostgreSqlDatabaseGateway.SafeDatabase.IsMatch(value.DatabaseName) && PostgreSqlResourceNaming.ValidExistingRoleName(value.LoginName) &&
        PostgreSqlApplicationLoginContract.ValidSchema(value.Schema) &&
        new[] { value.DatabaseOwner, value.SchemaOwner, value.PublicSchemaOwner }.All(x => !string.IsNullOrEmpty(x) && x.Length <= 63 && !x.Contains('\0')) &&
        new[] { value.DatabaseAcl, value.SchemaAcl, value.PublicSchemaAcl }.All(x => x is null || x.Length <= 16384 && !x.Contains('\0'));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(PostgreSqlExistingPairObservation))]
internal partial class PostgreSqlImportObservationJsonContext : JsonSerializerContext;
