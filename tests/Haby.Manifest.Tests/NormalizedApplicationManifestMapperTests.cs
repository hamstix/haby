using System.Text.Json;
using System.Text.Json.Nodes;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Manifest.Tests;

public sealed class NormalizedApplicationManifestMapperTests
{
    private static readonly ManifestJsonContext Context = ManifestJsonContext.CreateStrict();
    private static ManifestLocalId Id(string value) => ManifestLocalId.Create(value);

    [Fact]
    public void Complete_fixture_maps_all_closed_union_alternatives_to_typed_declarations()
    {
        var mapped = NormalizedApplicationManifestMapper.Map(ReadComplete());
        Assert.Equal(1, mapped.NormalizedModelVersion);
        Assert.Equal("haby.dev/v1alpha1", mapped.InputApiVersion);
        Assert.Equal(ApplicationManifestKind.Application, mapped.Kind);
        Assert.Equal("example.service", mapped.ApplicationId.Value);
        Assert.Equal("Example", mapped.Metadata.DisplayName);
        Assert.Equal("platform", mapped.Metadata.Labels["team"]);

        Assert.IsType<NormalizedStringParameter>(mapped.Spec.Parameters[Id("prefix")]);
        Assert.IsType<NormalizedBooleanParameter>(mapped.Spec.Parameters[Id("enabled")]);
        Assert.IsType<NormalizedIntegerParameter>(mapped.Spec.Parameters[Id("count")]);
        Assert.IsType<NormalizedNumberParameter>(mapped.Spec.Parameters[Id("ratio")]);
        var prefix = Assert.IsType<NormalizedStringParameter>(mapped.Spec.Parameters[Id("prefix")]);
        Assert.Equal("A literal prefix", prefix.Description);
        Assert.Equal(0, prefix.MinLength);
        Assert.Equal(30, prefix.MaxLength);
        Assert.Equal(["a", "b"], prefix.AllowedValues);
        Assert.Equal("a", Assert.IsType<NormalizedStringScalar>(Assert.IsType<NormalizedLiteralDefault>(prefix.Default).Value).Value);
        Assert.Equal("{{ environment.key }}", Assert.IsType<NormalizedExpressionDefault>(mapped.Spec.Parameters[Id("enabled")].Default).Source);
        var count = Assert.IsType<NormalizedIntegerParameter>(mapped.Spec.Parameters[Id("count")]);
        Assert.Equal(ExactManifestNumber.Parse("-999999999999999999999999999999999999999"), count.Minimum);
        Assert.Equal(ExactManifestNumber.Parse("1"), Assert.IsType<NormalizedIntegerScalar>(Assert.IsType<NormalizedLiteralDefault>(count.Default).Value).Value);
        var ratio = Assert.IsType<NormalizedNumberParameter>(mapped.Spec.Parameters[Id("ratio")]);
        Assert.Equal(ExactManifestNumber.Parse("1e999"), ratio.Maximum);

        var resource = mapped.Spec.Resources[Id("database")];
        Assert.Equal("postgresql.database/v1alpha1", resource.Type.Value);
        Assert.Equal("postgresql.primary", resource.ProviderRef.Value);
        Assert.Equal(ResourceDeletionPolicy.Retain, resource.DeletionPolicy);
        Assert.Equal(new ConfigurationString("small"), resource.Properties.Members["plan"]);
        var export = mapped.Spec.Exports[Id("database")];
        Assert.Equal(Id("database"), export.ResourceRef);
        Assert.Equal("postgresql.connection/v1alpha1", export.Contract.Value);
        Assert.Equal(Id("read-only"), export.AccessProfile);

        var component = mapped.Spec.Components[Id("worker")];
        Assert.Equal("Worker", component.DisplayName);
        Assert.Equal("worker", component.Labels["role"]);
        var local = component.Bindings[Id("local")];
        Assert.Equal(Id("database"), Assert.IsType<NormalizedLocalResourceTarget>(local.Target).ResourceRef);
        Assert.Equal(new ConfigurationBoolean(true), local.Options.Members["ssl"]);
        Assert.Equal("/Database", Assert.Single(local.DocumentProjections).Path.Value);
        Assert.Equal("postgresql.connection/v1alpha1", local.DocumentProjections[0].RendererRef.Value);
        var external = Assert.IsType<NormalizedApplicationExportTarget>(component.Bindings[Id("shared")].Target);
        Assert.Equal("shared.service", external.ApplicationId.Value);
        Assert.Equal(Id("database"), external.ExportRef);
        Assert.Equal("postgresql.connection/v1alpha1", external.Contract.Value);
        Assert.Equal(StructuredDocumentFormat.Json, Assert.IsType<NormalizedStructuredDocument>(component.Documents[Id("settings")]).Format);
        Assert.Equal(StructuredDocumentFormat.Yaml, Assert.IsType<NormalizedStructuredDocument>(component.Documents[Id("yaml-settings")]).Format);
        Assert.Equal("", Assert.IsType<NormalizedTextDocument>(component.Documents[Id("readme")]).Template);
        var workload = component.Workloads[Id("main")];
        Assert.Equal("kubernetes.workload/v1alpha1", workload.Type.Value);
        Assert.Equal("kubernetes.primary", workload.ProviderRef.Value);
        Assert.Equal(Id("worker"), workload.Profile);
        Assert.Equal(ConfigurationNumber.Parse("2"), workload.Properties.Members["replicas"]);
    }

