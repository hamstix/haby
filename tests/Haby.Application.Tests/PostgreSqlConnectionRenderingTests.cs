using System.Text;
using System.Text.Json;
using Haby.Domain;
using Haby.Manifest;
using Haby.PostgreSql;
using Xunit;
using Npgsql;

namespace Haby.Application.Tests;

public sealed class PostgreSqlConnectionRenderingTests
{
    private static ConfigurationObject Value(string json)
    {
        using var document = JsonDocument.Parse(json);
        return (ConfigurationObject)ConfigurationValueMapper.FromValidatedJson(document.RootElement);
    }
    private static ManifestCandidateResult Candidate(string options) => ManifestCandidatePipeline.Prepare(Encoding.UTF8.GetBytes("""
        {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{
        "resources":{"db":{"type":"postgresql.database/v1alpha1","providerRef":"pg","properties":{"name":"example_db"}},"app-login":{"type":"postgresql.application-login/v1alpha1","providerRef":"pg","properties":{"databaseRef":"db","schema":"app","privilegePolicy":"database-owner/v1"}}},
        "components":{"api":{"bindings":{"main":{"target":{"kind":"local-resource","resourceRef":"app-login"},"configurationSlot":"main","options":OPTIONS}}}}}}}
        """.Replace("OPTIONS", options, StringComparison.Ordinal)),
        new([PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition], [], [], []));

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"timeout\":1,\"commandTimeout\":3600,\"sslMode\":\"disable\"}", true)]
    [InlineData("{\"timeout\":300,\"commandTimeout\":1,\"sslMode\":\"require\"}", true)]
    [InlineData("{\"timeout\":200.0}", true)]
    [InlineData("{\"timeout\":2e2}", true)]
    [InlineData("{\"timeout\":0}", false)]
    [InlineData("{\"timeout\":301}", false)]
    [InlineData("{\"timeout\":-1}", false)]
    [InlineData("{\"timeout\":1.1}", false)]
    [InlineData("{\"timeout\":\"200\"}", false)]
    [InlineData("{\"timeout\":null}", false)]
    [InlineData("{\"timeout\":1e999}", false)]
    [InlineData("{\"timeout\":1e-999}", false)]
    [InlineData("{\"timeout\":99999999999999999999999999}", false)]
    [InlineData("{\"commandTimeout\":0}", false)]
    [InlineData("{\"commandTimeout\":3601}", false)]
    [InlineData("{\"sslMode\":\"prefer\"}", false)]
    [InlineData("{\"sslMode\":null}", false)]
    [InlineData("{\"sslMode\":true}", false)]
    [InlineData("{\"commandTimeout\":null}", false)]
    [InlineData("{\"commandTimeout\":30.5}", false)]
    [InlineData("{\"commandTimeout\":\"30\"}", false)]
    [InlineData("{\"commandTimeout\":3.6e3}", true)]
    [InlineData("{\"commandTimeout\":-1}", false)]
    [InlineData("{\"timeout\":true}", false)]
    [InlineData("{\"timeout\":[]}", false)]
    [InlineData("{\"connectionTimeoutSeconds\":20}", false)]
    [InlineData("{\"commandTimeoutSeconds\":20}", false)]
    [InlineData("{\"host\":\"localhost\"}", false)]
    [InlineData("{\"password\":\"selector\"}", false)]
    [InlineData("{\"Options\":\"suffix\"}", false)]
    [InlineData("{\"Search Path\":\"other\"}", false)]
    [InlineData("{\"schema\":\"other\"}", false)]
    [InlineData("{\"SecretRef\":\"other\"}", false)]
    [InlineData("{\"port\":5432}", false)]
    [InlineData("{\"database\":\"other\"}", false)]
    [InlineData("{\"username\":\"other\"}", false)]
    [InlineData("{\"suffix\":\";Password=other\"}", false)]
    [InlineData("{\"Pooling\":false}", false)]
    public void Partial_schema_and_typed_options_agree(string json, bool accepted)
    {
        Assert.Equal(accepted, PostgreSqlConsumerOptions.Validate(Value(json)));
        Assert.Equal(accepted, Candidate(json) is ManifestCandidateResult.Valid);
    }

    [Theory]
    [InlineData("{\"timeout\":0}", false)]
    [InlineData("{\"commandTimeout\":null}", false)]
    [InlineData("{\"sslMode\":\"prefer\"}", false)]
    [InlineData("{\"timeout\":15,\"sslMode\":\"require\"}", true)]
    [InlineData("{}", true)]
    public void Planning_validates_each_partial_layer_even_when_binding_replaces_it(string profileOptions, bool accepted)
    {
        var manifest = Assert.IsType<ManifestCandidateResult.Valid>(Candidate("{\"timeout\":2e2,\"commandTimeout\":45,\"sslMode\":\"disable\"}")).Candidate.Declaration;
        var profile = ProfilePayloadParser.ParseProfile(Encoding.UTF8.GetBytes("""
            {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"layout","revision":1},"spec":{
            "documents":{"settings":{"fileName":"appsettings.json","format":"json","template":{}}},
            "bindingRules":{"connection":{"slot":"main","rendererRef":"postgresql.npgsql-connection/v1alpha1","options":OPTIONS,
            "documentProjections":[{"documentRef":"settings","path":"/Connection"}]}}}}
            """.Replace("OPTIONS", profileOptions, StringComparison.Ordinal))).Declaration!;
        var scopes = ConfigurationPlanningSnapshot.Targets(manifest).Select((t, i) => new ConfigurationPlanningScope(t,
            i == 0 ? AssignmentToken.New() : null, new(t, i == 0 ? [new ProfileDecision.Use(profile.Reference)] : [])));
        var result = ConfigurationPlanningSnapshot.Prepare(ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)),
            manifest, scopes, [profile], PostgreSqlConnectionRendering.CreateCatalog());
        Assert.Equal(accepted, result.Snapshot is not null);
        if (!accepted) { Assert.Contains(result.Expansion.Diagnostics, x => x.Code == "render.options.invalid"); return; }
        var pin = Assert.Single(result.Snapshot!.RenderPins);
        Assert.Equal(new PostgreSqlConsumerSettings(200, 45, "disable").ToValue(), pin.Options);
        Assert.All(pin.OptionOrigins.Values, x => Assert.Equal(ProfileOptionScope.Binding, x));
        Assert.Equal(result.Snapshot, ConfigurationPlanningSnapshotCodec.Decode(ConfigurationPlanningSnapshotCodec.Encode(result.Snapshot), result.Snapshot.Revision));
    }

    [Theory]
    [InlineData("verify-full", SslMode.VerifyFull)]
    [InlineData("require", SslMode.Require)]
    [InlineData("disable", SslMode.Disable)]
    public void Pure_formatting_preserves_quoting_and_explicit_settings_without_search_path(string mode, SslMode driverMode)
    {
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        var output = PostgreSqlApplicationConnectionContract.Encode(new("localhost", 5432, "example", "app", "haby_a_" + new string('a', 48), reference));
        var settings = new PostgreSqlConsumerSettings(200, 45, mode).ToValue();
        var fragment = new PostgreSqlConnectionRenderer().Render(output, settings)!;
        var deferred = Assert.IsType<ConfigurationDeferredValue>(fragment.Value);
        Assert.Equal(reference, Assert.IsType<ConfigurationSecretReference>(deferred.Arguments.Members["password"]).Reference);
        Assert.False(ConfigurationJson.Serialize(fragment.Value).IsSuccess);
        Assert.Equal(new[] { "" }, ConfigurationJson.Preview(fragment.Value).RedactedPaths);
        foreach (var material in new[] { "semi;colon", "equals=value", "double\"single'", "back\\slash", "пароль🔑" })
        {
            var result = new PostgreSqlConnectionFormatter().Format(deferred.Arguments, material)!;
            Assert.DoesNotContain(material, result.ToString());
            var parsed = new NpgsqlConnectionStringBuilder(result.Reveal());
            Assert.True(parsed.Password == material, "Password round-trip failed.");
            Assert.Equal(200, parsed.Timeout); Assert.Equal(45, parsed.CommandTimeout); Assert.Equal(driverMode, parsed.SslMode);
            Assert.Null(parsed.SearchPath); Assert.Null(parsed.Options); Assert.Null(parsed.Passfile);
            Assert.Equal(GssEncryptionMode.Disable, parsed.GssEncryptionMode);
        }
        Assert.Null(new PostgreSqlConnectionFormatter().Format(deferred.Arguments, ""));
        Assert.Null(new PostgreSqlConnectionRenderer().Render(output with { Payload = "{}" }, settings));
        Assert.Null(new PostgreSqlConnectionRenderer().Render(output, Value("{\"timeout\":200}")));
    }
}
