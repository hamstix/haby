using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haby.Manifest;
using Xunit;

namespace Haby.Manifest.Tests;

public class ManifestPayloadParserTests
{
    private const string Minimal = """{"applicationVersion":"1.0.0+build.17","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{}}}""";

    [Fact]
    public void Accepts_the_original_envelope_with_source_generated_DTOs()
    {
        var result = Assert.IsType<ManifestParseResult.Valid>(Parse(Minimal));
        Assert.Equal("1.0.0+build.17", result.ApplicationVersion);
        Assert.Equal("example", result.Manifest.Metadata.Id);
        var again = Assert.IsType<ManifestParseResult.Valid>(Parse(Minimal));
        Assert.Equal(result.ApplicationVersion, again.ApplicationVersion);
        Assert.Equal(result.Manifest.Metadata.Id, again.Manifest.Metadata.Id);
    }

    [Fact]
    public void Accepts_the_complete_contract_fixture()
    {
        var path = RepositoryPath("tests", "Haby.Manifest.Tests", "Corpus", "complete.json");
        Assert.IsType<ManifestParseResult.Valid>(Parse(Envelope(File.ReadAllText(path))));
    }

    [Fact]
    public void Shared_negative_corpus_maps_to_safe_typed_outcomes()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(RepositoryPath("tests", "Haby.Manifest.Tests", "Corpus", "parser-negative.json")));
        foreach (var test in fixture.RootElement.EnumerateArray())
        {
            var payload = test.GetProperty("payload"u8).GetString()!;
            var code = test.GetProperty("code"u8).GetString()!;
            var path = test.GetProperty("path"u8).GetString()!;
            var result = Parse(payload);
            if (test.GetProperty("outcome"u8).GetString() == "request")
            {
                var errors = Assert.IsType<ManifestParseResult.InvalidRequest>(result).Errors;
                Assert.Contains(errors, error => error.Code == code && error.Path == path);
                Assert.DoesNotContain("secret-value", string.Join("", errors.Select(error => error.Message + string.Join("", error.Parameters.Values))));
            }
            else
            {
                var diagnostics = Assert.IsType<ManifestParseResult.InvalidManifest>(result).Report.Diagnostics;
                Assert.Contains(diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Path == path);
                Assert.DoesNotContain("secret-value", string.Join("", diagnostics.Select(diagnostic => diagnostic.Message + string.Join("", diagnostic.Parameters.Values))));
                Assert.DoesNotContain("another-secret", string.Join("", diagnostics.Select(diagnostic => diagnostic.Message + string.Join("", diagnostic.Parameters.Values))));
            }
        }
    }

    [Theory]
    [InlineData("", "manifest-import.invalid-json", "")]
    [InlineData("[]", "manifest-import.type-mismatch", "")]
    [InlineData("{}", "manifest-import.required", "/applicationVersion")]
    [InlineData("""{"applicationVersion":1,"manifest":{}}""", "manifest-import.type-mismatch", "/applicationVersion")]
    [InlineData("""{"applicationVersion":"a","manifest":{},"other":1}""", "manifest-import.unknown-property", "/other")]
    [InlineData("""{"applicationVersion":"a/secret","manifest":{}}""", "manifest-import.invalid-application-version", "/applicationVersion")]
    [InlineData("""{"applicationVersion":"a","applicationVersion":"b","manifest":{}}""", "manifest-import.duplicate-property", "/applicationVersion")]
    [InlineData("""{"applicationVersion":"a","manifest":{},}""", "manifest-import.invalid-json", "")]
    public void Envelope_negative_corpus(string json, string code, string path)
    {
        var errors = Assert.IsType<ManifestParseResult.InvalidRequest>(Parse(json)).Errors;
        Assert.Contains(errors, error => error.Code == code && error.Path == path);
        Assert.All(errors, error => Assert.DoesNotContain("secret", error.Message + string.Join("", error.Parameters.Values), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Comments_inside_manifest_are_manifest_syntax_diagnostics()
    {
        var diagnostic = Assert.Single(Report("""{"applicationVersion":"a","manifest":{/*comment*/}}""").Diagnostics);
        Assert.Equal("manifest.syntax.invalid-json", diagnostic.Code);
        Assert.Equal("", diagnostic.Path);
        Assert.NotNull(diagnostic.Location);
    }

    [Fact]
    public void Application_version_obeys_exact_lexical_bounds()
    {
        foreach (var length in new[] { 1, 128 })
        {
            var version = new string('a', length);
            Assert.IsType<ManifestParseResult.Valid>(Parse(Minimal.Replace("1.0.0+build.17", version, StringComparison.Ordinal)));
        }
        foreach (var version in new[] { new string('a', 129), "a\n", "+a" })
            Assert.Contains(Assert.IsType<ManifestParseResult.InvalidRequest>(Parse(Minimal.Replace("1.0.0+build.17", version.Replace("\n", "\\n"), StringComparison.Ordinal))).Errors,
                error => error.Code == "manifest-import.invalid-application-version");
    }

    [Theory]
    [InlineData("""{"apiVersion":"haby.dev/v2","kind":"Application","metadata":{"id":"example"},"spec":{}}""", "manifest.schema.unsupported-api-version", "/apiVersion")]
    [InlineData("""{"apiVersion":"haby.dev/v1alpha1","kind":"application","metadata":{"id":"example"},"spec":{}}""", "manifest.schema.unsupported-kind", "/kind")]
    [InlineData("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"}}""", "manifest.schema.required", "/spec")]
    [InlineData("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example","bad":true},"spec":{}}""", "manifest.schema.unknown-property", "/metadata/bad")]
    [InlineData("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"resources":{"db":{"providerRef":"p"}}}}""", "manifest.schema.required", "/spec/resources/db/type")]
    [InlineData("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"resources":{"db":{"type":"a/v1","providerRef":1}}}}""", "manifest.schema.type-mismatch", "/spec/resources/db/providerRef")]
    [InlineData("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"resources":{"Bad_Id":{"type":"a/v1","providerRef":"p"}}}}""", "manifest.id.invalid", "/spec/resources/Bad_Id")]
    [InlineData("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"parameters":{"p":{"type":"string","default":"x","defaultExpression":"y"}}}}""", "manifest.parameter.default-conflict", "/spec/parameters/p")]
    [InlineData("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"Bad_Id"},"spec":{}}""", "manifest.id.invalid", "/metadata/id")]
    [InlineData("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"components":{"c":{"bindings":{"b":{"target":{"kind":"local-resource"}}}}}}}""", "manifest.schema.required", "/spec/components/c/bindings/b/target/resourceRef")]
    [InlineData("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"components":{"c":{"bindings":{"b":{"target":{"kind":"unknown"}}}}}}}""", "manifest.schema.invalid-discriminator", "/spec/components/c/bindings/b/target/kind")]
    [InlineData("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"parameters":{"p":{"type":"number","allowedValues":[-0,0]}}}}""", "manifest.parameter.allowed-value-duplicate", "/spec/parameters/p/allowedValues")]
    [InlineData("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"parameters":{"p":{"type":"unknown"}}}}""", "manifest.parameter.type-invalid", "/spec/parameters/p/type")]
    [InlineData("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"components":{"a":{"documents":{"d":{"format":"xml","fileName":"x","template":"x"}}}}}}""", "manifest.schema.invalid-discriminator", "/spec/components/a/documents/d/format")]
    public void Manifest_negative_corpus(string manifest, string code, string path)
    {
        var report = Report(Envelope(manifest));
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Path == path);
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Path.StartsWith("/manifest", StringComparison.Ordinal));
    }

    [Fact]
    public void Too_many_labels_report_the_maxProperties_constraint_at_the_map()
    {
        var labels = string.Join(',', Enumerable.Range(0, 65).Select(i => $"\"label{i}\":\"ok\""));
        var manifest = """{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example","labels":{}},"spec":{}}""";
        manifest = manifest.Replace("\"labels\":{}", "\"labels\":{" + labels + "}", StringComparison.Ordinal);
        var diagnostic = Assert.Single(Report(Envelope(manifest)).Diagnostics);
        Assert.Equal("manifest.schema.constraint-violation", diagnostic.Code);
        Assert.Equal("/metadata/labels", diagnostic.Path);
        Assert.Equal("maxProperties", diagnostic.Parameters["constraint"]);
    }

    [Theory]
    [InlineData("Bad_Key")]
    [InlineData("bad/key")]
    [InlineData("bad~key")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Invalid_label_key_has_one_escaped_member_diagnostic(string key)
    {
        var manifest = """{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example","labels":{}},"spec":{}}""";
        manifest = manifest.Replace("\"labels\":{}", "\"labels\":{\"" + key + "\":\"ok\"}", StringComparison.Ordinal);
        var diagnostic = Assert.Single(Report(Envelope(manifest)).Diagnostics);
        Assert.Equal("manifest.schema.constraint-violation", diagnostic.Code);
        Assert.Equal("/metadata/labels/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal), diagnostic.Path);
        Assert.Equal("propertyNames", diagnostic.Parameters["constraint"]);
    }

    [Theory]
    [InlineData("string", "1", "string")]
    [InlineData("integer", "1.5", "integer")]
    [InlineData("boolean", "\"true\"", "boolean")]
    [InlineData("number", "null", "number")]
    public void Literal_default_type_mismatch_uses_parameter_diagnostic(string type, string value, string expectedType)
    {
        var manifest = """{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"parameters":{"p":{"type":"TYPE","default":VALUE}}}}"""
            .Replace("TYPE", type, StringComparison.Ordinal).Replace("VALUE", value, StringComparison.Ordinal);
        var diagnostic = Assert.Single(Report(Envelope(manifest)).Diagnostics);
        Assert.Equal("manifest.parameter.default-type-mismatch", diagnostic.Code);
        Assert.Equal("/spec/parameters/p/default", diagnostic.Path);
        Assert.Equal(expectedType, diagnostic.Parameters["expectedType"]);
    }

    [Theory]
    [InlineData("string", "[\"secret-value\",\"secret-value\"]")]
    [InlineData("integer", "[1,1.0]")]
    [InlineData("number", "[-0,0]")]
    public void Duplicate_allowed_values_use_parameter_diagnostic(string type, string values)
    {
        var manifest = """{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"parameters":{"p":{"type":"TYPE","allowedValues":VALUES}}}}"""
            .Replace("TYPE", type, StringComparison.Ordinal).Replace("VALUES", values, StringComparison.Ordinal);
        var diagnostic = Assert.Single(Report(Envelope(manifest)).Diagnostics);
        Assert.Equal("manifest.parameter.allowed-value-duplicate", diagnostic.Code);
        Assert.Equal("/spec/parameters/p/allowedValues", diagnostic.Path);
        Assert.Empty(diagnostic.Parameters);
        Assert.DoesNotContain("secret-value", diagnostic.Message + string.Join("", diagnostic.RelatedPaths));
    }

    [Fact]
    public void Rejects_invalid_encoding_and_unicode()
    {
        foreach (var bytes in new[] { new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{' }, new byte[] { (byte)'{', 0xFF, (byte)'}' } })
            Assert.Equal("manifest-import.invalid-encoding", Assert.Single(Assert.IsType<ManifestParseResult.InvalidRequest>(ManifestPayloadParser.Parse(bytes)).Errors).Code);
        Assert.Contains(Report(Envelope("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"\uD800"},"spec":{}}""")).Diagnostics,
            diagnostic => diagnostic.Code == "manifest.syntax.invalid-json" && diagnostic.Path == "/metadata/id");
    }

    [Fact]
    public void Rejects_utf16_payload_as_an_envelope_encoding_error()
    {
        var bytes = Encoding.Unicode.GetBytes(Minimal);
        var error = Assert.Single(Assert.IsType<ManifestParseResult.InvalidRequest>(ManifestPayloadParser.Parse(bytes)).Errors);
        Assert.Equal("manifest-import.invalid-encoding", error.Code);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Non_json_numbers_inside_manifest_are_manifest_syntax_errors(string literal)
    {
        var payload = Minimal.Replace("\"spec\":{}", "\"spec\":{\"extra\":" + literal + "}", StringComparison.Ordinal);
        var diagnostic = Assert.Single(Report(payload).Diagnostics);
        Assert.Equal("manifest.syntax.invalid-json", diagnostic.Code);
        Assert.DoesNotContain("manifest.schema.", diagnostic.Code);
    }

    [Fact]
    public void Bare_application_version_is_an_envelope_json_error()
    {
        var payload = Minimal.Replace("\"applicationVersion\":\"1.0.0+build.17\"", "\"applicationVersion\":1.0.0", StringComparison.Ordinal);
        var error = Assert.Single(Assert.IsType<ManifestParseResult.InvalidRequest>(Parse(payload)).Errors);
        Assert.Equal("manifest-import.invalid-json", error.Code);
    }

    [Theory]
    [InlineData("{\"applicationVersion\":\"1.0\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",")]
    [InlineData("""{"applicationVersion":"1.0","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"bad\q"},"spec":{}}}""")]
    public void Malformed_json_after_manifest_start_stays_in_manifest_syntax(string payload)
    {
        var diagnostic = Assert.Single(Report(payload).Diagnostics);
        Assert.Equal("manifest.syntax.invalid-json", diagnostic.Code);
        Assert.NotNull(diagnostic.Location);
    }

    [Fact]
    public void Envelope_syntax_error_precedes_manifest_diagnostics()
    {
        const string payload = """{"applicationVersion":"1.0","manifest":{"apiVersion":"wrong","metadata":{"id":"example"},"spec":{}},}""";
        var errors = Assert.IsType<ManifestParseResult.InvalidRequest>(Parse(payload)).Errors;
        Assert.Contains(errors, error => error.Code == "manifest-import.invalid-json");
        Assert.DoesNotContain(errors, error => error.Code.StartsWith("manifest.schema.", StringComparison.Ordinal));
    }

    [Fact]
    public void Schema_uri_mismatch_uses_the_selected_version_uri()
    {
        var manifest = """{"$schema":"https://haby.dev/schemas/application-manifest.v2alpha1.json","apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{}}""";
        var diagnostic = Assert.Single(Report(Envelope(manifest)).Diagnostics);
        Assert.Equal("manifest.schema.schema-uri-mismatch", diagnostic.Code);
        Assert.Equal("/$schema", diagnostic.Path);
        Assert.Equal("https://haby.dev/schemas/application-manifest.v1alpha1.json", diagnostic.Parameters["expectedSchema"]);
    }

    [Fact]
    public void Duplicate_manifest_metadata_is_deduplicated_at_manifest_relative_path()
    {
        var payload = Minimal.Replace("\"metadata\":{\"id\":\"example\"}",
            "\"metadata\":{\"id\":\"a\"},\"metadata\":{\"id\":\"b\"},\"metadata\":{\"id\":\"c\"}", StringComparison.Ordinal);
        var diagnostic = Assert.Single(Report(payload).Diagnostics);
        Assert.Equal("manifest.syntax.duplicate-property", diagnostic.Code);
        Assert.Equal("/metadata", diagnostic.Path);
        Assert.Equal("metadata", diagnostic.Parameters["property"]);
    }

    [Fact]
    public void Missing_document_members_are_reported_once_each_in_path_order()
    {
        var manifest = """{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"components":{"api":{"documents":{"app":{}}}}}}""";
        var diagnostics = Report(Envelope(manifest)).Diagnostics;
        Assert.Equal(["fileName", "format", "template"], diagnostics.Select(d => d.Parameters["property"]));
        Assert.Equal(["/spec/components/api/documents/app/fileName", "/spec/components/api/documents/app/format", "/spec/components/api/documents/app/template"],
            diagnostics.Select(d => d.Path));
        Assert.All(diagnostics, d => Assert.Equal("manifest.schema.required", d.Code));
    }

    [Fact]
    public void Accepts_late_union_discriminator_and_empty_presence_label()
    {
        var manifest = """{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example","labels":{"production":""}},"spec":{"components":{"api":{"bindings":{"b":{"target":{"resourceRef":"db","kind":"local-resource"}}}}}}}""";
        Assert.IsType<ManifestParseResult.Valid>(Parse(Envelope(manifest)));
    }

    [Fact]
    public void Rejects_nested_extension_duplicates_before_DTO_deserialization()
    {
        var report = Report(Envelope("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"resources":{"r":{"type":"a/v1","providerRef":"p","properties":{"nested":{"x":1,"x":2}}}}}}"""));
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal("manifest.syntax.duplicate-property", diagnostic.Code);
        Assert.Equal("/spec/resources/r/properties/nested/x", diagnostic.Path);
        Assert.NotNull(diagnostic.Location);
        Assert.Equal(1, diagnostic.Location.Line);
        Assert.True(diagnostic.Location.Column > 1);
    }

    [Fact]
    public void Escapes_pointer_segments_and_redacts_values()
    {
        var report = Report(Envelope("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"resources":{"a/b~c":{"type":"a/v1","providerRef":"p","properties":{"password":"TOP-SECRET","password":"LEAK"}}}}}"""));
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal("/spec/resources/a~1b~0c/properties/password", diagnostic.Path);
        Assert.DoesNotContain("TOP-SECRET", diagnostic.Message + string.Join("", diagnostic.Parameters.Values));
        Assert.DoesNotContain("LEAK", diagnostic.Message + string.Join("", diagnostic.Parameters.Values));
    }

    [Fact]
    public void Escaped_duplicate_member_names_are_equal()
    {
        var report = Report(Envelope("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"resources":{"r":{"type":"a/v1","providerRef":"p","properties":{"x":1,"\u0078":2}}}}}"""));
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "manifest.syntax.duplicate-property" && diagnostic.Path == "/spec/resources/r/properties/x");
    }

    [Fact]
    public void Known_and_unknown_unions_suppress_other_branch_noise()
    {
        var known = Report(Envelope("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"components":{"c":{"bindings":{"b":{"target":{"kind":"local-resource"}}}}}}}"""));
        Assert.Equal("manifest.schema.required", Assert.Single(known.Diagnostics).Code);
        var unknown = Report(Envelope("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"components":{"c":{"bindings":{"b":{"target":{"kind":"unknown"}}}}}}}"""));
        Assert.Equal("manifest.schema.invalid-discriminator", Assert.Single(unknown.Diagnostics).Code);
    }

    [Fact]
    public void Enforces_depth_64_and_65()
    {
        var at64 = "{\"a\":" + new string('[', 62) + "0" + new string(']', 62) + "}";
        var at65 = "{\"a\":" + new string('[', 63) + "0" + new string(']', 63) + "}";
        // The envelope root and manifest root consume two levels.
        Assert.DoesNotContain(Report(Envelope(at64)).Diagnostics, diagnostic => diagnostic.Code == "manifest.syntax.max-depth-exceeded");
        var exceeded = Assert.Single(Report(Envelope(at65)).Diagnostics);
        Assert.Equal("manifest.syntax.max-depth-exceeded", exceeded.Code);
        Assert.NotNull(exceeded.Location);
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public void Bounds_and_orders_independent_diagnostics(int count, bool truncated)
    {
        var components = string.Join(',', Enumerable.Range(0, count).Reverse().Select(i => $"\"c{i:D3}\":{{\"unknown\":true}}"));
        var manifest = """{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"components":{}}}""";
        manifest = manifest.Replace("\"components\":{}", "\"components\":{" + components + "}", StringComparison.Ordinal);
        var report = Report(Envelope(manifest));
        Assert.Equal(Math.Min(count, 100), report.Diagnostics.Count);
        Assert.Equal(truncated, report.Truncated);
        Assert.Equal(report.Diagnostics.OrderBy(d => d.Path, StringComparer.Ordinal).Select(d => d.Path), report.Diagnostics.Select(d => d.Path));
    }

    [Fact]
    public void Negative_wire_shapes_produce_specific_paths_without_adapter_fallbacks()
    {
        var cases = new (string Name, Action<JsonObject> Change)[]
        {
            ("metadata id missing", root => root["metadata"]!.AsObject().Remove("id")),
            ("metadata null", root => root["metadata"] = null),
            ("labels null", root => root["metadata"]!["labels"] = null),
            ("spec unknown", root => root["spec"]!["extra"] = true),
            ("resource type missing", root => root["spec"]!["resources"]!["database"]!.AsObject().Remove("type")),
            ("resource properties scalar", root => root["spec"]!["resources"]!["database"]!["properties"] = 1),
            ("target missing kind", root => root["spec"]!["components"]!["worker"]!["bindings"]!["local"]!["target"]!.AsObject().Remove("kind")),
            ("target unknown kind", root => root["spec"]!["components"]!["worker"]!["bindings"]!["local"]!["target"]!["kind"] = "unknown"),
            ("document missing format", root => root["spec"]!["components"]!["worker"]!["documents"]!["settings"]!.AsObject().Remove("format")),
            ("document unknown format", root => root["spec"]!["components"]!["worker"]!["documents"]!["settings"]!["format"] = "xml"),
            ("parameter type missing", root => root["spec"]!["parameters"]!["prefix"]!.AsObject().Remove("type")),
            ("parameter integer fractional", root => root["spec"]!["parameters"]!["count"]!["default"] = 1.5),
            ("parameter wrong default", root => root["spec"]!["parameters"]!["prefix"]!["default"] = 1),
            ("parameter conflicting defaults", root => root["spec"]!["parameters"]!["prefix"]!["defaultExpression"] = "literal"),
            ("bad projection path", root => root["spec"]!["components"]!["worker"]!["bindings"]!["local"]!["documentProjections"]![0]!["path"] = "/bad~2path")
        };
        var complete = File.ReadAllText(RepositoryPath("tests", "Haby.Manifest.Tests", "Corpus", "complete.json"));
        foreach (var (name, change) in cases)
        {
            var root = JsonNode.Parse(complete)!.AsObject();
            change(root);
            var report = Report(Envelope(root.ToJsonString()));
            Assert.NotEmpty(report.Diagnostics);
            Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Path == "" &&
                diagnostic.Code == "manifest.schema.constraint-violation" &&
                diagnostic.Parameters.TryGetValue("constraint", out var constraint) && constraint is "schema" or "branch" or "dto");
            Assert.All(report.Diagnostics, diagnostic => Assert.StartsWith("/", diagnostic.Path));
        }
    }

    private static string Envelope(string manifest) => "{\"applicationVersion\":\"1.0\",\"manifest\":" + manifest + "}";
    private static ManifestParseResult Parse(string text) => ManifestPayloadParser.Parse(Encoding.UTF8.GetBytes(text));
    private static ManifestValidationReport Report(string text) => Assert.IsType<ManifestParseResult.InvalidManifest>(Parse(text)).Report;

    private static string RepositoryPath(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "HamstixHaby.slnx"))) return Path.Combine([directory.FullName, .. parts]);
        throw new DirectoryNotFoundException();
    }
}
