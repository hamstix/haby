using System.Text.Json.Nodes;
using Haby.Domain;
using Haby.Tests;
using Xunit;
using static Haby.Tests.ProfilePersistenceSamples;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class ProfileSnapshotCodecTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Complete_roundtrip_preserves_ordinary_values_exact_numbers_and_secret_declarations(bool secrets)
    {
        var source = Node(secrets); var expected = Parse(source);
        var snapshot = ProfileSnapshotCodec.Encode(expected);
        var restored = Decode(snapshot);
        Assert.Equal(expected, restored);
        Assert.Equal(expected.GetHashCode(), restored.GetHashCode());
        Assert.Equal(expected, Parse(Equivalent(source)));
        var seed = Assert.IsType<ConfigurationArray>(Assert.IsType<ConfigurationObject>(restored.Documents.Single().Template).Members["seed"]);
        Assert.Equal(ConfigurationNumber.Parse("1.234567890123456789012345678901e999"), seed.Items[2]);
        Assert.IsType<ConfigurationObject>(seed.Items[3]);
        Assert.Equal(ConfigurationNumber.Parse("1e-999"), Assert.IsType<ConfigurationObject>(restored.Contributions.Single(x => x.Id.Value == "logging").Value).Members["tiny"]);
        if (secrets)
        {
            var projections = restored.Contributions.Single(x => x.Id.Value == "telemetry").SecretProjections;
            Assert.Collection(projections,
                p => { Assert.Equal("/Auth/Token", p.Path); Assert.Equal(ProfileSecretFormat.String, p.Format); Assert.Equal("future-token", p.SecretRef.Name); Assert.Equal(3, p.SecretRef.Version); },
                p => { Assert.Equal("/Otlp", p.Path); Assert.Equal(ProfileSecretFormat.Json, p.Format); Assert.Equal("future-otel", p.SecretRef.Name); Assert.Equal(7, p.SecretRef.Version); });
        }
        source["spec"] = new JsonObject();
        Assert.Equal(expected, restored);
    }

    [Fact]
    public void Semantic_caller_collections_are_copied_before_encoding_and_remain_immutable()
    {
        var expected = Parse(Node());
        var documents = expected.Documents.ToList(); var contributions = expected.Contributions.ToList(); var rules = expected.Rules.ToList();
        var secrets = contributions[1].SecretProjections.ToList();
        var original = contributions[1];
        contributions[1] = new(original.Id, original.Document, original.Path, original.Value, secrets);
        var profile = new ConfigurationProfileRevision(expected.Reference, documents, contributions, rules);
        documents.Clear(); contributions.Clear(); rules.Clear(); secrets.Clear();
        Assert.Equal(expected, profile);
        Assert.Equal(expected, Decode(ProfileSnapshotCodec.Encode(profile)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    public void Independent_format_and_model_versions_are_strict(int format, int model) =>
        Refuses(() => ProfileSnapshotCodec.Decode(ProfileSnapshotCodec.Encode(Parse(Node())), "layout", 1, format, model));

    [Theory]
    [InlineData("id", "another")]
    [InlineData("revision", "2")]
    [InlineData("format", "2")]
    [InlineData("model", "2")]
    public void Stored_header_mismatch_refuses(string member, string value)
    {
        var node = Snapshot(); node[member] = member == "id" ? JsonValue.Create(value) : JsonValue.Create(int.Parse(value));
        Refuses(() => Decode(node.ToJsonString()));
    }

    public static IEnumerable<object[]> RequiredObjects()
    {
        yield return [""];
        yield return ["documents/settings"];
        yield return ["contributions/telemetry"];
        yield return ["contributions/telemetry/secrets/0"];
        yield return ["rules/connection"];
        yield return ["rules/connection/projections/0"];
    }

    [Theory, MemberData(nameof(RequiredObjects))]
    public void Every_internal_field_is_required_and_unknown_members_refuse(string path)
    {
        var original = At(Snapshot(), path).AsObject();
        foreach (var key in original.Select(x => x.Key))
        {
            var missing = Snapshot(); At(missing, path).AsObject().Remove(key);
            Refuses(() => Decode(missing.ToJsonString()));
            var nulled = Snapshot(); At(nulled, path)[key] = null;
            // Ordinary contribution value/template null are valid, not a missing field/default.
            if (key is not ("value" or "template")) Refuses(() => Decode(nulled.ToJsonString()));
        }
        var unknown = Snapshot(); At(unknown, path)["sentinel"] = "private-value";
        Refuses(() => Decode(unknown.ToJsonString()));
    }

    [Theory]
    [InlineData("documents/settings/format", "yaml")]
    [InlineData("documents/settings/file", "../private-value")]
    [InlineData("contributions/telemetry/path", "bad")]
    [InlineData("contributions/telemetry/secrets/0/path", "/Enabled")]
    [InlineData("contributions/telemetry/secrets/0/path", "/Otlp/Child")]
    [InlineData("contributions/telemetry/secrets/0/path", "/Otlp")]
    [InlineData("contributions/telemetry/secrets/0/format", "other")]
    [InlineData("contributions/telemetry/secrets/0/name", "bad_name")]
    [InlineData("rules/connection/projections/0/path", "/bad~2")]
    public void Malformed_structures_refuse_without_echoing_values(string path, string value)
    {
        var node = Snapshot(); Set(node, path, JsonValue.Create(value));
        Refuses(() => Decode(node.ToJsonString()));
    }

    [Fact]
    public void Duplicates_at_any_level_and_invalid_projection_shapes_refuse()
    {
        var snapshot = ProfileSnapshotCodec.Encode(Parse(Node()));
        foreach (var (before, after) in new[]
        {
            ("\"format\":1", "\"format\":1,\"format\":1"),
            ("\"file\":", "\"file\":\"private-value\",\"file\":"),
            ("\"Enabled\":true", "\"Enabled\":true,\"Enabled\":false"),
            ("\"name\":\"future-token\"", "\"name\":\"future-token\",\"name\":\"private-value\"")
        })
        {
            var changed = snapshot.Replace(before, after, StringComparison.Ordinal);
            Assert.NotEqual(snapshot, changed); Refuses(() => Decode(changed));
        }
        var node = Snapshot(); node["rules"]!["connection"]!["projections"]!.AsArray().Clear();
        Refuses(() => Decode(node.ToJsonString()));
        node = Snapshot(); var projections = node["rules"]!["connection"]!["projections"]!.AsArray(); projections.Add(projections[0]!.DeepClone());
        Refuses(() => Decode(node.ToJsonString()));
        node = Snapshot(); node["contributions"]!["telemetry"]!["value"] = new JsonArray();
        Refuses(() => Decode(node.ToJsonString()));
        node = Snapshot(); node["contributions"]!["telemetry"]!["secrets"]![0]!["version"] = 0;
        Refuses(() => Decode(node.ToJsonString()));
        Refuses(() => Decode("{bad private-value"));
    }

    [Fact]
    public void Valid_privileged_content_substitution_is_within_catalog_trust_boundary()
    {
        var node = Snapshot(); node["rules"]!["connection"]!["options"]!["timeout"] = 42;
        Assert.NotEqual(Parse(Node()), Decode(node.ToJsonString()));
    }

    [Fact]
    public void Stored_collection_limits_and_semantic_confidential_nodes_refuse()
    {
        var node = Snapshot();
        var documents = node["documents"]!.AsObject();
        for (var i = 0; i < 32; i++) documents["document-" + i] = documents["settings"]!.DeepClone();
        Refuses(() => Decode(node.ToJsonString()));
        node = Snapshot();
        var rules = node["rules"]!.AsObject();
        for (var i = 0; i < 32; i++) rules["rule-" + i] = rules["connection"]!.DeepClone();
        Refuses(() => Decode(node.ToJsonString()));
        node = Snapshot();
        var contributions = node["contributions"]!.AsObject();
        for (var i = 0; i < 253; i++) contributions["contribution-" + i] = contributions["logging"]!.DeepClone();
        // 255 contributions + two secrets + two projections exceeds the shared 256 budget.
        Refuses(() => Decode(node.ToJsonString()));

        var secret = new ConfigurationSecretReference(new(SecretId.New(), SecretVersionId.New()));
        var profile = new ConfigurationProfileRevision(Parse(Node()).Reference,
            [new(ManifestLocalId.Create("settings"), "settings.json", StructuredDocumentFormat.Json, secret)], [], []);
        Refuses(() => ProfileSnapshotCodec.Encode(profile));
    }

    private static JsonObject Snapshot() => JsonNode.Parse(ProfileSnapshotCodec.Encode(Parse(Node())))!.AsObject();
    private static ConfigurationProfileRevision Decode(string snapshot) => ProfileSnapshotCodec.Decode(snapshot, "layout", 1, 1, 1);
    private static JsonNode At(JsonNode node, string path)
    {
        if (path.Length == 0) return node;
        foreach (var part in path.Split('/')) node = node is JsonArray array ? array[int.Parse(part)]! : node[part]!;
        return node;
    }
    private static void Set(JsonNode node, string path, JsonNode? value)
    {
        var index = path.LastIndexOf('/'); At(node, path[..index])[path[(index + 1)..]] = value;
    }
    private static void Refuses(Action action)
    {
        var error = Assert.Throws<InvalidDataException>(action);
        Assert.Equal("Stored Profile snapshot is invalid or unsupported.", error.Message);
        Assert.Null(error.InnerException);
    }
}