    [Fact]
    public void Core_defaults_and_set_order_are_normalized()
    {
        var original = CompleteNode();
        var changed = CompleteNode();
        foreach (var root in new[] { original, changed })
            root["spec"]!["components"]!["worker"]!["documents"]!["extra-settings"] =
                new JsonObject { ["fileName"] = "extra.json", ["format"] = "json", ["template"] = new JsonObject() };
        changed.Remove("$schema");
        changed["spec"]!["resources"]!["database"]!.AsObject().Remove("lifecycle");
        changed["spec"]!["parameters"]!["prefix"]!["allowedValues"] = new JsonArray("b", "a");
        changed["spec"]!["parameters"]!["count"]!["allowedValues"] = new JsonArray(2, 1);
        original["spec"]!["components"]!["worker"]!["bindings"]!["local"]!["documentProjections"] = Projections(false);
        changed["spec"]!["components"]!["worker"]!["bindings"]!["local"]!["documentProjections"] = Projections(true);
        var first = Map(original);
        var second = Map(changed);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        var projections = first.Spec.Components[Id("worker")].Bindings[Id("local")].DocumentProjections;
        Assert.Equal(["extra-settings", "settings", "settings"], projections.Select(item => item.DocumentRef.Value));
        Assert.Equal(["/Z", "/Database", "/Other"], projections.Select(item => item.Path.Value));
    }

