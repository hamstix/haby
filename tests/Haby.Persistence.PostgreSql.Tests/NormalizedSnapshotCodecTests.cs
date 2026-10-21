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
