using System.Text;
using System.Text.Json.Nodes;
using Haby.Domain;
using Xunit;

namespace Haby.Manifest.Tests;

public sealed class ManifestExtensionValidationTests
{
    private const string AnyObject = """{"type":"object"}""";
    private const string RequiredName = """{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}""";

    [Fact]
    public void Exact_registrations_cover_every_extension_lookup()
    {
        var manifest = Base();
        manifest["spec"]!["resources"] = JsonNode.Parse("""{"db":{"type":"example.store/v1","providerRef":"provider"}}"""u8);
        manifest["spec"]!["exports"] = JsonNode.Parse("""{"db":{"resourceRef":"db","contract":"example.output/v1"}}"""u8);
        manifest["spec"]!["components"] = JsonNode.Parse("""{"worker":{"bindings":{"local":{"target":{"kind":"local-resource","resourceRef":"db"},"options":{"secret":"secret-value"},"documentProjections":[{"documentRef":"settings","path":"/database","rendererRef":"example.renderer/v1"}]},"remote":{"target":{"kind":"application-export","applicationId":"other","exportRef":"db","contract":"example.output/v1"},"options":{"enabled":true}}},"documents":{"settings":{"format":"json","fileName":"settings.json","template":{}}},"workloads":{"main":{"type":"example.workload/v1","providerRef":"provider"}}}}"""u8);

        var diagnostics = Invalid(manifest, Empty()).Diagnostics;
        Assert.Equal(5, diagnostics.Count);
        Assert.Contains(diagnostics, d => d.Path == "/spec/resources/db/type" && d.Parameters["contractKind"] == "resource");
        Assert.Contains(diagnostics, d => d.Path == "/spec/exports/db/contract" && d.Parameters["contractKind"] == "output");
        Assert.Contains(diagnostics, d => d.Path == "/spec/components/worker/workloads/main/type" && d.Parameters["contractKind"] == "workload");
        Assert.Contains(diagnostics, d => d.Path == "/spec/components/worker/bindings/remote/target/contract" && d.Parameters["contractKind"] == "output");
        Assert.Contains(diagnostics, d => d.Path == "/spec/components/worker/bindings/local/documentProjections/0/rendererRef" && d.Parameters["contractKind"] == "renderer");
        Assert.DoesNotContain(diagnostics, d => d.Path.EndsWith("/options", StringComparison.Ordinal));
    }

    [Fact]
    public void Properties_and_options_use_exact_owner_schema_and_effective_empty_objects()
    {
        var catalog = Catalog(RequiredName, RequiredName, RequiredName, RequiredName);
        var manifest = Base();
        manifest["spec"]!["resources"] = JsonNode.Parse("""{"db":{"type":"example.store/v1","providerRef":"provider"}}"""u8);
        manifest["spec"]!["components"] = JsonNode.Parse("""{"worker":{"bindings":{"local":{"target":{"kind":"local-resource","resourceRef":"db"}},"remote":{"target":{"kind":"application-export","applicationId":"other","exportRef":"db","contract":"example.output/v1"}}},"workloads":{"main":{"type":"example.workload/v1","providerRef":"provider"}}}}"""u8);

        var diagnostics = Invalid(manifest, catalog).Diagnostics;
        Assert.Equal(4, diagnostics.Count);
        Assert.All(diagnostics, d => Assert.Equal("manifest.properties.schema-violation", d.Code));
        Assert.Contains(diagnostics, d => d.Path == "/spec/resources/db/properties/name");
        Assert.Contains(diagnostics, d => d.Path == "/spec/components/worker/workloads/main/properties/name");
        Assert.Contains(diagnostics, d => d.Path == "/spec/components/worker/bindings/local/options/name");
        Assert.Contains(diagnostics, d => d.Path == "/spec/components/worker/bindings/remote/options/name");
    }

    [Fact]
    public void Schemas_report_precise_paths_without_values_or_library_messages()
    {
        var manifest = Base();
        manifest["spec"]!["resources"] = JsonNode.Parse("""{"db":{"type":"example.store/v1","providerRef":"provider","properties":{"name":42,"password":"secret-value"}}}"""u8);
        var diagnostic = Assert.Single(Invalid(manifest, Catalog(RequiredName)).Diagnostics,
            d => d.Severity == ManifestDiagnosticSeverity.Error);
        Assert.Equal("/spec/resources/db/properties/name", diagnostic.Path);
        Assert.Equal("type", diagnostic.Parameters["constraint"]);
        Assert.DoesNotContain("secret-value", diagnostic.Message + string.Join("", diagnostic.Parameters.Values));
        Assert.DoesNotContain("42", diagnostic.Message + string.Join("", diagnostic.Parameters.Values));
    }

