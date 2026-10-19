using System.Text.Json;
using System.Text.Json.Nodes;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Manifest.Tests;

public sealed class NormalizedApplicationManifestMapperTests
{
    private static readonly ManifestJsonContext Context = ManifestJsonContext.CreateStrict();

    [Fact]
    public void Complete_fixture_covers_every_union_and_preserves_all_declaration_fields()
    {
        var dto = ReadComplete();
        var mapped = NormalizedApplicationManifestMapper.Map(dto);
        var spec = At(mapped.Declaration, "spec");
        var parameters = At(spec, "parameters");
        foreach (var name in new[] { "prefix", "enabled", "count", "ratio" })
            Assert.True(parameters.Members.ContainsKey(name));
        var component = At(At(spec, "components"), "worker");
        Assert.Equal("local-resource", String(At(At(At(component, "bindings"), "local"), "target"), "kind"));
        Assert.Equal("application-export", String(At(At(At(component, "bindings"), "shared"), "target"), "kind"));
        var documents = At(component, "documents");
        Assert.Equal("json", String(At(documents, "settings"), "format"));
        Assert.Equal("yaml", String(At(documents, "yaml-settings"), "format"));
        Assert.Equal("text", String(At(documents, "readme"), "format"));
        Assert.Equal("retain", String(At(At(At(spec, "resources"), "database"), "lifecycle"), "deletionPolicy"));
        Assert.Equal(1, mapped.NormalizedModelVersion);
        Assert.Equal("haby.dev/v1alpha1", mapped.InputApiVersion);
        Assert.False(mapped.Declaration.Members.ContainsKey("$schema"));
    }

    [Fact]
    public void Core_defaults_and_set_order_are_normalized()
    {
        var original = CompleteNode();
        var changed = CompleteNode();
        changed.Remove("$schema");
        var spec = changed["spec"]!.AsObject();
        var resource = spec["resources"]!["database"]!.AsObject();
        resource.Remove("lifecycle");
        var local = spec["components"]!["worker"]!["bindings"]!["local"]!.AsObject();
        local["documentProjections"] = new JsonArray(
            new JsonObject { ["documentRef"] = "settings", ["path"] = "/Other", ["rendererRef"] = "postgresql.connection/v1alpha1" },
            new JsonObject { ["documentRef"] = "settings", ["path"] = "/Database", ["rendererRef"] = "postgresql.connection/v1alpha1" });
        original["spec"]!["components"]!["worker"]!["bindings"]!["local"]!["documentProjections"] = new JsonArray(
            new JsonObject { ["documentRef"] = "settings", ["path"] = "/Database", ["rendererRef"] = "postgresql.connection/v1alpha1" },
            new JsonObject { ["documentRef"] = "settings", ["path"] = "/Other", ["rendererRef"] = "postgresql.connection/v1alpha1" });
        changed["spec"]!["parameters"]!["prefix"]!["allowedValues"] = new JsonArray("b", "a");
        changed["spec"]!["parameters"]!["count"]!["allowedValues"] = new JsonArray(2, 1);
        var first = Map(original);
        var second = Map(changed);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
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
    public void Exact_numbers_object_order_and_string_escaping_compare_equal()
    {
        const string first = """{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample","labels":{"a":"x","b":"é"}},"spec":{"parameters":{"number":{"type":"number","minimum":1e999999999999999999999,"allowedValues":[1,2],"default":1.0}},"resources":{"store":{"type":"example.store/v1","providerRef":"example","properties":{"a":1,"b":-0}}}}}""";
        const string second = """{"spec":{"resources":{"store":{"properties":{"b":0.0,"a":1e0},"providerRef":"example","type":"example.store/v1"}},"parameters":{"number":{"default":1e0,"allowedValues":[2.0,1.00],"minimum":10e999999999999999999998,"type":"number"}}},"metadata":{"labels":{"b":"\u00e9","a":"x"},"id":"sample"},"kind":"Application","apiVersion":"haby.dev/v1alpha1"}""";
        var a = Map(first);
        var b = Map(second);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Arrays_and_raw_text_remain_order_and_whitespace_sensitive()
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

    [Fact]
    public void Mapping_does_not_retain_dto_collections_or_document_storage()
    {
        var dto = ReadComplete();
        var mapped = NormalizedApplicationManifestMapper.Map(dto);
        dto.Metadata.Labels["team"] = "changed";
        dto.Spec.Components["worker"].Bindings["local"].DocumentProjections.Clear();
        Assert.Equal("platform", String(At(mapped.Declaration, "metadata", "labels"), "team"));
        Assert.Single(Assert.IsType<ConfigurationArray>(At(mapped.Declaration, "spec", "components", "worker", "bindings", "local").Members["documentProjections"]).Items);

        NormalizedApplicationManifest detached;
        using (var document = JsonDocument.Parse("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},"spec":{"resources":{"store":{"type":"example.store/v1","providerRef":"example","properties":{"nested":[1]}}}}}"""))
            detached = NormalizedApplicationManifestMapper.Map(JsonSerializer.Deserialize(document.RootElement, Context.ApplicationManifest)!);
        Assert.Equal(new ConfigurationArray([ConfigurationNumber.Parse("1")]),
            At(detached.Declaration, "spec", "resources", "store", "properties").Members["nested"]);
    }

    private static NormalizedApplicationManifest Map(string json) =>
        NormalizedApplicationManifestMapper.Map(JsonSerializer.Deserialize(json, Context.ApplicationManifest)!);
    private static NormalizedApplicationManifest Map(JsonObject json) => Map(json.ToJsonString());
    private static ApplicationManifest ReadComplete() => JsonSerializer.Deserialize(File.ReadAllText(RepositoryPath("tests", "Haby.Manifest.Tests", "Corpus", "complete.json")), Context.ApplicationManifest)!;
    private static JsonObject CompleteNode() => JsonNode.Parse(File.ReadAllText(RepositoryPath("tests", "Haby.Manifest.Tests", "Corpus", "complete.json")))!.AsObject();
    private static string RepositoryPath(params string[] parts) => Path.Combine([FindRoot(), .. parts]);
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HamstixHaby.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    private static ConfigurationObject At(ConfigurationObject root, params string[] path)
    {
        foreach (var part in path) root = Assert.IsType<ConfigurationObject>(root.Members[part]);
        return root;
    }
    private static string String(ConfigurationObject root, string name) => Assert.IsType<ConfigurationString>(root.Members[name]).Value;
}
