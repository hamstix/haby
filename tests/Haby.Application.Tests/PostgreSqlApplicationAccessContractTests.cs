using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Haby.PostgreSql;
using Xunit;

namespace Haby.Application.Tests;

public sealed class PostgreSqlApplicationAccessContractTests
{
    private static DurableOperationNode Node()
    {
        var database = new OperationOutputRefDocument("db-create", PostgreSqlDatabaseOutputContract.Contract, "database");
        var role = new OperationOutputRefDocument("app-login-create", PostgreSqlRoleEvidenceContract.Contract, "role");
        var password = new OperationOutputRefDocument("app-login-material", GeneratedCredentialContracts.OutputContract, "password");
        return new(OperationId.Create("app-login-access"), ManifestLocalId.Create("app-login"), PostgreSqlApplicationAccessAction.RegistrationKey,
            new(EnvironmentRecordId.New(), ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1), PostgreSqlDatabaseCheckpointCapability.ImplementationId),
            new(PostgreSqlApplicationAccessAction.Format, JsonSerializer.Serialize(new PostgreSqlLoginAccessInput("app_database", database, "app",
                PostgreSqlApplicationLoginContract.Policy, role, password), PostgreSqlPairJsonContext.Default.PostgreSqlLoginAccessInput)),
            [OperationId.Create("app-login-create"), OperationId.Create("app-login-material"), OperationId.Create("db-create"), OperationId.Create("db-restrict")])
        { OutputReferences = [role.ToReference(), password.ToReference(), database.ToReference()] };
    }

    [Fact]
    public void Final_action_strict_codec_requires_all_dependencies_selectors_policy_and_schema()
    {
        var node = Node(); Assert.NotNull(PostgreSqlApplicationAccessAction.Decode(node));
        foreach (var json in new[]
        {
            node.Input.Json[..^1] + ",\"isOwner\":true}",
            node.Input.Json.Replace("database-owner/v1", "runtime/v1", StringComparison.Ordinal),
            node.Input.Json.Replace("\"schema\":\"app\"", "\"schema\":\"public\"", StringComparison.Ordinal),
            node.Input.Json.Replace("\"schema\":\"app\"", "\"schema\":null", StringComparison.Ordinal),
            node.Input.Json.Replace("\"schema\":\"app\"", "\"schema\":\"pg_private\"", StringComparison.Ordinal),
            node.Input.Json.Replace("db-create", "db-restrict", StringComparison.Ordinal),
            node.Input.Json.Replace("role-evidence/v1", "other/v1", StringComparison.Ordinal),
            node.Input.Json.Replace("\"role\"", "\"other\"", StringComparison.Ordinal),
            node.Input.Json.Replace("app-login-material", "other-material", StringComparison.Ordinal),
            node.Input.Json.Replace("\"passwordSource\":{", "\"passwordSource\":null,\"unknown\":{", StringComparison.Ordinal)
        }) Assert.Null(PostgreSqlApplicationAccessAction.Decode(node with { Input = node.Input with { Json = json } }));
        foreach (var dependency in node.Dependencies)
            Assert.Null(PostgreSqlApplicationAccessAction.Decode(node with { Dependencies = node.Dependencies.Where(d => d != dependency).ToArray() }));
        foreach (var selector in node.OutputReferences)
            Assert.Null(PostgreSqlApplicationAccessAction.Decode(node with { OutputReferences = node.OutputReferences.Where(o => o != selector).ToArray() }));
        Assert.Null(PostgreSqlApplicationAccessAction.Decode(node with { Input = node.Input with { Format = "unknown/v1" } }));
    }

    [Fact]
    public void Connection_strict_typed_output_has_only_an_exact_secret_reference_and_no_material_fields()
    {
        var value = new PostgreSqlApplicationConnection("localhost", 5432, "app_database", "app", "haby_a_" + new string('a', 48), new(SecretId.New(), SecretVersionId.New()));
        var encoded = PostgreSqlApplicationConnectionContract.Encode(value);
        Assert.Equal(value, PostgreSqlApplicationConnectionContract.Decode(encoded));
        foreach (var invalid in new[]
        {
            value with { Host = "" }, value with { Port = 0 }, value with { Port = 65536 }, value with { DatabaseName = "unsafe;" },
            value with { Schema = "public" }, value with { Schema = "pg_catalog" }, value with { UserName = "postgres" }
        }) Assert.Null(PostgreSqlApplicationConnectionContract.Decode(PostgreSqlApplicationConnectionContract.Encode(invalid)));
        Assert.Null(PostgreSqlApplicationConnectionContract.Decode(encoded with { Contract = "unknown/v1" }));
        Assert.Null(PostgreSqlApplicationConnectionContract.Decode(encoded with { Key = "other" }));
        foreach (var json in new[]
        {
            encoded.Payload[..^1] + ",\"connectionString\":\"forbidden\"}",
            encoded.Payload.Replace("\"secretId\":", "\"material\":\"forbidden\",\"secretId\":", StringComparison.Ordinal),
            encoded.Payload.Replace(value.Password.SecretVersionId.ToString(), "latest", StringComparison.Ordinal),
            encoded.Payload.Replace("\"password\":{", "\"password\":null,\"unknown\":{", StringComparison.Ordinal)
        }) Assert.Null(PostgreSqlApplicationConnectionContract.Decode(encoded with { Payload = json }));
    }
}
