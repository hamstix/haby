using System.Text;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ResourceDeltaTests
{
    [Fact]
    public void Stable_ids_classify_unchanged_added_changed_removed_and_rename_without_position_matching()
    {
        var before = Spec("""
            {"same":{"type":"sample.resource/v1","providerRef":"first","properties":{"name":"kept"}},
             "changed":{"type":"sample.resource/v1","providerRef":"first","properties":{}},
             "renamed":{"type":"sample.resource/v1","providerRef":"first","properties":{}}}
            """);
        var after = Spec("""
            {"new-name":{"type":"sample.resource/v1","providerRef":"first","properties":{}},
             "changed":{"type":"sample.resource/v1","providerRef":"other","properties":{}},
             "same":{"type":"sample.resource/v1","providerRef":"first","properties":{"name":"kept"}}}
            """);
        Assert.Equal(new[] { ("changed", ResourceDeltaKind.Changed), ("new-name", ResourceDeltaKind.Added),
            ("renamed", ResourceDeltaKind.Removed), ("same", ResourceDeltaKind.Unchanged) },
            new ResourceDelta(before, after).Entries.Select(x => (x.Resource.Value, x.Kind)));
    }

    [Theory]
    [InlineData("type", "sample.other/v1")]
    [InlineData("providerRef", "other")]
    [InlineData("deletionPolicy", "orphan")]
    [InlineData("properties", "changed")]
    public void Every_resource_identity_or_effect_property_change_is_classified(string member, string value)
    {
        var original = "{\"item\":{\"type\":\"sample.resource/v1\",\"providerRef\":\"first\",\"properties\":{},\"lifecycle\":{\"deletionPolicy\":\"retain\"}}}";
        var updated = member == "properties" ? original.Replace("\"properties\":{}", "\"properties\":{\"value\":\"changed\"}", StringComparison.Ordinal)
            : original.Replace(member == "type" ? "sample.resource/v1" : member == "providerRef" ? "first" : "retain", value, StringComparison.Ordinal);
        Assert.Equal(ResourceDeltaKind.Changed, Assert.Single(new ResourceDelta(Spec(original), Spec(updated)).Entries).Kind);
    }

    private static NormalizedManifestSpec Spec(string resources)
    {
        var result = ManifestCandidatePipeline.Prepare(Encoding.UTF8.GetBytes(
            "{\"applicationVersion\":\"1\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"transition\"},\"spec\":{\"resources\":" + resources + "}}}"),
            new([new(ResourceTypeId.Create("sample.resource/v1"), "{}"), new(ResourceTypeId.Create("sample.other/v1"), "{}")], [], [], []));
        return Assert.IsType<ManifestCandidateResult.Valid>(result).Candidate.Declaration.Spec;
    }
}
