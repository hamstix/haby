using System.Text.Json.Nodes;
using Haby.Domain;
using Haby.Manifest;
using Xunit;
using static Haby.Tests.ProfilePersistenceSamples;
using static Haby.Tests.ConfigurationRenderingSamples;

namespace Haby.Application.Tests;

public sealed class ConfigurationRenderingCatalogTests
{
    private static ConfigurationPlanningSnapshot Snapshot(ConfigurationRenderingCatalog catalog)
    {
        var node = JsonNode.Parse("""
            {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},"spec":{
            "resources":{"resource":{"type":"sample.resource/v1","providerRef":"pg","properties":{}}},
            "components":{"api":{"bindings":{"use":{"target":{"kind":"local-resource","resourceRef":"resource"},"configurationSlot":"main"}}}}}}}
            """u8)!;
        var manifest = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Bytes(node),
            new([new(ResourceTypeId.Create("sample.resource/v1"), "{}", "{}")], [], [], []))).Candidate.Declaration;
        var profileNode = Node(false); profileNode["spec"]!["bindingRules"]!["connection"]!["documentProjections"]!.AsArray().RemoveAt(1);
        var profile = Parse(profileNode);
        var scopes = ConfigurationPlanningSnapshot.Targets(manifest).Select((t, i) => new ConfigurationPlanningScope(t, i == 0 ? AssignmentToken.New() : null,
            new(t, i == 0 ? [new ProfileDecision.Use(profile.Reference)] : [])));
        return ConfigurationPlanningSnapshot.Prepare(ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)),
            manifest, scopes, [profile], catalog).Snapshot!;
    }
    [Fact]
    public void Second_typed_family_traverses_actual_dispatch_and_frozen_defaults_without_current_recomputation()
    {
        var defaults = new ConfigurationObject(new Dictionary<string, ConfigurationValue> { ["mode"] = new ConfigurationString("original") });
        var catalog = Catalog(defaults: defaults);
        var snapshot = Snapshot(catalog); var pin = Assert.Single(snapshot.RenderPins);
        Assert.Equal(ProfileOptionScope.RendererDefault, pin.OptionOrigins["mode"]);
        var saved = ConfigurationPlanningSnapshotCodec.Encode(snapshot);
        var changed = Catalog(defaults: new(new Dictionary<string, ConfigurationValue> { ["mode"] = new ConfigurationString("changed") }));
        var decoded = ConfigurationPlanningSnapshotCodec.Decode(saved, snapshot.Revision);
        Assert.Equal(snapshot, decoded); Assert.True(changed.Supports(pin));
        Assert.Equal(defaults, pin.Defaults);
        Assert.Null(catalog.Render(pin, new("sample.output/v1", "value", "{}")));
        var fragment = catalog.Render(pin, new("sample.output/v1", "value", "{\"Destination\":\"events\"}"))!;
        var deferred = Assert.IsType<ConfigurationDeferredValue>(fragment.Value);
        var result = changed.Format(pin, deferred, "supplied");
        Assert.True(result?.Reveal() == "supplied");
        Assert.Null(catalog.Render(pin, new("wrong/v1", "value", "{}")));
        Assert.Null(ConfigurationRenderingCatalog.Empty.Render(pin, new("sample.output/v1", "value", "{\"Destination\":\"events\"}")));
        Assert.False(ConfigurationRenderingCatalog.Empty.Supports(pin));
        foreach (var field in new[] { "renderer", "rendererImplementation", "formatter", "formatterImplementation", "outputContract", "outputKey", "producer" })
        {
            var json = JsonNode.Parse(saved)!; json["renderPins"]![0]![field] = field is "rendererImplementation" or "formatterImplementation" or "producer" ? "changed" : "changed/v1";
            if (field == "renderer")
            { Assert.Throws<InvalidDataException>(() => ConfigurationPlanningSnapshotCodec.Decode(json.ToJsonString(), snapshot.Revision)); continue; }
            var altered = ConfigurationPlanningSnapshotCodec.Decode(json.ToJsonString(), snapshot.Revision);
            Assert.NotEqual(snapshot, altered);
            Assert.NotEqual(ConfigurationPlanningSnapshotTests.Fingerprint(snapshot), ConfigurationPlanningSnapshotTests.Fingerprint(altered));
            Assert.False(catalog.Supports(altered.RenderPins.Single()));
        }
    }
    [Fact]
    public void Frozen_pin_defaults_effective_values_origins_and_closed_shape_are_checked_and_fingerprinted()
    {
        var snapshot = Snapshot(Catalog(defaults: new(new Dictionary<string, ConfigurationValue> { ["mode"] = new ConfigurationString("original") })));
        var text = ConfigurationPlanningSnapshotCodec.Encode(snapshot);
        foreach (var path in new[] { "defaults", "options", "origins" })
        {
            var corrupt = JsonNode.Parse(text)!; corrupt["renderPins"]![0]![path]!["mode"] = "changed";
            Assert.Throws<InvalidDataException>(() => ConfigurationPlanningSnapshotCodec.Decode(corrupt.ToJsonString(), snapshot.Revision));
        }
        var changed = JsonNode.Parse(text)!;
        changed["renderPins"]![0]!["defaults"]!["mode"] = "changed";
        changed["renderPins"]![0]!["options"]!["mode"] = "changed";
        var changedDefaults = ConfigurationPlanningSnapshotCodec.Decode(changed.ToJsonString(), snapshot.Revision);
        Assert.NotEqual(ConfigurationPlanningSnapshotTests.Fingerprint(snapshot), ConfigurationPlanningSnapshotTests.Fingerprint(changedDefaults));
        var action = JsonNode.Parse(text)!; action["renderPins"]![0]!["producerAction"]!["action"] = "changed/v1";
        var changedAction = ConfigurationPlanningSnapshotCodec.Decode(action.ToJsonString(), snapshot.Revision);
        Assert.False(Catalog().Supports(changedAction.RenderPins.Single()));
        Assert.NotEqual(ConfigurationPlanningSnapshotTests.Fingerprint(snapshot), ConfigurationPlanningSnapshotTests.Fingerprint(changedAction));
        var unknown = JsonNode.Parse(text)!; unknown["renderPins"]![0]!["unknown"] = true;
        Assert.Throws<InvalidDataException>(() => ConfigurationPlanningSnapshotCodec.Decode(unknown.ToJsonString(), snapshot.Revision));
    }

    [Fact]
    public void Explicit_registration_rejects_ambiguity_conflict_and_missing_support_without_fallback()
    {
        var definition = new Definition(ResourceTypeId.Create("sample.resource/v1"), new([]));
        var metadata = new ConfigurationFormatterCatalog([new FormatterDefinition()], [new(Implementation, FormatterId)]);
        Assert.Throws<ArgumentException>(() => new ConfigurationRenderingCatalog([definition], [new Renderer(), new Renderer()], metadata, [new Formatter()]));
        Assert.Throws<ArgumentException>(() => new ConfigurationRenderingCatalog([definition], [new Renderer()], metadata, [new Formatter(), new Formatter()]));
        Assert.Throws<ArgumentException>(() => new ConfigurationRenderingCatalog([definition, definition with { ResourceType = ResourceTypeId.Create("different/v1") }], [], metadata, []));
        Assert.Throws<ArgumentException>(() => new ConfigurationFormatterCatalog([new FormatterDefinition()], [new(Implementation, FormatterId), new(Implementation, FormatterId)]));
        Assert.Null(Snapshot(ConfigurationRenderingCatalog.Empty));
        Assert.Null(Snapshot(new([definition], [], metadata, [new Formatter()])));
        Assert.Null(Snapshot(new([definition], [new Renderer()], metadata, [])));
        Assert.Null(Snapshot(Catalog("incompatible/v1")));
    }
}
