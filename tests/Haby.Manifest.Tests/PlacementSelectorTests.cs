using System.Text;
using Haby.Manifest;
using Xunit;

namespace Haby.Manifest.Tests;

public sealed class PlacementSelectorTests
{
    [Theory]
    [InlineData("placementRef", true)]
    [InlineData("profile", false)]
    public void Current_alpha_accepts_only_placement_selector(string member, bool valid)
    {
        var json = """{"applicationVersion":"1.4.2","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"orders"},"spec":{"components":{"api":{"workloads":{"main":{"type":"a/v1","providerRef":"p","MEMBER":"standard"}}}}}}}""".Replace("MEMBER", member, StringComparison.Ordinal);
        var catalog = new ManifestContractCatalog([], [new(Haby.Domain.WorkloadTypeId.Create("a/v1"), """{"type":"object"}""")], [], []);
        var result = ManifestPayloadParser.Parse(Encoding.UTF8.GetBytes(json), catalog);
        Assert.Equal(valid, result is ManifestParseResult.Valid);
    }
}
