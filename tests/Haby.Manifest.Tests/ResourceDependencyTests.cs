using System.Text;
using Haby.Domain;
using Xunit;

namespace Haby.Manifest.Tests;

public sealed class ResourceDependencyTests
{
    private const string Schema = """{"type":"object","required":["parent"],"properties":{"parent":{"type":"string","pattern":"^[a-z]+$"}},"additionalProperties":false}""";

    [Theory]
    [InlineData("db", true, false)]
    [InlineData("missing", true, false)]
    [InlineData("invalid/value", true, false)]
    [InlineData("db", false, false)]
    [InlineData("db", true, true)]
    public void Exact_synthetic_contract_dependency_is_staged_and_opaque_data_is_not_scanned(string reference, bool registeredDependency, bool wrongType)
    {
        var targetType = ResourceTypeId.Create("example.store/v1");
        var catalog = new ManifestContractCatalog([
            new(targetType, "{\"type\":\"object\"}"),
            new(ResourceTypeId.Create("example.client/v1"), Schema, Dependency: registeredDependency ? new("parent", targetType) : null),
            new(ResourceTypeId.Create("example.opaque/v1"), "{\"type\":\"object\"}")], [], [], []);
        var payload = """
            {"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"deps"},"spec":{
            "resources":{"db":{"type":"TARGET","providerRef":"p"},"orphan":{"type":"example.store/v1","providerRef":"p"},
            "client":{"type":"example.client/v1","providerRef":"p","properties":{"parent":"REF"}},
            "opaque":{"type":"example.opaque/v1","providerRef":"p","properties":{"databaseRef":"orphan","nested":{"parent":"orphan"}}}},
            "components":{"api":{"bindings":{"a":{"target":{"kind":"local-resource","resourceRef":"client"}},"b":{"target":{"kind":"local-resource","resourceRef":"opaque"}}}}}}}}
            """.Replace("TARGET", wrongType ? "example.opaque/v1" : targetType.Value, StringComparison.Ordinal).Replace("REF", reference, StringComparison.Ordinal);
        var result = ManifestPayloadParser.Parse(Encoding.UTF8.GetBytes(payload), catalog);
        var report = result switch { ManifestParseResult.Valid valid => valid.Report, ManifestParseResult.InvalidManifest invalid => invalid.Report, _ => throw new InvalidOperationException() };
        Assert.Contains(report.Diagnostics, d => d.Code == "manifest.resource.unused" && d.Path == "/spec/resources/orphan");
        if (reference == "db" && registeredDependency && !wrongType)
        {
            Assert.True(report.IsValid);
            Assert.DoesNotContain(report.Diagnostics, d => d.Path == "/spec/resources/db");
        }
        else Assert.Contains(report.Diagnostics, d => d.Code == "manifest.resource.unused" && d.Path == "/spec/resources/db");
        if (reference == "missing") Assert.Contains(report.Diagnostics, d => d.Code == "manifest.reference.not-found" && d.Path == "/spec/resources/client/properties/parent");
        if (reference.Contains('/', StringComparison.Ordinal))
        {
            Assert.False(report.IsValid);
            Assert.DoesNotContain(report.Diagnostics, d => d.Code == "manifest.reference.not-found");
        }
        if (wrongType) Assert.Contains(report.Diagnostics, d => d.Code == "manifest.reference.type-mismatch");
        Assert.Equal(report.Diagnostics.OrderBy(d => d.Path, StringComparer.Ordinal).Select(d => d.Path), report.Diagnostics.Select(d => d.Path));
    }

    [Fact]
    public void Conflicting_dependency_metadata_refuses_catalog_registration()
    {
        var definition = new ResourceTypeDefinition(ResourceTypeId.Create("example.client/v1"), Schema);
        Assert.Throws<InvalidOperationException>(() => new ManifestContractCatalog([definition,
            definition with { Dependency = new("parent", ResourceTypeId.Create("example.store/v1")) }], [], [], []));
    }
}
