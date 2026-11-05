using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvus.Text.Json.Validator;
using Haby.Domain;
using Xunit;

namespace Haby.Manifest.Tests;

public sealed class ProfileWireTests
{
    private static readonly ManifestJsonContext Context = ManifestJsonContext.CreateStrict();
    private const string Empty = """{"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"layout","revision":1},"spec":{}}""";
    private static JsonObject Profile(string spec = "{}") => JsonNode.Parse(Empty.Replace("\"spec\":{}", "\"spec\":" + spec, StringComparison.Ordinal))!.AsObject();
    private static ProfileWireResult<ConfigurationProfileRevision> Parse(JsonNode node) => ProfilePayloadParser.ParseProfile(Encoding.UTF8.GetBytes(node.ToJsonString()));
    private static JsonObject Assignment(string target = "{\"kind\":\"environment\"}", string decisions = "[]") => JsonNode.Parse(
        "{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"ConfigurationProfileAssignment\",\"spec\":{\"target\":" + target + ",\"decisions\":" + decisions + "}}")!.AsObject();
    private static ProfileWireResult<ConfigurationProfileAssignment> Assign(JsonNode node) => ProfilePayloadParser.ParseAssignment(Encoding.UTF8.GetBytes(node.ToJsonString()));
    private static JsonObject Secret(string path = "/token", string format = "json", string name = "otel-connection", int version = 1) => new()
    { ["path"] = path, ["format"] = format, ["secretRef"] = new JsonObject { ["name"] = name, ["version"] = version } };
    private static JsonObject WithSecret(string value = "{}", params JsonObject[] projections) => Profile(new JsonObject
    {
        ["staticContributions"] = new JsonObject
        {
            ["otel"] = new JsonObject
            { ["documentRef"] = "settings", ["path"] = "/OpenTelemetry", ["value"] = JsonNode.Parse(value), ["secretProjections"] = new JsonArray(projections.Select(x => (JsonNode)x).ToArray()) }
        }
    }.ToJsonString());
    private static bool SchemaValid(JsonNode node, bool assignment = false)
    {
        var name = assignment ? "configuration-profile-assignment" : "configuration-profile";
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(Root(), "schemas", name + ".v1alpha1.json")),
            "https://haby.dev/schemas/" + name + ".v1alpha1.json", new JsonSchema.Options(allowFileSystemAndHttpResolution: false));
        return schema.Validate(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HamstixHaby.slnx"))) directory = directory.Parent;
        return directory!.FullName;
    }

    [Fact]
    public void Defaults_exact_numbers_and_magic_objects_map_to_ordinary_immutable_values()
    {
        var empty = Parse(Profile()).Declaration!;
        var explicitEmpty = Parse(Profile("""{"documents":{},"staticContributions":{},"bindingRules":{}}""")).Declaration!;
        Assert.Equal(empty, explicitEmpty);
        Assert.Equal(empty.GetHashCode(), explicitEmpty.GetHashCode());
        var node = Profile("""{"staticContributions":{"opaque":{"documentRef":"elsewhere","path":"/x","value":{"kind":"SecretReference","arguments":{"secret":"ordinary"},"number":1.234567890123456789012345678901e999,"null":null}}}}""");
        Assert.True(SchemaValid(node));
        var dto = JsonSerializer.Deserialize(node.ToJsonString(), Context.ConfigurationProfileDocument)!;
        var accepted = Parse(node).Declaration!;
        var value = Assert.IsType<ConfigurationObject>(Assert.Single(accepted.Contributions).Value);
        Assert.IsType<ConfigurationObject>(value.Members["arguments"]);
        Assert.Equal(ConfigurationNumber.Parse("1.234567890123456789012345678901e999"), value.Members["number"]);
        node["spec"]!["staticContributions"] = new JsonObject();
        dto.Spec.StaticContributions.Clear();
        Assert.Single(accepted.Contributions);
        Assert.IsType<ConfigurationNull>(value.Members["null"]);
    }

    [Theory]
    [InlineData("apiVersion", "haby.dev/v1alpha2")]
    [InlineData("kind", "Application")]
    [InlineData("environment", "private")]
    public void Closed_root_and_exact_headers_refuse(string member, string value)
    {
        var node = Profile(); node[member] = value;
        Assert.False(SchemaValid(node)); Assert.False(Parse(node).IsSuccess);
    }

    [Theory]
    [InlineData("apiVersion")]
    [InlineData("kind")]
    [InlineData("metadata")]
    [InlineData("spec")]
    public void Required_root_members_refuse_absence_and_null(string member)
    {
        var node = Profile(); node.Remove(member);
        Assert.False(SchemaValid(node)); Assert.False(Parse(node).IsSuccess);
        node = Profile(); node[member] = null;
        Assert.False(SchemaValid(node)); Assert.False(Parse(node).IsSuccess);
    }

    [Theory]
    [InlineData("documents")]
    [InlineData("staticContributions")]
    [InlineData("bindingRules")]
    public void Optional_maps_refuse_null_and_invalid_keys(string member)
    {
        var node = Profile(); node["spec"]![member] = null;
        Assert.False(SchemaValid(node)); Assert.False(Parse(node).IsSuccess);
        node["spec"]![member] = new JsonObject { ["Invalid"] = new JsonObject() };
        Assert.False(SchemaValid(node)); Assert.False(Parse(node).IsSuccess);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("2147483648", false)]
    [InlineData("1.5", false)]
    [InlineData("1e999999", false)]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("1e0", true)]
    [InlineData("2147483647", true)]
    public void Revisions_have_exact_positive_Int32_semantics(string number, bool valid)
    {
        var node = Profile(); node["metadata"]!["revision"] = JsonNode.Parse(number);
        Assert.Equal(valid, SchemaValid(node)); Assert.Equal(valid, Parse(node).IsSuccess);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Bad", false)]
    [InlineData("valid\n", false)]
    [InlineData("a..b", false)]
    [InlineData("a.b-c", true)]
    public void Profile_ID_rules_match_domain(string id, bool valid)
    {
        var node = Profile(); node["metadata"]!["id"] = id;
        Assert.Equal(valid, SchemaValid(node)); Assert.Equal(valid, Parse(node).IsSuccess);
    }

    [Theory]
    [InlineData("/", "{}", true)]
    [InlineData("/a~1b/~0/0", "{}", true)]
    [InlineData("/0/-", "{}", true)]
    [InlineData("", "{}", false)]
    [InlineData("relative", "{}", false)]
    [InlineData("/bad~2", "{}", false)]
    [InlineData("/token", "null", false)]
    [InlineData("/token", "[]", false)]
    [InlineData("/token", "1", false)]
    [InlineData("/token", "{\"token\":null}", false)]
    [InlineData("/token/child", "{\"token\":[]}", false)]
    [InlineData("/token/child", "{\"token\":false}", false)]
    public void Relative_secret_insertion_is_object_only(string path, string value, bool valid)
    {
        var result = Parse(WithSecret(value, Secret(path)));
        Assert.Equal(valid, result.IsSuccess);
        if (!valid) Assert.Null(result.Declaration);
    }

    [Theory]
    [InlineData("/token", "/token")]
    [InlineData("/token", "/token/child")]
    [InlineData("/token/child", "/token")]
    public void Complete_secret_path_set_refuses_duplicates_and_overlap(string first, string second)
    {
        Assert.False(Parse(WithSecret("{}", Secret(first), Secret(second, "string"))).IsSuccess);
    }

    [Fact]
    public void Secret_metadata_roundtrip_equality_hash_catalog_inputs_and_ownership_are_exact()
    {
        var original = WithSecret("{}", Secret("/a"), Secret("/b", "string"));
        var parsed = Parse(original).Declaration!;
        var reversed = WithSecret("{}", Secret("/b", "string"), Secret("/a"));
        Assert.Equal(parsed, Parse(reversed).Declaration);
        Assert.Equal(parsed.GetHashCode(), Parse(reversed).Declaration!.GetHashCode());
        var dto = JsonSerializer.Deserialize(original.ToJsonString(), Context.ConfigurationProfileDocument)!;
        var roundtrip = JsonSerializer.Serialize(dto, Context.ConfigurationProfileDocument);
        Assert.Equal(parsed, ProfilePayloadParser.ParseProfile(Encoding.UTF8.GetBytes(roundtrip)).Declaration);
        foreach (var changed in new[] { Secret("/different"), Secret("/a", "string"), Secret("/a", name: "other"), Secret("/a", version: 2) })
            Assert.NotEqual(parsed, Parse(WithSecret("{}", changed, Secret("/b", "string"))).Declaration);
        var source = new List<ProfileSecretProjection> { new("/a", ProfileSecretFormat.Json, new("otel-connection", 1)) };
        var contribution = new ProfileStaticContribution(ManifestLocalId.Create("c"), ManifestLocalId.Create("d"), "/x", new ConfigurationObject([]), source);
        source.Clear(); Assert.Single(contribution.SecretProjections);
        Assert.Equal("otel-connection", contribution.SecretProjections[0].SecretRef.Name);
    }

    [Theory]
    [InlineData("secretProjections")]
    [InlineData("path")]
    [InlineData("format")]
    [InlineData("secretRef")]
    [InlineData("name")]
    [InlineData("version")]
    public void Secret_shapes_refuse_required_null_or_foreign_members(string member)
    {
        var node = WithSecret("{}", Secret());
        var contribution = node["spec"]!["staticContributions"]!["otel"]!;
        var secret = contribution["secretProjections"]![0]!;
        if (member == "secretProjections") contribution[member] = null;
        else if (member is "name" or "version") secret["secretRef"]![member] = null;
        else secret[member] = null;
        Assert.False(SchemaValid(node)); Assert.False(Parse(node).IsSuccess);
        node = WithSecret("{}", Secret());
        node["spec"]!["staticContributions"]!["otel"]!["secretProjections"]![0]!["secretRef"]!["internalId"] = "not-allowed";
        Assert.False(SchemaValid(node)); Assert.False(Parse(node).IsSuccess);
    }

    [Theory]
    [InlineData("{\"kind\":\"environment\"}", true)]
    [InlineData("{\"applicationId\":\"sample\",\"kind\":\"application\"}", true)]
    [InlineData("{\"componentRef\":\"api\",\"applicationId\":\"sample\",\"kind\":\"component\"}", true)]
    [InlineData("{\"kind\":\"environment\",\"applicationId\":\"sample\"}", false)]
    [InlineData("{\"kind\":\"application\"}", false)]
    [InlineData("{\"kind\":\"component\",\"applicationId\":\"sample\"}", false)]
    [InlineData("{\"kind\":\"latest\"}", false)]
    public void Assignment_targets_are_explicit_closed_unions(string target, bool valid)
    {
        var node = Assignment(target);
        Assert.Equal(valid, SchemaValid(node, true)); Assert.Equal(valid, Assign(node).IsSuccess);
        if (valid)
        {
            Assert.Empty(Assign(node).Declaration!.Decisions);
            Assert.NotNull(JsonSerializer.Deserialize(node.ToJsonString(), Context.ConfigurationProfileAssignmentDocument));
        }
    }

    [Fact]
    public void Assignment_decisions_are_a_set_and_target_identity_is_preserved()
    {
        const string use = """{"profileRef":{"profileId":"layout","revision":1},"kind":"use"}""";
        const string disable = """{"kind":"disable","profileId":"logging"}""";
        var first = Assign(Assignment(decisions: "[" + use + "," + disable + "]")).Declaration!;
        var reverse = Assign(Assignment(decisions: "[" + disable + "," + use + "]")).Declaration!;
        Assert.Equal(first, reverse); Assert.Equal(first.GetHashCode(), reverse.GetHashCode());
        Assert.IsType<ProfileAssignmentTarget.Environment>(first.Target);
        Assert.False(Assign(Assignment(decisions: "[" + use + "," + use + "]")).IsSuccess);
        Assert.False(Assign(Assignment(decisions: "[" + use + "," + disable.Replace("logging", "layout", StringComparison.Ordinal) + "]")).IsSuccess);
        Assert.NotEqual(first, Assign(Assignment("{\"kind\":\"application\",\"applicationId\":\"sample\"}", "[" + use + "," + disable + "]")).Declaration);
    }

    [Theory]
    [InlineData("{\"kind\":\"disable\",\"profileId\":\"layout\",\"profileRef\":{\"profileId\":\"layout\",\"revision\":1}}")]
    [InlineData("{\"kind\":\"use\",\"profileId\":\"layout\"}")]
    [InlineData("{\"kind\":\"use\",\"profileRef\":null}")]
    [InlineData("{\"kind\":\"use\",\"profileRef\":{\"profileId\":\"layout\",\"revision\":0}}")]
    [InlineData("{\"kind\":\"latest\",\"profileId\":\"layout\"}")]
    public void Assignment_foreign_missing_null_and_discriminator_members_refuse(string decision)
    {
        var node = Assignment(decisions: "[" + decision + "]");
        Assert.False(SchemaValid(node, true)); Assert.False(Assign(node).IsSuccess);
    }

    [Fact]
    public void Assignment_required_and_closed_shapes_refuse()
    {
        foreach (var member in new[] { "target", "decisions" })
        {
            var node = Assignment(); node["spec"]!.AsObject().Remove(member);
            Assert.False(SchemaValid(node, true)); Assert.False(Assign(node).IsSuccess);
            node = Assignment(); node["spec"]![member] = null;
            Assert.False(SchemaValid(node, true)); Assert.False(Assign(node).IsSuccess);
        }
        foreach (var member in new[] { "metadata", "environment" })
        {
            var node = Assignment(); node[member] = new JsonObject();
            Assert.False(SchemaValid(node, true)); Assert.False(Assign(node).IsSuccess);
        }
    }

    [Fact]
    public void All_collection_and_aggregate_boundaries_include_secret_targets()
    {
        foreach (var member in new[] { "documents", "bindingRules", "staticContributions" })
        {
            var max = member == "staticContributions" ? 256 : 32;
            var map = new JsonObject();
            for (var i = 0; i <= max; i++)
            {
                map["item-" + i] = JsonNode.Parse(member switch
                {
                    "documents" => "{\"fileName\":\"settings" + i + ".json\",\"format\":\"json\",\"template\":{}}",
                    "bindingRules" => "{\"slot\":\"slot-" + i + "\",\"rendererRef\":\"sample.render/v1\",\"documentProjections\":[{\"documentRef\":\"external\",\"path\":\"/" + i + "\"}]}",
                    _ => "{\"documentRef\":\"external\",\"path\":\"/" + i + "\",\"value\":null}"
                });
                if (i == max - 1) Assert.True(Parse(Profile(new JsonObject { [member] = map.DeepClone() }.ToJsonString())).IsSuccess);
            }
            Assert.False(Parse(Profile(new JsonObject { [member] = map }.ToJsonString())).IsSuccess);
        }
        var secrets = Enumerable.Range(0, 255).Select(i => Secret("/key-" + i)).ToArray();
        var accepted = WithSecret("{}", secrets);
        Assert.True(Parse(accepted).IsSuccess);
        accepted["spec"]!["staticContributions"]!["second"] = JsonNode.Parse("""{"documentRef":"external","path":"/x","value":null}"""u8);
        Assert.False(Parse(accepted).IsSuccess);
        var decisions = new JsonArray(Enumerable.Range(0, 64).Select(i => (JsonNode)new JsonObject { ["kind"] = "disable", ["profileId"] = "p-" + i }).ToArray());
        var assignment = Assignment(decisions: decisions.ToJsonString()); Assert.True(Assign(assignment).IsSuccess);
        assignment["spec"]!["decisions"]!.AsArray().Add(new JsonObject { ["kind"] = "disable", ["profileId"] = "extra" });
        Assert.False(Assign(assignment).IsSuccess);
        var rule = Profile("""{"bindingRules":{"main":{"slot":"main","rendererRef":"sample.render/v1","documentProjections":[{"documentRef":"d","path":"/x"}]}}}""");
        Assert.True(Parse(rule).IsSuccess);
        rule["spec"]!["bindingRules"]!["main"]!["options"] = null; Assert.False(Parse(rule).IsSuccess);
        rule["spec"]!["bindingRules"]!["main"]!["options"] = new JsonObject();
        rule["spec"]!["bindingRules"]!["main"]!["documentProjections"]!.AsArray().Add(JsonNode.Parse("""{"path":"/x","documentRef":"d"}"""u8));
        Assert.False(Parse(rule).IsSuccess);
    }

    [Theory]
    [InlineData("{\"x\":1,\"x\":2}")]
    [InlineData("{\"x\":1,\"\\u0078\":2}")]
    [InlineData("{\"x\":\"\\uD800\"}")]
    [InlineData("{\"\\uD800\":1}")]
    [InlineData("{\"x\":NaN}")]
    [InlineData("{\"x\":Infinity}")]
    [InlineData("{\"x\":1,}")]
    [InlineData("{/*comment*/}")]
    public void Strict_raw_intake_refuses_before_dto_loses_information(string value)
    {
        var json = Empty.Replace("\"spec\":{}", "\"spec\":{\"staticContributions\":{\"c\":{\"documentRef\":\"d\",\"path\":\"/x\",\"value\":" + value + "}}}", StringComparison.Ordinal);
        Assert.False(ProfilePayloadParser.ParseProfile(Encoding.UTF8.GetBytes(json)).IsSuccess);
    }

    [Fact]
    public void Encoding_depth_trailing_content_and_size_limits_are_safe()
    {
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes(Empty + "{}"), new byte[] { 0xFF }, Encoding.Unicode.GetBytes(Empty), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Empty)).ToArray(),
            Encoding.UTF8.GetBytes(new string('[', 65) + "0" + new string(']', 65)), new byte[ProfilePayloadParser.MaxPayloadBytes + 1] })
        {
            var result = ProfilePayloadParser.ParseProfile(bytes); Assert.False(result.IsSuccess); Assert.Null(result.Declaration);
            Assert.False(ProfilePayloadParser.ParseAssignment(bytes).IsSuccess);
        }
    }

    [Fact]
    public void Diagnostics_count_all_semantic_errors_and_are_bounded_deterministic_and_value_free()
    {
        var contributions = new JsonObject();
        for (var i = 0; i < 110; i++) contributions["c-" + i] = new JsonObject
        { ["documentRef"] = "d", ["path"] = "/x", ["value"] = new JsonObject { ["leaf"] = "synthetic-private-value" }, ["secretProjections"] = new JsonArray(Secret("/leaf")) };
        var node = Profile(new JsonObject { ["staticContributions"] = contributions }.ToJsonString());
        var result = Parse(node); Assert.False(result.IsSuccess); Assert.Null(result.Declaration);
        Assert.Equal(110, result.ErrorCount); Assert.Equal(100, result.Diagnostics.Count); Assert.True(result.IsTruncated);
        Assert.DoesNotContain(result.Diagnostics, x => x.ToString().Contains("synthetic-private-value", StringComparison.Ordinal));
        Assert.Equal(result.Diagnostics, Parse(node).Diagnostics);
    }
    [Fact]
    public void Checked_in_examples_agree_with_schema_DTO_and_semantic_acceptance()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "docs", "examples", "configuration-profiles"), "*.json"))
        {
            var node = JsonNode.Parse(File.ReadAllText(file))!;
            var assignment = node["kind"]!.GetValue<string>() == "ConfigurationProfileAssignment";
            Assert.True(SchemaValid(node, assignment));
            if (assignment)
            {
                Assert.NotNull(JsonSerializer.Deserialize(node.ToJsonString(), Context.ConfigurationProfileAssignmentDocument));
                Assert.True(Assign(node).IsSuccess);
            }
            else
            {
                Assert.NotNull(JsonSerializer.Deserialize(node.ToJsonString(), Context.ConfigurationProfileDocument));
                Assert.True(Parse(node).IsSuccess);
            }
        }
    }

    [Fact]
    public void Projection_and_map_order_and_optional_empty_values_have_identical_semantics()
    {
        var node = Profile("""{"staticContributions":{"c":{"documentRef":"external","path":"/x","value":null}},"bindingRules":{"r":{"slot":"main","rendererRef":"sample.renderer/v1","documentProjections":[{"documentRef":"external","path":"/b"},{"path":"/a","documentRef":"external"}]}}}""");
        var first = Parse(node).Declaration!;
        node["spec"]!["staticContributions"]!["c"]!["secretProjections"] = new JsonArray();
        var rule = node["spec"]!["bindingRules"]!["r"]!;
        rule["options"] = new JsonObject();
        var projections = rule["documentProjections"]!.AsArray();
        rule["documentProjections"] = new JsonArray(projections.Reverse().Select(x => x!.DeepClone()).ToArray());
        var second = Parse(node).Declaration!;
        Assert.Equal(first, second); Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.IsType<ConfigurationNull>(Assert.Single(second.Contributions).Value);
        Assert.Empty(Assert.Single(second.Contributions).SecretProjections);
        rule["documentProjections"] = new JsonArray(); Assert.False(Parse(node).IsSuccess);
    }

    [Theory]
    [InlineData("json", "otel-connection", 1, true)]
    [InlineData("string", "otel-connection", 2147483647, true)]
    [InlineData("JSON", "otel-connection", 1, false)]
    [InlineData("latest", "otel-connection", 1, false)]
    [InlineData("json", "bad.name", 1, false)]
    [InlineData("json", "Bad", 1, false)]
    [InlineData("json", "otel-connection", 0, false)]
    [InlineData("json", "otel-connection", -1, false)]
    public void Named_secret_format_name_and_version_have_closed_exact_rules(string format, string name, int version, bool valid)
    {
        var node = WithSecret("{}", Secret(format: format, name: name, version: version));
        Assert.Equal(valid, SchemaValid(node)); Assert.Equal(valid, Parse(node).IsSuccess);
    }

    [Fact]
    public void Binding_projection_limit_and_combined_static_binding_secret_count_are_enforced()
    {
        var projections = new JsonArray(Enumerable.Range(0, 256).Select(i => (JsonNode)new JsonObject { ["documentRef"] = "external", ["path"] = "/key-" + i }).ToArray());
        var node = Profile(new JsonObject
        {
            ["bindingRules"] = new JsonObject
            {
                ["main"] = new JsonObject
                { ["slot"] = "main", ["rendererRef"] = "sample.render/v1", ["documentProjections"] = projections }
            }
        }.ToJsonString());
        Assert.True(Parse(node).IsSuccess);
        node["spec"]!["bindingRules"]!["main"]!["documentProjections"]!.AsArray().Add(new JsonObject { ["documentRef"] = "external", ["path"] = "/extra" });
        Assert.False(Parse(node).IsSuccess);
        node = WithSecret("{}", Secret());
        node["spec"]!["bindingRules"] = new JsonObject
        {
            ["main"] = new JsonObject
            { ["slot"] = "main", ["rendererRef"] = "sample.render/v1", ["documentProjections"] = new JsonArray(Enumerable.Range(0, 254).Select(i => (JsonNode)new JsonObject { ["documentRef"] = "external", ["path"] = "/key-" + i }).ToArray()) }
        };
        Assert.True(Parse(node).IsSuccess);
        node["spec"]!["bindingRules"]!["main"]!["documentProjections"]!.AsArray().Add(new JsonObject { ["documentRef"] = "external", ["path"] = "/extra" });
        Assert.Contains(Parse(node).Diagnostics, x => x.Code == "profile.declaration.limit-exceeded");
    }

}
