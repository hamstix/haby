using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Haby.PostgreSql;
using Xunit;

namespace Haby.Application.Tests;

public sealed class PostgreSqlApplicationLoginContractTests
{
    private static DurableOperationNode Node()
    {
        var owner = new ManagedResourceKey(EnvironmentRecordId.New(), ApplicationRecordId.New(), ManifestLocalId.Create("app-login"));
        var database = new OperationOutputRefDocument("db-create", PostgreSqlDatabaseOutputContract.Contract, "database");
        var password = new OperationOutputRefDocument("app-login-material", GeneratedCredentialContracts.OutputContract, "password");
        return new(OperationId.Create("app-login-create"), owner.ResourceLocalId, PostgreSqlApplicationLoginAction.RegistrationKey,
            new(owner.EnvironmentRecordId, ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1), PostgreSqlDatabaseCheckpointCapability.ImplementationId),
            new(PostgreSqlApplicationLoginAction.Format, JsonSerializer.Serialize(new PostgreSqlLoginCreateInput(
                new(owner.EnvironmentRecordId.ToString(), owner.ApplicationRecordId.ToString(), owner.ResourceLocalId.Value), "app_database", database,
                PostgreSqlPairResourceAdmission.RoleName(owner), password), PostgreSqlPairJsonContext.Default.PostgreSqlLoginCreateInput)),
            [OperationId.Create("app-login-material"), OperationId.Create("db-create"), OperationId.Create("db-restrict")])
        { OutputReferences = [password.ToReference(), database.ToReference()] };
    }

    [Fact]
    public void Strict_generated_login_and_evidence_codecs_refuse_overrides_nulls_wrong_names_dependencies_and_selectors()
    {
        var node = Node(); Assert.NotNull(PostgreSqlApplicationLoginAction.Decode(node));
        foreach (var json in new[]
        {
            node.Input.Json.Replace("\"producerOperationId\":\"db-create\"", "\"producerOperationId\":null", StringComparison.Ordinal),
            node.Input.Json[..^1] + ",\"password\":\"forbidden\"}",
            node.Input.Json.Replace("\"owner\":{", "\"owner\":null,\"unknown\":{", StringComparison.Ordinal),
            node.Input.Json.Replace("app-login-material", "other-material", StringComparison.Ordinal),
            node.Input.Json.Replace("haby_a_", "other_", StringComparison.Ordinal),
            node.Input.Json.Replace(PostgreSqlResourceNaming.Algorithm, "unsupported/v9", StringComparison.Ordinal),
            node.Input.Json.Replace("\"naming\":{", "\"naming\":null,\"unknown\":{", StringComparison.Ordinal),
            node.Input.Json.Replace("\"password\"", "\"other\"", StringComparison.Ordinal),
            node.Input.Json.Replace("\"databaseSource\":{", "\"databaseSource\":null,\"unknown\":{", StringComparison.Ordinal)
        }) Assert.Null(PostgreSqlApplicationLoginAction.Decode(node with { Input = new(node.Input.Format, json) }));
        Assert.Null(PostgreSqlApplicationLoginAction.Decode(node with { Dependencies = [] }));
        Assert.Null(PostgreSqlApplicationLoginAction.Decode(node with { OutputReferences = [] }));
        Assert.Null(PostgreSqlApplicationLoginAction.Decode(node with { Input = new("unknown/v1", node.Input.Json) }));
        var input = PostgreSqlApplicationLoginAction.Decode(node)!;
        var valid = PostgreSqlRoleEvidenceContract.Encode(new(input.RoleName, 123));
        var contract = new PostgreSqlRoleEvidenceContract(); Assert.True(contract.Validate(node, valid));
        Assert.Null(PostgreSqlRoleEvidenceContract.Decode(valid with { Payload = valid.Payload[..^1] + ",\"password\":null}" }));
        Assert.Null(PostgreSqlRoleEvidenceContract.Decode(PostgreSqlRoleEvidenceContract.Encode(new(input.RoleName, 0))));
        Assert.False(contract.Validate(node, PostgreSqlRoleEvidenceContract.Encode(new("haby_a_" + new string('0', 48), 123))));
    }

    [Fact]
    public void Saved_login_inputs_require_explicit_naming_and_derivation_data()
    {
        var node = Node();
        foreach (var field in new[] { "naming", "derivation" })
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(node.Input.Json)!.AsObject();
            Assert.True(json.Remove(field));
            Assert.Null(PostgreSqlApplicationLoginAction.Decode(node with { Input = new(node.Input.Format, json.ToJsonString()) }));
        }
    }

    [Fact]
    public void Scram_scope_refuses_arbitrary_material_and_only_emits_the_confidential_postgresql_format()
    {
        foreach (var invalid in new[] { Array.Empty<byte>(), new byte[43], new byte[44], Enumerable.Repeat((byte)'b', 43).ToArray(), Enumerable.Repeat((byte)'\'', 43).ToArray() })
            Assert.Throws<ArgumentException>(() => PostgreSqlScramVerifier.Create(invalid));
        var material = Enumerable.Repeat((byte)'a', 43).ToArray(); material[^1] = (byte)'A';
        var first = PostgreSqlScramVerifier.Create(material); var second = PostgreSqlScramVerifier.Create(material);
        var expectedPrefix = first.StartsWith("SCRAM-SHA-256$4096:", StringComparison.Ordinal);
        Assert.True(expectedPrefix); // Never echo a confidential verifier in an assertion failure.
        Assert.False(first == second); // Fresh salt; password identity remains the saved material.
        Assert.False(first.Contains(new string('a', 43), StringComparison.Ordinal));
    }
}
