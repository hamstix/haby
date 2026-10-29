using System.Text;
using System.Text.Json.Nodes;
using Corvus.Text.Json.Validator;
using Haby.Application;
using Haby.PostgreSql;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ProviderDeclarationTests
{
    public const string Valid = """
        {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"pg-main","environment":"pilot"},
        "spec":{"revision":1,"providerKind":"postgresql/v1","configurationContract":"haby.postgresql.connection/v1",
        "implementationId":"haby.postgresql.database/v1alpha1","properties":{"host":"unreachable.invalid","port":5432,
        "maintenanceDatabase":"postgres","administratorUserName":"admin"},
        "secretRefs":{"administrator-password":{"secretName":"admin","version":1}}}}
        """;
    private static readonly PostgreSqlProviderDeclarationAdapter Adapter = new(new(new PostgreSqlDatabaseCheckpointCapability()));

    [Fact]
    public void Required_members_and_nulls_agree_with_checked_in_schema_and_typed_input()
    {
        var schema = Schema();
        Assert.True(Accepted(Valid));
        Assert.True(schema.Validate(Encoding.UTF8.GetBytes(Valid)));
        foreach (var path in new[] { "", "metadata", "spec", "spec.properties", "spec.secretRefs.administrator-password" })
        {
            var original = JsonNode.Parse(Valid)!;
            var target = At(original, path).AsObject();
            foreach (var field in target.Select(x => x.Key).ToArray())
                foreach (var remove in new[] { false, true })
                {
                    var node = JsonNode.Parse(Valid)!;
                    var obj = At(node, path).AsObject();
                    if (remove) obj.Remove(field); else obj[field] = null;
                    var json = node.ToJsonString();
                    Assert.False(schema.Validate(Encoding.UTF8.GetBytes(json)), path + "." + field);
                    Assert.False(Accepted(json), path + "." + field);
                }
            var unknown = JsonNode.Parse(Valid)!;
            At(unknown, path)["inlinePassword"] = "never-echo";
            Assert.False(schema.Validate(Encoding.UTF8.GetBytes(unknown.ToJsonString())));
            Assert.False(Accepted(unknown.ToJsonString()));
        }
    }

    [Theory]
    [InlineData("metadata.name", "Bad")]
    [InlineData("metadata.environment", "")]
    [InlineData("spec.revision", "0")]
    [InlineData("spec.revision", "9223372036854775808")]
    [InlineData("spec.revision", "1.5")]
    [InlineData("spec.properties.port", "0")]
    [InlineData("spec.properties.port", "65536")]
    [InlineData("spec.secretRefs.administrator-password.version", "0")]
    [InlineData("spec.secretRefs.administrator-password.version", "2147483648")]
    public void Scalar_boundaries_agree(string path, string value)
    {
        var node = JsonNode.Parse(Valid)!;
        var parts = path.Split('.');
        At(node, string.Join('.', parts[..^1]))[parts[^1]] = char.IsDigit(value.FirstOrDefault()) ? JsonNode.Parse(value) : JsonValue.Create(value);
        var json = node.ToJsonString();
        Assert.False(Schema().Validate(Encoding.UTF8.GetBytes(json)));
        Assert.False(Accepted(json));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("100e-2")]
    [InlineData("9223372036854775807")]
    public void Mathematical_integer_revisions_agree(string revision)
    {
        var json = Valid.Replace("\"revision\":1", "\"revision\":" + revision, StringComparison.Ordinal);
        Assert.True(Schema().Validate(Encoding.UTF8.GetBytes(json)));
        Assert.True(Accepted(json));
    }

    [Theory]
    [InlineData("{\"metadata\":null}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"apiVersion\":\"\\uD800\"}")]
    public void Malformed_input_fails_safely(string json) => Assert.Null(ProviderDeclarationInput.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Duplicates_sizes_invalid_endpoints_and_registration_conflicts_fail_closed()
    {
        Assert.False(Accepted(Valid.Replace("\"port\":5432", "\"port\":5432,\"port\":5433", StringComparison.Ordinal)));
        Assert.False(Accepted(Valid.Replace("unreachable.invalid", "bad/host", StringComparison.Ordinal)));
        Assert.Null(ProviderDeclarationInput.Parse(new byte[ProviderDeclarationInput.MaxBytes + 1]));
        Assert.Throws<InvalidOperationException>(() => new ProviderDeclarationRegistry([Adapter, Adapter], [Adapter.Codec]));
        Assert.Null(new ProviderDeclarationRegistry([], []).Find(Adapter.Key));
    }

    private static bool Accepted(string json)
    {
        var declaration = ProviderDeclarationInput.Parse(Encoding.UTF8.GetBytes(json));
        if (declaration is null) return false;
        try { _ = Adapter.ParseProperties(declaration.Spec.Properties); return true; }
        catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException) { return false; }
    }
    private static JsonNode At(JsonNode node, string path)
    {
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries)) node = node[part]!;
        return node;
    }
    private static JsonSchema Schema()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HamstixHaby.slnx"))) root = root.Parent;
        return JsonSchema.FromText(File.ReadAllText(Path.Combine(root!.FullName, "schemas", "provider.v1alpha1.json")),
            "https://haby.dev/schemas/provider-v1alpha1.schema.json", new JsonSchema.Options(allowFileSystemAndHttpResolution: false));
    }
}