    [Fact]
    public void Omitted_and_explicit_empty_core_collections_compare_equal()
    {
        const string omitted = """{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},"spec":{"resources":{"store":{"type":"example.store/v1","providerRef":"example"}},"components":{"worker":{"bindings":{"store":{"target":{"kind":"local-resource","resourceRef":"store"}}},"workloads":{"main":{"type":"example.workload/v1","providerRef":"example"}}}}}}""";
        const string explicitDefaults = """{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample","labels":{}},"spec":{"parameters":{},"exports":{},"resources":{"store":{"type":"example.store/v1","providerRef":"example","properties":{},"lifecycle":{"deletionPolicy":"retain"}}},"components":{"worker":{"labels":{},"documents":{},"bindings":{"store":{"target":{"kind":"local-resource","resourceRef":"store"},"options":{},"documentProjections":[]}},"workloads":{"main":{"type":"example.workload/v1","providerRef":"example","properties":{}}}}}}}""";
        var first = Map(omitted);
        var second = Map(explicitDefaults);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Object_order_string_escaping_and_number_forms_do_not_affect_equality()
    {
        const string first = """{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample","labels":{"a":"x","b":"é"}},"spec":{"parameters":{"number":{"type":"number","minimum":1e999999999999999999999,"allowedValues":[1,2],"default":1.0}},"resources":{"store":{"type":"example.store/v1","providerRef":"example","properties":{"a":1,"b":-0}}}}}""";
        const string second = """{"spec":{"resources":{"store":{"properties":{"b":0.0,"a":1e0},"providerRef":"example","type":"example.store/v1"}},"parameters":{"number":{"default":1e0,"allowedValues":[2.0,1.00],"minimum":10e999999999999999999998,"type":"number"}}},"metadata":{"labels":{"b":"\u00e9","a":"x"},"id":"sample"},"kind":"Application","apiVersion":"haby.dev/v1alpha1"}""";
        var a = Map(first);
        var b = Map(second);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData("0.123456789012345678901234567890123456789", "0.123456789012345678901234567890123456788")]
    [InlineData("1e999999999999999999999", "2e999999999999999999999")]
    public void Distinct_exact_numbers_remain_distinct(string first, string second) =>
        Assert.NotEqual(MapWithNumberMinimum(first), MapWithNumberMinimum(second));

    [Fact]
    public void Ordered_arrays_and_raw_text_remain_significant()
    {
        var baseline = CompleteNode();
        var reversed = CompleteNode();
        reversed["spec"]!["components"]!["worker"]!["documents"]!["yaml-settings"]!["template"] = new JsonArray(false, 1, null);
        Assert.NotEqual(Map(baseline), Map(reversed));
        var changedText = CompleteNode();
        changedText["spec"]!["components"]!["worker"]!["documents"]!["readme"]!["template"] = " ";
        Assert.NotEqual(Map(baseline), Map(changedText));
    }

    [Fact]
    public void Extension_members_and_display_metadata_are_significant()
    {
        var baseline = CompleteNode();
        var changed = CompleteNode();
        changed["spec"]!["resources"]!["database"]!["properties"]!["extra"] = true;
        Assert.NotEqual(Map(baseline), Map(changed));
        changed = CompleteNode();
        changed["metadata"]!["displayName"] = "Changed";
        Assert.NotEqual(Map(baseline), Map(changed));
    }

    [Theory]
    [InlineData("/metadata/id", "\"another.service\"")]
    [InlineData("/metadata/labels/team", "\"other\"")]
    [InlineData("/spec/parameters/prefix/description", "\"Changed\"")]
    [InlineData("/spec/parameters/prefix/minLength", "1")]
    [InlineData("/spec/parameters/prefix/maxLength", "31")]
    [InlineData("/spec/parameters/prefix/allowedValues", "[\"a\"]")]
    [InlineData("/spec/parameters/prefix/default", "\"b\"")]
    [InlineData("/spec/parameters/count/minimum", "-1")]
    [InlineData("/spec/parameters/count/maximum", "3")]
    [InlineData("/spec/parameters/count/allowedValues", "[1]")]
    [InlineData("/spec/parameters/count/default", "2")]
    [InlineData("/spec/resources/database/type", "\"postgresql.database/v2\"")]
    [InlineData("/spec/resources/database/providerRef", "\"postgresql.secondary\"")]
    [InlineData("/spec/resources/database/properties/plan", "\"medium\"")]
    [InlineData("/spec/resources/database/lifecycle/deletionPolicy", "\"orphan\"")]
    [InlineData("/spec/exports/database/contract", "\"postgresql.connection/v2\"")]
    [InlineData("/spec/exports/database/accessProfile", "\"read-write\"")]
    [InlineData("/spec/components/worker/displayName", "\"Other worker\"")]
    [InlineData("/spec/components/worker/labels/role", "\"other\"")]
    [InlineData("/spec/components/worker/bindings/local/options/ssl", "false")]
    [InlineData("/spec/components/worker/bindings/local/documentProjections/0/path", "\"/Else\"")]
    [InlineData("/spec/components/worker/bindings/local/documentProjections/0/rendererRef", "\"postgresql.connection/v2\"")]
    [InlineData("/spec/components/worker/bindings/shared/target/applicationId", "\"other.service\"")]
    [InlineData("/spec/components/worker/bindings/shared/target/exportRef", "\"other-export\"")]
    [InlineData("/spec/components/worker/bindings/shared/target/contract", "\"postgresql.connection/v2\"")]
    [InlineData("/spec/components/worker/documents/settings/fileName", "\"other.json\"")]
    [InlineData("/spec/components/worker/documents/settings/template/count", "2")]
    [InlineData("/spec/components/worker/workloads/main/type", "\"kubernetes.workload/v2\"")]
    [InlineData("/spec/components/worker/workloads/main/providerRef", "\"kubernetes.secondary\"")]
    [InlineData("/spec/components/worker/workloads/main/profile", "\"other\"")]
    [InlineData("/spec/components/worker/workloads/main/properties/replicas", "3")]
    public void Every_closed_field_participates_in_equality(string path, string replacement)
    {
        var baseline = CompleteNode();
        var changed = CompleteNode();
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        JsonNode current = changed;
        foreach (var part in parts[..^1])
            current = current is JsonArray array ? array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)]! : current[part]!;
        if (current is JsonArray parentArray)
            parentArray[int.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture)] = JsonNode.Parse(replacement);
        else
            current[parts[^1]] = JsonNode.Parse(replacement);
        Assert.NotEqual(Map(baseline), Map(changed));
    }

    [Fact]
    public void Mapping_does_not_retain_dto_collections_or_document_storage()
    {
        var dto = ReadComplete();
        var mapped = NormalizedApplicationManifestMapper.Map(dto);
        dto.Metadata.Labels["team"] = "changed";
        dto.Spec.Components["worker"].Bindings["local"].DocumentProjections.Clear();
        Assert.Equal("platform", mapped.Metadata.Labels["team"]);
        Assert.Single(mapped.Spec.Components[Id("worker")].Bindings[Id("local")].DocumentProjections);

        NormalizedApplicationManifest detached;
        using (var document = JsonDocument.Parse("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},"spec":{"resources":{"store":{"type":"example.store/v1","providerRef":"example","properties":{"nested":[1]}}}}}"""))
            detached = NormalizedApplicationManifestMapper.Map(JsonSerializer.Deserialize(document.RootElement, Context.ApplicationManifest)!);
        Assert.Equal(new ConfigurationArray([ConfigurationNumber.Parse("1")]),
            detached.Spec.Resources[Id("store")].Properties.Members["nested"]);
    }

    private static JsonArray Projections(bool reversed) =>
        reversed
            ? new JsonArray(Projection("settings", "/Other"), Projection("settings", "/Database"), Projection("extra-settings", "/Z"))
            : new JsonArray(Projection("extra-settings", "/Z"), Projection("settings", "/Database"), Projection("settings", "/Other"));
    private static JsonObject Projection(string document, string path) =>
        new() { ["documentRef"] = document, ["path"] = path, ["rendererRef"] = "postgresql.connection/v1alpha1" };
    private static NormalizedApplicationManifest Map(string json) =>
        NormalizedApplicationManifestMapper.Map(JsonSerializer.Deserialize(json, Context.ApplicationManifest)!);
    private static NormalizedApplicationManifest Map(JsonObject json) => Map(json.ToJsonString());
    private static NormalizedApplicationManifest MapWithNumberMinimum(string rawNumber) =>
        Map("{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"sample\"},\"spec\":{\"parameters\":{\"ratio\":{\"type\":\"number\",\"minimum\":" + rawNumber + "}}}}");
    private static ApplicationManifest ReadComplete() => JsonSerializer.Deserialize(File.ReadAllText(RepositoryPath("tests", "Haby.Manifest.Tests", "Corpus", "complete.json")), Context.ApplicationManifest)!;
    private static JsonObject CompleteNode() => JsonNode.Parse(File.ReadAllText(RepositoryPath("tests", "Haby.Manifest.Tests", "Corpus", "complete.json")))!.AsObject();
    private static string RepositoryPath(params string[] parts) => Path.Combine([FindRoot(), .. parts]);
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HamstixHaby.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
