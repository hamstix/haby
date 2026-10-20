using Haby.Domain;

namespace Haby.Manifest.Tests;

internal static class TestManifestCatalog
{
    private const string AnyObject = """{"type":"object"}""";

    internal static readonly ManifestContractCatalog Instance = new(
        [
            new(ResourceTypeId.Create("postgresql.database/v1alpha1"), AnyObject, AnyObject),
            new(ResourceTypeId.Create("a/v1"), AnyObject, AnyObject),
        ],
        [
            new(WorkloadTypeId.Create("kubernetes.workload/v1alpha1"), AnyObject),
            new(WorkloadTypeId.Create("a/v1"), AnyObject),
        ],
        [
            new(OutputContractId.Create("postgresql.connection/v1alpha1"), AnyObject),
            new(OutputContractId.Create("a/v1"), AnyObject),
        ],
        [
            new(ConfigurationRendererId.Create("postgresql.connection/v1alpha1")),
            new(ConfigurationRendererId.Create("a/v1")),
        ]);
}
