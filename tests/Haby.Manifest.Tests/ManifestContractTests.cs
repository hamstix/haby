using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Corvus.Text.Json.Validator;
using Xunit;

namespace Haby.Manifest.Tests;

public sealed class ManifestContractTests
{
    private static readonly ManifestJsonContext Context = ManifestJsonContext.CreateStrict();
    private static readonly JsonSchema Schema = JsonSchema.FromText(
        File.ReadAllText(RepositoryPath("schemas", "application-manifest.v1alpha1.json")),
        "https://haby.dev/schemas/application-manifest.v1alpha1.json",
        new JsonSchema.Options(allowFileSystemAndHttpResolution: false),
        refreshCache: true);

    [Theory]
    [InlineData("minimal.json")]
    [InlineData("complete.json")]
    public void Positive_corpus_agrees_with_schema_and_source_generated_DTO(string fixture)
    {
        var json = File.ReadAllText(RepositoryPath("tests", "Haby.Manifest.Tests", "Corpus", fixture));
        Assert.True(IsSchemaValid(json));
        var manifest = JsonSerializer.Deserialize(json, Context.ApplicationManifest);
        Assert.NotNull(manifest);
        Assert.Equal("haby.dev/v1alpha1", manifest.ApiVersion);
        Assert.Equal("Application", manifest.Kind);
        Assert.NotNull(manifest.Spec);
    }

    [Fact]
    public void Omitted_collections_and_extension_objects_have_empty_core_defaults()
    {
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(RepositoryPath("tests", "Haby.Manifest.Tests", "Corpus", "minimal.json")), Context.ApplicationManifest)!;
        Assert.Empty(manifest.Metadata.Labels);
        Assert.Empty(manifest.Spec.Parameters);
        Assert.Empty(manifest.Spec.Resources);
        Assert.Empty(manifest.Spec.Exports);
        Assert.Empty(manifest.Spec.Components);