    [Fact]
    public void Options_without_schema_accept_empty_and_reject_non_empty()
    {
        var catalog = Catalog(AnyObject);
        var manifest = Base();
        manifest["spec"]!["resources"] = JsonNode.Parse("""{"db":{"type":"example.store/v1","providerRef":"provider"}}"""u8);
        manifest["spec"]!["components"] = JsonNode.Parse("""{"worker":{"bindings":{"local":{"target":{"kind":"local-resource","resourceRef":"db"}},"remote":{"target":{"kind":"application-export","applicationId":"other","exportRef":"db","contract":"example.output/v1"},"options":{}}}}}"""u8);
        Assert.IsType<ManifestParseResult.Valid>(Parse(manifest, catalog));

        manifest["spec"]!["components"]!["worker"]!["bindings"]!["local"]!["options"] = JsonNode.Parse("""{"secret":"secret-value"}"""u8);
        manifest["spec"]!["components"]!["worker"]!["bindings"]!["remote"]!["options"] = JsonNode.Parse("""{"enabled":true}"""u8);
        var diagnostics = Invalid(manifest, catalog).Diagnostics;
        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, d => Assert.Equal("manifest.type.unknown", d.Code));
        Assert.All(diagnostics, d => Assert.Equal("binding-options", d.Parameters["contractKind"]));
        Assert.DoesNotContain("secret-value", string.Join("", diagnostics.Select(d => d.Message + string.Join("", d.Parameters.Values))));
    }

    [Fact]
    public void Later_extension_error_survives_more_than_one_hundred_warnings()
    {
        var manifest = Base();
        var resources = new JsonObject();
        for (var index = 0; index < 101; index++)
            resources[$"r{index:000}"] = JsonNode.Parse("""{"type":"example.store/v1","providerRef":"provider","properties":{"name":"ok"}}"""u8);
        resources["zzzz"] = JsonNode.Parse("""{"type":"example.store/v1","providerRef":"provider","properties":{"name":42}}"""u8);
        manifest["spec"]!["resources"] = resources;
        var report = Invalid(manifest, Catalog(RequiredName));
        Assert.True(report.Truncated);
        Assert.Equal(100, report.Diagnostics.Count);
        Assert.Contains(report.Diagnostics, d => d.Code == "manifest.properties.schema-violation" && d.Path == "/spec/resources/zzzz/properties/name");
        Assert.False(report.IsValid);

        resources["zzzz"]!["properties"]!["name"] = "ok";
        var valid = Assert.IsType<ManifestParseResult.Valid>(Parse(manifest, Catalog(RequiredName)));
        Assert.True(valid.Report.IsValid);
        Assert.True(valid.Report.Truncated);
        Assert.Equal(100, valid.Report.Diagnostics.Count);
    }

    [Fact]
    public void Equivalent_definitions_are_idempotent_and_conflicts_fail_at_registration()
    {
        var id = ResourceTypeId.Create("example.store/v1");
        _ = new ManifestContractCatalog(
            [new(id, """{"type":"object","required":["name"]}"""), new(id, """{"required":["name"],"type":"object"}""")],
            [], [], []);
        Assert.Throws<InvalidOperationException>(() => new ManifestContractCatalog(
            [new(id, AnyObject), new(id, RequiredName)], [], [], []));
        Assert.Throws<ArgumentException>(() => new ManifestContractCatalog([new(id, "")], [], [], []));
    }

    private static JsonObject Base() => JsonNode.Parse("""{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{}}"""u8)!.AsObject();

    private static ManifestContractCatalog Empty() => new([], [], [], []);

    private static ManifestContractCatalog Catalog(string properties, string? resourceOptions = null,
        string? outputOptions = null, string? workloadProperties = null) => new(
        [new(ResourceTypeId.Create("example.store/v1"), properties, resourceOptions)],
        [new(WorkloadTypeId.Create("example.workload/v1"), workloadProperties ?? AnyObject)],
        [new(OutputContractId.Create("example.output/v1"), outputOptions)],
        [new(ConfigurationRendererId.Create("example.renderer/v1"))]);

    private static ManifestParseResult Parse(JsonObject manifest, ManifestContractCatalog catalog)
    {
        var envelope = new JsonObject { ["applicationVersion"] = "1", ["manifest"] = manifest.DeepClone() };
        return ManifestPayloadParser.Parse(Encoding.UTF8.GetBytes(envelope.ToJsonString()), catalog);
    }

    private static ManifestValidationReport Invalid(JsonObject manifest, ManifestContractCatalog catalog) =>
        Assert.IsType<ManifestParseResult.InvalidManifest>(Parse(manifest, catalog)).Report;
}
