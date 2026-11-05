using System.Text;
using System.Text.Json;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Manifest.Tests;

public sealed class BindingConfigurationSlotTests
{
    private static byte[] Payload(string members, string extra = "") => Encoding.UTF8.GetBytes(
        """{"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},"spec":{"resources":{"db":{"type":"a/v1","providerRef":"p"}},"components":{"api":{"bindings":{"consume":{"target":{"kind":"local-resource","resourceRef":"db"}MEMBERS}EXTRA}}}}}}"""
            .Replace("MEMBERS", members, StringComparison.Ordinal).Replace("EXTRA", extra, StringComparison.Ordinal));

    [Theory]
    [InlineData("")]
    [InlineData(",\"configurationSlot\":\"main-database\"")]
    public void Real_parser_maps_exact_typed_slot_or_absence_and_snapshots_caller_data(string members)
    {
        var parsed = Assert.IsType<ManifestParseResult.Valid>(ManifestPayloadParser.Parse(Payload(members), TestManifestCatalog.Instance));
        var mapped = NormalizedApplicationManifestMapper.Map(parsed.Manifest);
        var binding = mapped.Spec.Components[ManifestLocalId.Create("api")].Bindings[ManifestLocalId.Create("consume")];
        Assert.Equal(members.Length == 0 ? null : ManifestLocalId.Create("main-database"), binding.ConfigurationSlot);
        parsed.Manifest.Spec.Components["api"].Bindings.Clear();
        Assert.Single(mapped.Spec.Components[ManifestLocalId.Create("api")].Bindings);
        var equal = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Payload(members), TestManifestCatalog.Instance)).Candidate.Declaration;
        Assert.Equal(mapped, equal);
        Assert.Equal(mapped.GetHashCode(), equal.GetHashCode());
        var other = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Payload(",\"configurationSlot\":\"other\""), TestManifestCatalog.Instance)).Candidate.Declaration;
        Assert.NotEqual(mapped, other);
    }

    [Theory]
    [InlineData("null", "manifest.schema.type-mismatch", null)]
    [InlineData("1", "manifest.schema.type-mismatch", null)]
    [InlineData("true", "manifest.schema.type-mismatch", null)]
    [InlineData("{}", "manifest.schema.type-mismatch", null)]
    [InlineData("[]", "manifest.schema.type-mismatch", null)]
    [InlineData("\"\"", "manifest.id.invalid", "configuration-slot")]
    [InlineData("\"Bad_Value\"", "manifest.id.invalid", "configuration-slot")]
    [InlineData("\"two--parts\"", "manifest.id.invalid", "configuration-slot")]
    public void Invalid_slot_refuses_with_safe_exact_diagnostic(string value, string code, string? idKind)
    {
        var invalid = Assert.IsType<ManifestParseResult.InvalidManifest>(ManifestPayloadParser.Parse(Payload(",\"configurationSlot\":" + value), TestManifestCatalog.Instance));
        var error = Assert.Single(invalid.Report.Diagnostics);
        Assert.Equal(code, error.Code);
        Assert.Equal("/spec/components/api/bindings/consume/configurationSlot", error.Path);
        if (idKind is not null) Assert.Equal(idKind, error.Parameters["idKind"]);
        Assert.DoesNotContain("Bad_Value", JsonSerializer.Serialize(error.Parameters));
    }

    [Theory]
    [InlineData(",\"configurationSlot\":\"main\",\"configurationSlot\":\"other\"", "manifest.syntax.duplicate-property")]
    [InlineData(",\"unknownSlot\":\"main\"", "manifest.schema.unknown-property")]
    public void Duplicate_and_unknown_members_refuse_before_mapping(string members, string code)
    {
        var invalid = Assert.IsType<ManifestParseResult.InvalidManifest>(ManifestPayloadParser.Parse(Payload(members), TestManifestCatalog.Instance));
        Assert.Contains(invalid.Report.Diagnostics, x => x.Code == code);
    }

    [Fact]
    public void Duplicate_slots_are_component_local_ordered_and_value_free()
    {
        const string other = """, "another":{"configurationSlot":"main","target":{"kind":"local-resource","resourceRef":"db"}}""";
        var invalid = Assert.IsType<ManifestParseResult.InvalidManifest>(ManifestPayloadParser.Parse(Payload(",\"configurationSlot\":\"main\"", other), TestManifestCatalog.Instance));
        var error = Assert.Single(invalid.Report.Diagnostics);
        Assert.Equal("manifest.id.duplicate", error.Code);
        Assert.Equal("configuration-slot", error.Parameters["idKind"]);
        Assert.Equal("/spec/components/api/bindings/another/configurationSlot", error.Path);
        Assert.Equal(["/spec/components/api/bindings/consume/configurationSlot"], error.RelatedPaths);
        var wire = Encoding.UTF8.GetString(Payload(",\"configurationSlot\":\"main\""));
        var node = System.Text.Json.Nodes.JsonNode.Parse(wire)!;
        var components = node["manifest"]!["spec"]!["components"]!;
        components["worker"] = components["api"]!.DeepClone();
        Assert.IsType<ManifestParseResult.Valid>(ManifestPayloadParser.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()), TestManifestCatalog.Instance));
    }
}