        const string withDeclarations = """{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"resources":{"db":{"type":"postgresql.database/v1","providerRef":"postgresql.primary"}},"components":{"worker":{"bindings":{"access":{"target":{"kind":"local-resource","resourceRef":"db"}}},"workloads":{"main":{"type":"kubernetes.workload/v1","providerRef":"kubernetes.primary"}}}}}}""";
        Assert.True(IsSchemaValid(withDeclarations));
        var populated = JsonSerializer.Deserialize(withDeclarations, Context.ApplicationManifest)!;
        Assert.Equal(JsonValueKind.Object, populated.Spec.Resources["db"].Properties.ValueKind);
        Assert.Equal("retain", populated.Spec.Resources["db"].Lifecycle.DeletionPolicy);
        var component = populated.Spec.Components["worker"];
        Assert.Empty(component.Labels);
        Assert.Empty(component.Documents);
        Assert.Equal(JsonValueKind.Object, component.Bindings["access"].Options.ValueKind);
        Assert.Empty(component.Bindings["access"].DocumentProjections);
        Assert.Equal(JsonValueKind.Object, component.Workloads["main"].Properties.ValueKind);
    }

    [Fact]
    public void Unicode_scalar_limits_and_extreme_integer_exponents_preserve_the_wire_contract()
    {
        var root = JsonNode.Parse(File.ReadAllText(RepositoryPath("tests", "Haby.Manifest.Tests", "Corpus", "minimal.json")))!.AsObject();
        root["metadata"]!["displayName"] = string.Concat(Enumerable.Repeat("🚀", 256));
        root["spec"]!["parameters"] = JsonNode.Parse("""{"count":{"type":"integer","minimum":1e999999999,"allowedValues":[1e999999999]}}"""u8);
        var accepted = root.ToJsonString();
        Assert.True(IsSchemaValid(accepted));
        var parameter = Assert.IsType<IntegerParameterDefinition>(
            JsonSerializer.Deserialize(accepted, Context.ApplicationManifest)!.Spec.Parameters["count"]);
        Assert.Equal("1e999999999", parameter.Minimum!.Value.RawText);

        root["metadata"]!["displayName"] = string.Concat(Enumerable.Repeat("🚀", 257));
        Assert.False(IsSchemaValid(root.ToJsonString()));
    }

    [Fact]
    public void Complete_corpus_covers_every_union_and_extension_boundary()
    {
        var manifest = ReadComplete();
        var spec = manifest.Spec;
        Assert.IsType<StringParameterDefinition>(spec.Parameters["prefix"]);
        Assert.IsType<BooleanParameterDefinition>(spec.Parameters["enabled"]);
        Assert.IsType<IntegerParameterDefinition>(spec.Parameters["count"]);
        var number = Assert.IsType<NumberParameterDefinition>(spec.Parameters["ratio"]);
        Assert.Equal("0.123456789012345678901234567890123456789", number.Minimum!.Value.RawText);
        Assert.Equal("1e999", number.Maximum!.Value.RawText);
        var integer = Assert.IsType<IntegerParameterDefinition>(spec.Parameters["count"]);
        Assert.Equal("-999999999999999999999999999999999999999", integer.Minimum!.Value.RawText);
        Assert.IsType<LocalResourceTarget>(spec.Components["worker"].Bindings["local"].Target);
        Assert.IsType<ApplicationExportTarget>(spec.Components["worker"].Bindings["shared"].Target);
        Assert.IsType<JsonConfigurationDocument>(spec.Components["worker"].Documents["settings"]);
        Assert.IsType<YamlConfigurationDocument>(spec.Components["worker"].Documents["yaml-settings"]);
        Assert.IsType<TextConfigurationDocument>(spec.Components["worker"].Documents["readme"]);
        Assert.Equal(JsonValueKind.Object, spec.Resources["database"].Properties.ValueKind);
        Assert.Equal(JsonValueKind.Object, spec.Components["worker"].Bindings["local"].Options.ValueKind);
        Assert.Equal(JsonValueKind.Object, ((JsonConfigurationDocument)spec.Components["worker"].Documents["settings"]).Template.ValueKind);
        Assert.Equal(JsonValueKind.String, spec.Parameters["prefix"].Default.ValueKind);
    }

    [Fact]
    public void Resource_lifecycle_absence_empty_object_and_retain_have_the_same_core_default()
    {
        var values = new[] { (JsonNode?)null, JsonNode.Parse("{}"u8), JsonNode.Parse("""{"deletionPolicy":"retain"}"""u8) };
        foreach (var lifecycle in values)
        {
            var root = CompleteNode();
            var resource = root["spec"]!["resources"]!["database"]!.AsObject();
            if (lifecycle is null) resource.Remove("lifecycle");
            else resource["lifecycle"] = lifecycle;
            var json = root.ToJsonString();
            Assert.True(IsSchemaValid(json));
            Assert.Equal("retain", JsonSerializer.Deserialize(json, Context.ApplicationManifest)!.Spec.Resources["database"].Lifecycle.DeletionPolicy);
        }
    }

    [Fact]
    public void Negative_shape_corpus_is_rejected_by_the_schema()
    {
        var cases = new (string Name, Action<JsonObject> Change)[]
        {
            ("missing apiVersion", root => root.Remove("apiVersion")),
            ("wrong root casing", root => { root["ApiVersion"] = root["apiVersion"]!.DeepClone(); root.Remove("apiVersion"); }),
            ("unknown root", root => root["extra"] = true),
            ("null metadata", root => root["metadata"] = null),
            ("null display name", root => root["metadata"]!["displayName"] = null),
            ("null labels", root => root["metadata"]!["labels"] = null),
            ("missing metadata id", root => root["metadata"]!.AsObject().Remove("id")),
            ("bad application id", root => root["metadata"]!["id"] = "Bad_Id"),
            ("unknown metadata", root => root["metadata"]!["extra"] = true),
            ("bad labels", root => root["metadata"]!["labels"]!["team"] = "Upper"),
            ("unknown spec", root => root["spec"]!["extra"] = true),
            ("bad map key", root => root["spec"]!["resources"]!["Bad_Id"] = root["spec"]!["resources"]!["database"]!.DeepClone()),
            ("missing resource type", root => Resource(root).Remove("type")),
            ("invalid provider alias", root => Resource(root)["providerRef"] = "Upper"),
            ("resource properties scalar", root => Resource(root)["properties"] = 1),
            ("resource properties null", root => Resource(root)["properties"] = null),
            ("null lifecycle", root => Resource(root)["lifecycle"] = null),
            ("null deletion policy", root => Resource(root)["lifecycle"]!["deletionPolicy"] = null),
            ("unknown lifecycle member", root => Resource(root)["lifecycle"]!["extra"] = true),
            ("unknown resource", root => Resource(root)["extra"] = true),
            ("missing export contract", root => Export(root).Remove("contract")),
            ("bad access profile", root => Export(root)["accessProfile"] = "bad.profile"),
            ("unknown component", root => Component(root)["type"] = "worker"),
            ("component documents null", root => Component(root)["documents"] = null),
            ("missing binding target", root => Binding(root).Remove("target")),
            ("unknown binding", root => Binding(root)["extra"] = true),
            ("binding options null", root => Binding(root)["options"] = null),
            ("projection collection null", root => Binding(root)["documentProjections"] = null),
            ("target mixed alternatives", root => Binding(root)["target"]!["exportRef"] = "database"),
            ("target unknown discriminator", root => Binding(root)["target"]!["kind"] = "unknown"),
            ("target missing discriminator", root => Binding(root)["target"]!.AsObject().Remove("kind")),
            ("projection missing path", root => Projection(root).Remove("path")),
            ("projection renderer null", root => Projection(root)["rendererRef"] = null),
            ("projection bad pointer", root => Projection(root)["path"] = "/bad~2path"),
            ("projection duplicate", root => Binding(root)["documentProjections"]!.AsArray().Add(Projection(root).DeepClone())),
            ("document missing format", root => Document(root, "settings").Remove("format")),
            ("document unknown format", root => Document(root, "settings")["format"] = "xml"),
            ("document wrong format casing", root => Document(root, "settings")["format"] = "JSON"),
            ("text template object", root => Document(root, "readme")["template"] = new JsonObject()),
            ("text template null", root => Document(root, "readme")["template"] = null),
            ("document bad filename", root => Document(root, "readme")["fileName"] = "../bad"),
            ("unknown document", root => Document(root, "settings")["extra"] = true),
            ("workload missing provider", root => Workload(root).Remove("providerRef")),
            ("unknown workload", root => Workload(root)["extra"] = true),
            ("workload properties null", root => Workload(root)["properties"] = null),
            ("parameter missing type", root => Parameter(root, "prefix").Remove("type")),
            ("parameter unknown type", root => Parameter(root, "prefix")["type"] = "object"),
            ("string wrong default", root => Parameter(root, "prefix")["default"] = 1),
            ("boolean wrong default", root => Parameter(root, "enabled")["default"] = "true"),
            ("integer wrong allowed value", root => Parameter(root, "count")["allowedValues"] = new JsonArray("1")),
            ("number wrong bound", root => Parameter(root, "ratio")["minimum"] = "0.1"),
            ("number forbidden string constraint", root => Parameter(root, "ratio")["minLength"] = 1),
            ("boolean forbidden numeric constraint", root => Parameter(root, "enabled")["minimum"] = 0),
            ("parameter null default", root => Parameter(root, "prefix")["default"] = null),
            ("parameter null expression", root => Parameter(root, "enabled")["defaultExpression"] = null),
            ("parameter null allowed values", root => Parameter(root, "ratio")["allowedValues"] = null),
            ("parameter conflicting defaults", root => Parameter(root, "prefix")["defaultExpression"] = "literal"),
            ("parameter empty allowed values", root => Parameter(root, "prefix")["allowedValues"] = new JsonArray()),
            ("parameter duplicate allowed values", root => Parameter(root, "prefix")["allowedValues"] = new JsonArray("a", "a")),
            ("integer mathematically duplicate values", root => Parameter(root, "count")["allowedValues"] = JsonNode.Parse("[1,1.0]"u8)),
            ("number mathematically duplicate values", root => Parameter(root, "ratio")["allowedValues"] = JsonNode.Parse("[0.1,1e-1]"u8)),
            ("number negative zero duplicate", root => Parameter(root, "ratio")["allowedValues"] = JsonNode.Parse("[-0,0]"u8)),
            ("unknown parameter member", root => Parameter(root, "prefix")["extra"] = true)
        };

        foreach (var (name, change) in cases)
        {
            var root = CompleteNode();
            change(root);
            Assert.False(IsSchemaValid(root.ToJsonString()), name);
        }
    }

    [Fact]
    public void Required_members_and_unknown_core_members_are_enforced_by_DTO_metadata()
    {
        var cases = new Action<JsonObject>[]
        {
            root => root.Remove("apiVersion"),
            root => root["extra"] = true,
            root => root["metadata"]!.AsObject().Remove("id"),
            root => Resource(root).Remove("type"),
            root => Resource(root)["lifecycle"]!["extra"] = true,
            root => Binding(root)["target"]!["other"] = true,
            root => Document(root, "settings").Remove("template"),
            root => Parameter(root, "prefix")["other"] = true
        };
        foreach (var change in cases)
        {
            var root = CompleteNode();
            change(root);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(root.ToJsonString(), Context.ApplicationManifest));
        }
    }

    [Fact]
    public void Exact_numbers_compare_by_mathematical_value_without_rounding()
    {
        Assert.Equal(ExactJsonNumber.Parse("1"), ExactJsonNumber.Parse("1.00e0"));
        Assert.Equal(ExactJsonNumber.Parse("1e999"), ExactJsonNumber.Parse("10e998"));
        Assert.NotEqual(ExactJsonNumber.Parse("0.123456789012345678901234567890123456789"), ExactJsonNumber.Parse("0.123456789012345678901234567890123456788"));
        Assert.Equal(ExactJsonNumber.Parse("0"), ExactJsonNumber.Parse("-0.000e999"));
        Assert.Equal(ExactJsonInteger.Parse("1e999999999"), ExactJsonInteger.Parse("10e999999998"));
        Assert.Throws<FormatException>(() => ExactJsonInteger.Parse("1.5"));
    }

    [Fact]
    public void Corvus_equality_matrix_is_exact_for_supported_v1alpha1_values()
    {
        var cases = new (string Name, string Schema, string Instance, bool Expected)[]
        {
            ("unique huge", """{"type":"array","uniqueItems":true}""", "[1e999]", true),
            ("unique integer forms", """{"type":"array","uniqueItems":true}""", "[1,1.0]", false),
            ("unique decimal forms", """{"type":"array","uniqueItems":true}""", "[0.1,1e-1]", false),
            ("unique exponent forms", """{"type":"array","uniqueItems":true}""", "[1e999,10e998]", false),
            ("unique adjacent precision", """{"type":"array","uniqueItems":true}""", "[0.123456789012345678901234567890123456789,0.123456789012345678901234567890123456788]", true),
            ("unique nested arrays", """{"type":"array","uniqueItems":true}""", "[[1,1.0],[1.0,1]]", false),
            ("unique reordered objects", """{"type":"array","uniqueItems":true}""", """[{"a":1,"b":2},{"b":2.0,"a":1.0}]""", false),
            ("const huge", """{"const":1e999}""", "1e999", true),
            ("const decimal forms", """{"const":0.1}""", "1e-1", true),
            ("const negative zero", """{"const":-0}""", "0", true),
            ("const reordered object", """{"const":{"a":1,"b":[0.1]}}""", """{"b":[1e-1],"a":1.0}""", true),
            ("enum huge", """{"enum":[1e999]}""", "1e999", true),
            ("enum negative zero", """{"enum":[-0]}""", "0", true),
            ("enum reordered object", """{"enum":[{"a":1,"b":[0.1]}]}""", """{"b":[1e-1],"a":1.0}""", true)
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var test = cases[i];
            var schema = JsonSchema.FromText(
                """{"$schema":"https://json-schema.org/draft/2020-12/schema","allOf":[""" + test.Schema + "]}",
                $"urn:haby:manifest-contract-test:equality-{i}",
                new JsonSchema.Options(allowFileSystemAndHttpResolution: false),
                refreshCache: true);
            Assert.Equal(test.Expected, schema.Validate(Encoding.UTF8.GetBytes(test.Instance)));
        }
    }

    [Fact]
    public void Numeric_allowed_values_reject_negative_zero_duplicates_without_changing_input()
    {
        const string input = """{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"parameters":{"ratio":{"type":"number","allowedValues":[-0,0]},"count":{"type":"integer","allowedValues":[-0,0]}}}}""";
        var bytes = Encoding.UTF8.GetBytes(input);
        Assert.True(Schema.Validate(bytes)); // Corvus 5.7.7 misses this uniqueItems case.
        using var document = JsonDocument.Parse(bytes);
        var paths = V1Alpha1NumericSetGuard.FindDuplicatePaths(document.RootElement);
        Assert.Equal(["/spec/parameters/ratio/allowedValues", "/spec/parameters/count/allowedValues"], paths);
        Assert.Equal("manifest.schema.constraint-violation", V1Alpha1NumericSetGuard.DiagnosticCode);
        Assert.Equal("uniqueItems", V1Alpha1NumericSetGuard.Constraint);
        Assert.Equal(input, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Corvus_diagnostics_supply_paths_without_exposing_values()
    {
        var diagnosticSchema = JsonSchema.FromText(
            """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"values":{"type":"array","uniqueItems":true}}}""",
            "urn:haby:manifest-contract-test:diagnostic-path",
            new JsonSchema.Options(allowFileSystemAndHttpResolution: false),
            refreshCache: true);
        using var collector = Corvus.Text.Json.JsonSchemaResultsCollector.Create(Corvus.Text.Json.JsonSchemaResultsLevel.Detailed, 100);
        Assert.False(diagnosticSchema.Validate("""{"values":[1,1.0]}"""u8.ToArray(), collector));
        Assert.Contains(collector.EnumerateResults(), result =>
            !result.IsMatch &&
            result.GetDocumentEvaluationLocationText() == "/values" &&
            result.GetSchemaEvaluationLocationText().EndsWith("/uniqueItems", StringComparison.Ordinal));
    }

    [Fact]
    public void Envelope_is_typed_and_closed_but_dispatch_is_reserved_for_slice_three()
    {
        const string valid = """{"applicationVersion":"1.0.0+build.17","manifest":{"apiVersion":"haby.dev/v1alpha1"}}""";
        var envelope = JsonSerializer.Deserialize(valid, Context.ImportApplicationEnvelope)!;
        Assert.Equal("1.0.0+build.17", envelope.ApplicationVersion);
        Assert.Equal(JsonValueKind.Object, envelope.Manifest.ValueKind);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"manifest":{}}""", Context.ImportApplicationEnvelope));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"applicationVersion":"1","manifest":{},"other":1}""", Context.ImportApplicationEnvelope));
    }

    private static ApplicationManifest ReadComplete() => JsonSerializer.Deserialize(File.ReadAllText(RepositoryPath("tests", "Haby.Manifest.Tests", "Corpus", "complete.json")), Context.ApplicationManifest)!;
    private static JsonObject CompleteNode() => JsonNode.Parse(File.ReadAllText(RepositoryPath("tests", "Haby.Manifest.Tests", "Corpus", "complete.json")))!.AsObject();
    private static JsonObject Resource(JsonObject root) => root["spec"]!["resources"]!["database"]!.AsObject();
    private static JsonObject Export(JsonObject root) => root["spec"]!["exports"]!["database"]!.AsObject();
    private static JsonObject Component(JsonObject root) => root["spec"]!["components"]!["worker"]!.AsObject();
    private static JsonObject Binding(JsonObject root) => Component(root)["bindings"]!["local"]!.AsObject();
    private static JsonObject Projection(JsonObject root) => Binding(root)["documentProjections"]![0]!.AsObject();
    private static JsonObject Document(JsonObject root, string name) => Component(root)["documents"]![name]!.AsObject();
    private static JsonObject Workload(JsonObject root) => Component(root)["workloads"]!["main"]!.AsObject();
    private static JsonObject Parameter(JsonObject root, string name) => root["spec"]!["parameters"]![name]!.AsObject();

    private static bool IsSchemaValid(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (!Schema.Validate(bytes))
            return false;
        using var document = JsonDocument.Parse(bytes);
        return V1Alpha1NumericSetGuard.FindDuplicatePaths(document.RootElement).Count == 0;
    }

    private static string RepositoryPath(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HamstixHaby.slnx")))
                return Path.Combine([directory.FullName, .. parts]);
        }
        throw new DirectoryNotFoundException("Could not find HamstixHaby.slnx above the test output directory.");
    }
}
