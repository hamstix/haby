using System.Text.Json;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ConfigurationCompositionTests
{
    private static readonly ConfigurationFormatterId Connection = ConfigurationFormatterId.Create("sample.connection/v1");
    private static readonly ConfigurationFormatterId Queue = ConfigurationFormatterId.Create("sample.queue-uri/v1");
    private static ConfigurationObject Obj(params (string Key, ConfigurationValue Value)[] members) => new(members.Select(x => KeyValuePair.Create(x.Key, x.Value)));
    private static ConfigurationString Text(string text) => new(text);
    private static ConfigurationProjection Projection(string path, ConfigurationValue value, string source = "binding") => new(path, new(value), ManifestLocalId.Create(source));
    private static ConfigurationSecretReference Secret() => new(new(SecretId.New(), SecretVersionId.New()));

    // Synthetic adapter metadata and typed decoders exercise the real shared seam, without formatting or secret access.
    private sealed record ConnectionArguments(ConfigurationSecretReference Credential, ConfigurationString Consumer)
    {
        public static ConnectionArguments? Read(ConfigurationObject value) => value.Members.Count == 2 &&
            value.Members.GetValueOrDefault("credential") is ConfigurationSecretReference secret &&
            value.Members.GetValueOrDefault("consumer") is ConfigurationString consumer && consumer.Value is "api" or "worker"
                ? new(secret, consumer) : null;
    }
    private sealed record QueueArguments(ConfigurationSecretReference? Credential, ConfigurationNumber Channel)
    {
        public static QueueArguments? Read(ConfigurationObject value) => value.Members.Count is 1 or 2 &&
            (!value.Members.ContainsKey("credential") || value.Members["credential"] is ConfigurationSecretReference) &&
            value.Members.GetValueOrDefault("channel") is ConfigurationNumber channel && channel.Equals(ConfigurationNumber.Parse("1"))
                ? new(value.Members.GetValueOrDefault("credential") as ConfigurationSecretReference, channel) : null;
    }
    private sealed record ConnectionContract(int Revision = 1) : ConfigurationFormatterContract(Connection)
    {
        public override bool ValidateArguments(ConfigurationObject arguments) => ConnectionArguments.Read(arguments) is not null;
    }
    private sealed record QueueContract() : ConfigurationFormatterContract(Queue)
    {
        public override bool ValidateArguments(ConfigurationObject arguments) => QueueArguments.Read(arguments) is not null;
    }
    private sealed record ThrowingContract() : ConfigurationFormatterContract(Connection)
    {
        public override bool ValidateArguments(ConfigurationObject arguments) => throw new InvalidOperationException("sensitive driver data");
    }
    private static ConfigurationFormatterImplementation Active(ConfigurationFormatterId id, string identity = "adapter") => new(ManifestLocalId.Create(identity), id);
    private static ConfigurationFormatterCatalog Catalog() => new([new ConnectionContract(), new QueueContract()], [Active(Connection), Active(Queue)]);
    private static ConfigurationComposer Composer() => new(Catalog());
    private static ConfigurationDeferredValue Deferred(ConfigurationSecretReference secret, string consumer) => new(Connection, Obj(("credential", secret), ("consumer", Text(consumer))));

    [Fact]
    public void Shared_exact_secret_with_consumer_arguments_and_second_typed_contract_compose_and_redact()
    {
        var secret = Secret();
        var api = Deferred(secret, "api");
        var worker = Deferred(secret, "worker");
        var queue = new ConfigurationDeferredValue(Queue, Obj(("credential", secret), ("channel", ConfigurationNumber.Parse("1"))));
        Assert.NotEqual(api, worker);
        Assert.Same(api.Arguments.Members["credential"], worker.Arguments.Members["credential"]);
        var projections = new List<ConfigurationProjection> { Projection("/api", api), Projection("/worker", worker), Projection("/queue", queue) };
        var result = Composer().Compose(Obj(), projections);
        var reversed = Composer().Compose(Obj(), projections.AsEnumerable().Reverse());
        Assert.True(result.IsSuccess);
        Assert.Equal(result.Document, reversed.Document);
        projections.Clear();
        var preview = ConfigurationJson.Preview(result.Document!);
        Assert.Equal(new[] { "/api", "/queue", "/worker" }, preview.RedactedPaths);
        using var json = JsonDocument.Parse(preview.Json);
        Assert.All(json.RootElement.GetProperty("document"u8).EnumerateObject(), x => Assert.Equal(ConfigurationJson.RedactedMarker, x.Value.GetString()));
        Assert.DoesNotContain(secret.Reference.SecretId.ToString(), preview.Json);
        Assert.DoesNotContain("credential", preview.Json);
        Assert.DoesNotContain("consumer", preview.Json);
        Assert.False(ConfigurationJson.Serialize(result.Document!).IsSuccess);
        Assert.Null(ConfigurationJson.Serialize(result.Document!).Json);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, ConfigurationValue>)Assert.IsType<ConfigurationObject>(result.Document).Members).Clear());
    }

    [Fact]
    public void Registration_is_exact_conflicts_and_ambiguity_fail_at_construction_and_definitions_are_idempotent()
    {
        _ = new ConfigurationFormatterCatalog([new ConnectionContract(), new ConnectionContract()], [Active(Connection)]);
        Assert.Throws<ArgumentException>(() => new ConfigurationFormatterCatalog([new ConnectionContract(), new ConnectionContract(2)], []));
        Assert.Throws<ArgumentException>(() => new ConfigurationFormatterCatalog([new ConnectionContract()], [Active(Connection), Active(Connection, "other")]));
        Assert.Throws<ArgumentException>(() => new ConfigurationFormatterCatalog([new ConnectionContract()], [Active(Connection), Active(Connection)]));
        Assert.Throws<ArgumentException>(() => new ConfigurationFormatterCatalog([], [Active(Connection)]));
        var definitions = new List<ConfigurationFormatterContract> { new ConnectionContract() };
        var implementations = new List<ConfigurationFormatterImplementation> { Active(Connection) };
        var catalog = new ConfigurationFormatterCatalog(definitions, implementations);
        definitions.Clear(); implementations.Clear();
        Assert.True(new ConfigurationComposer(catalog).Compose(Deferred(Secret(), "api"), []).IsSuccess);
    }

    [Fact]
    public void Recursive_validation_refuses_unknown_unavailable_and_invalid_arguments_with_safe_deterministic_errors()
    {
        var unknown = new ConfigurationDeferredValue(ConfigurationFormatterId.Create("missing/v1"), Obj(("sensitive", Text("never include this"))));
        var invalid = new ConfigurationDeferredValue(Connection, Obj());
        var invalidQueue = new ConfigurationDeferredValue(Queue, Obj(("credential", Secret()), ("channel", Text("1"))));
        var result = Composer().Compose(Obj(("array", new ConfigurationArray([unknown, invalid, invalidQueue]))), []);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Document);
        Assert.Equal(new[] { "render.formatter.unknown", "render.formatter.arguments-invalid", "render.formatter.arguments-invalid" }, result.Diagnostics.Select(x => x.Code));
        Assert.Equal(new[] { "/array/0", "/array/1", "/array/2" }, result.Diagnostics.Select(x => x.Path));
        Assert.DoesNotContain("never include", string.Join(" ", result.Diagnostics));
        var unavailable = new ConfigurationComposer(new([new ConnectionContract()], [])).Compose(Deferred(Secret(), "api"), []);
        Assert.Equal("render.formatter.unavailable", Assert.Single(unavailable.Diagnostics).Code);
        var throws = new ConfigurationComposer(new([new ThrowingContract()], [Active(Connection)])).Compose(invalid, []);
        Assert.Equal("render.formatter.arguments-invalid", Assert.Single(throws.Diagnostics).Code);
        Assert.DoesNotContain("sensitive", string.Join(" ", throws.Diagnostics));
    }

    [Fact]
    public void Sibling_insertion_preserves_template_fragments_and_handles_escaped_empty_numeric_and_dash_keys()
    {
        var template = Obj(("keep", Text("original")));
        var fragment = Obj(("nested", new ConfigurationArray([Text("value")])));
        var projections = new[] { Projection("/", Text("empty")), Projection("/a~1b/~0/0", fragment), Projection("/a~1b/~0/-", Text("dash")) };
        var result = Composer().Compose(template, projections);
        Assert.True(result.IsSuccess);
        Assert.Equal(result.Document, Composer().Compose(template, projections.Reverse()).Document);
        Assert.Single(template.Members);
        var root = Assert.IsType<ConfigurationObject>(result.Document);
        Assert.Equal(Text("empty"), root.Members[""]);
        var parent = Assert.IsType<ConfigurationObject>(Assert.IsType<ConfigurationObject>(root.Members["a/b"]).Members["~"]);
        Assert.Same(fragment, parent.Members["0"]);
        Assert.True(Composer().Compose(Obj(), [Projection("/a", Text("one")), Projection("/ab", Text("two"))]).IsSuccess);
        Assert.Equal(Text("dash"), parent.Members["-"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("/~2")]
    [InlineData("/~")]
    public void Invalid_projection_paths_refuse_without_echoing_input(string path)
    {
        var result = Composer().Compose(Obj(), [Projection(path, Text("secret"))]);
        Assert.Null(result.Document);
        Assert.Equal(new ConfigurationRenderDiagnostic("render.projection.invalid-path", "", "binding"), Assert.Single(result.Diagnostics));
    }

    [Fact]
    public void Complete_collision_set_and_late_formatter_error_are_order_independent_and_never_publish_partial_tree()
    {
        var template = Obj(("leaf", Text("existing")), ("array", new ConfigurationArray([])), ("secret", Secret()), ("deferred", Deferred(Secret(), "api")));
        var projections = new[] { Projection("/new/ok", Text("ok")), Projection("/leaf", Text("collision")), Projection("/array/0", Text("collision")),
            Projection("/secret/x", Text("collision")), Projection("/deferred/x", Text("collision")), Projection("/a", Obj(), "one"),
            Projection("/a/b", Text("overlap"), "two"), Projection("/a", Text("duplicate"), "three"),
            Projection("/z", new ConfigurationDeferredValue(Connection, Obj())) };
        var result = Composer().Compose(template, projections);
        Assert.Null(result.Document);
        Assert.Equal(8, result.ErrorCount);
        Assert.Equal(result.Diagnostics, Composer().Compose(template, projections.Reverse()).Diagnostics);
        Assert.Equal(4, template.Members.Count);
        Assert.Contains(result.Diagnostics, x => x.Path == "/z" && x.Code == "render.formatter.arguments-invalid");
    }

    [Fact]
    public void Any_root_is_allowed_without_projections_but_non_object_roots_refuse_projections()
    {
        foreach (var root in new ConfigurationValue[] { ConfigurationNull.Instance, Text("literal"), new ConfigurationBoolean(true), ConfigurationNumber.Parse("1"),
            new ConfigurationArray([]), Secret(), Deferred(Secret(), "api") })
        {
            Assert.Same(root, Composer().Compose(root, []).Document);
            Assert.Null(Composer().Compose(root, [Projection("/x", Text("value"))]).Document);
        }
    }

    [Fact]
    public void Reports_are_deduplicated_sorted_bounded_and_cannot_hide_failure()
    {
        var projections = Enumerable.Range(0, 150).Select(i => Projection("/" + i, Text("value"))).ToArray();
        var result = Composer().Compose(Text("root"), projections.Concat(projections));
        Assert.Null(result.Document);
        Assert.Equal(300, result.ErrorCount); // Two distinct codes per canonical target; duplicate source diagnostics collapse.
        Assert.Equal(100, result.Diagnostics.Count);
        Assert.True(result.IsTruncated);
        Assert.Equal(result.Diagnostics, Composer().Compose(Text("root"), projections.Reverse().Concat(projections)).Diagnostics);
    }

    [Fact]
    public void Recursive_preview_redacts_whole_deferred_values_even_without_secrets_and_distinguishes_literal_marker()
    {
        var confidential = new ConfigurationDeferredValue(Queue, Obj(("channel", ConfigurationNumber.Parse("1"))));
        Assert.True(Composer().Compose(confidential, []).IsSuccess);
        var tree = Obj(("a/b~", new ConfigurationArray([Secret(), confidential, Text(ConfigurationJson.RedactedMarker)])));
        var preview = ConfigurationJson.Preview(tree);
        Assert.Equal(new[] { "/a~1b~0/0", "/a~1b~0/1" }, preview.RedactedPaths);
        Assert.DoesNotContain("never exposed", preview.Json);
        Assert.DoesNotContain("public-looking", preview.Json);
        Assert.Empty(ConfigurationJson.Preview(Text(ConfigurationJson.RedactedMarker)).RedactedPaths);
        foreach (var root in new ConfigurationValue[] { Secret(), confidential })
        {
            var rootPreview = ConfigurationJson.Preview(root);
            Assert.Equal(new[] { "" }, rootPreview.RedactedPaths);
            using var json = JsonDocument.Parse(rootPreview.Json);
            Assert.Equal(ConfigurationJson.RedactedMarker, json.RootElement.GetProperty("document"u8).GetString());
            Assert.Null(ConfigurationJson.Serialize(root).Json);
            Assert.Null(ConfigurationJson.Serialize(new ConfigurationArray([Text("prefix"), root])).Json);
        }
        Assert.Throws<NotSupportedException>(() => ((IList<string>)preview.RedactedPaths).Clear());
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("1e999999999999999999999")]
    [InlineData("123456789012345678901234567890.123456789012345678901")]
    [InlineData("-0")]
    [InlineData("-0.00e999")]
    [InlineData("1e-999")]
    public void Ordinary_json_preserves_exact_numbers_as_valid_json(string text)
    {
        var number = ConfigurationNumber.Parse(text);
        var result = ConfigurationJson.Serialize(number);
        Assert.True(result.IsSuccess);
        using var document = JsonDocument.Parse(result.Json!);
        Assert.Equal(number, ConfigurationValueMapper.FromValidatedJson(document.RootElement));
        if (number.Equals(ConfigurationNumber.Parse("0"))) Assert.Equal("0e0", result.Json);
    }

    [Fact]
    public void Ordinary_json_profile_has_ordinal_order_compact_output_and_default_escaping()
    {
        var tree = Obj(("z", new ConfigurationArray([ConfigurationNull.Instance, new ConfigurationBoolean(true)])), ("a", Text("é<\n\"\\")));
        Assert.Equal("{\"a\":\"\\u00E9\\u003C\\n\\u0022\\\\\",\"z\":[null,true]}", ConfigurationJson.Serialize(tree).Json);
        Assert.Equal(ConfigurationJson.Serialize(tree).Json, ConfigurationJson.Serialize(Obj(tree.Members.Reverse().Select(x => (x.Key, x.Value)).ToArray())).Json);
    }
}
