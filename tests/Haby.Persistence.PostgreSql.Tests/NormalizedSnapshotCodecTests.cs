using System.Text;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class NormalizedSnapshotCodecTests
{
    private const string AnyObject = """{"type":"object"}""";
    private static readonly ManifestContractCatalog Catalog = new(
        [new(ResourceTypeId.Create("postgresql.database/v1alpha1"), AnyObject, AnyObject)],
        [new(WorkloadTypeId.Create("kubernetes.workload/v1alpha1"), AnyObject)],
        [new(OutputContractId.Create("postgresql.connection/v1alpha1"), AnyObject)],
        [new(ConfigurationRendererId.Create("postgresql.connection/v1alpha1"))]);

    [Fact]
    public void Complete_normalized_manifest_round_trips_without_a_database()
    {
        var manifest = CompleteManifest();

        var restored = NormalizedSnapshotCodec.Decode(NormalizedSnapshotCodec.Encode(manifest),
            manifest.InputApiVersion, manifest.NormalizedModelVersion, manifest.Metadata.Id.Value,
            NormalizedSnapshotCodec.FormatVersion);

        Assert.Equal(manifest, restored);
    }

    [Fact]
    public void Corrupt_snapshot_fails_without_a_database()
    {
        var manifest = CompleteManifest();

        Assert.Throws<InvalidDataException>(() => NormalizedSnapshotCodec.Decode("{bad",
            manifest.InputApiVersion, manifest.NormalizedModelVersion, manifest.Metadata.Id.Value,
            NormalizedSnapshotCodec.FormatVersion));
    }

    [Fact]
    public void Deferred_nodes_are_not_silently_accepted_by_the_manifest_snapshot_codec()
    {
        var manifest = CompleteManifest();
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse(NormalizedSnapshotCodec.Encode(manifest))!;
        snapshot["spec"]!["resources"]!["database"]!["properties"] = System.Text.Json.Nodes.JsonNode.Parse(
            """{"kind":"object","members":{"confidential":{"kind":"deferred","formatter":"sample/v1","arguments":{"sensitive":"do not expose"}}}}"""u8);
        var error = Assert.Throws<InvalidDataException>(() => NormalizedSnapshotCodec.Decode(snapshot.ToJsonString(),
            manifest.InputApiVersion, manifest.NormalizedModelVersion, manifest.Metadata.Id.Value,
            NormalizedSnapshotCodec.FormatVersion));
        Assert.DoesNotContain("do not expose", error.Message);
    }

    [Theory]
    [InlineData("omit")]
    [InlineData("omit-unslotted")]
    [InlineData("invalid-id")]
    [InlineData("wrong-type")]
    [InlineData("old-format")]
    [InlineData("old-model")]
    [InlineData("wrong-column-model")]
    [InlineData("old-column-format")]
    public void Current_snapshot_requires_explicit_slot_and_exact_versions(string corruption)
    {
        var manifest = CompleteManifest();
        var root = System.Text.Json.Nodes.JsonNode.Parse(NormalizedSnapshotCodec.Encode(manifest))!;
        var binding = root["spec"]!["components"]!["worker"]!["bindings"]!["local"]!.AsObject();
        var model = manifest.NormalizedModelVersion;
        var format = NormalizedSnapshotCodec.FormatVersion;
        switch (corruption)
        {
            case "omit": binding.Remove("configurationSlot"); break;
            case "omit-unslotted": root["spec"]!["components"]!["worker"]!["bindings"]!["shared"]!.AsObject().Remove("configurationSlot"); break;
            case "invalid-id": binding["configurationSlot"] = "private_Invalid"; break;
            case "wrong-type": binding["configurationSlot"] = 1; break;
            case "old-format": root["format"] = 1; break;
            case "old-model": root["model"] = 1; break;
            case "wrong-column-model": model = 1; break;
            case "old-column-format": format = 1; break;
        }
        var error = Assert.Throws<InvalidDataException>(() => NormalizedSnapshotCodec.Decode(root.ToJsonString(),
            manifest.InputApiVersion, model, manifest.ApplicationId.Value, format));
        Assert.DoesNotContain("private_Invalid", error.Message);
    }

    [Fact]
    public void Snapshot_preserves_explicit_null_and_requested_slot_and_expansion_rechecks_restored_duplicates()
    {
        var manifest = CompleteManifest();
        var root = System.Text.Json.Nodes.JsonNode.Parse(NormalizedSnapshotCodec.Encode(manifest))!;
        var bindings = root["spec"]!["components"]!["worker"]!["bindings"]!;
        Assert.Equal("main-database", bindings["local"]!["configurationSlot"]!.GetValue<string>());
        Assert.True(bindings["shared"]!.AsObject().ContainsKey("configurationSlot"));
        Assert.Null(bindings["shared"]!["configurationSlot"]);
        bindings["shared"]!["configurationSlot"] = "main-database";
        var restored = NormalizedSnapshotCodec.Decode(root.ToJsonString(), manifest.InputApiVersion,
            manifest.NormalizedModelVersion, manifest.ApplicationId.Value, NormalizedSnapshotCodec.FormatVersion);
        var result = new Haby.Application.ConfigurationProfileExpander(new([])).Expand([], [],
            [new(ManifestLocalId.Create("worker"), restored.Spec.Components[ManifestLocalId.Create("worker")], [])]);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Layouts);
        Assert.Contains(result.Diagnostics, x => x.Code == "profile.slot.duplicate");
    }

    private static NormalizedApplicationManifest CompleteManifest()
    {
        using var stream = typeof(NormalizedSnapshotCodecTests).Assembly
            .GetManifestResourceStream("Haby.Persistence.PostgreSql.Tests.complete.json")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var payload = Encoding.UTF8.GetBytes("{\"applicationVersion\":\"v1\",\"manifest\":" + reader.ReadToEnd() + "}");
        return Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(payload, Catalog))
            .Candidate.Declaration;
    }
}
