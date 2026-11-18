using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class ConfiguredDeploymentUpdateTargetTests
{
    [Fact]
    public async Task Actual_qualification_target_envelope_imports_with_registered_workload_contract()
    {
        const string baseline = "ghcr.io/hamstix/haby-runtime-consumer@sha256:192203c5c93a3d21989b0b7b54a1e687fa03c35ea75b14fb11018aef99fd378a";
        var envelope = JsonNode.Parse("""
            {"applicationVersion":"1.0.0","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"orders"},
            "spec":{"components":{"api":{"documents":{"settings":{"fileName":"settings.json","format":"json","template":{}}},
            "workloads":{"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary",
            "properties":{"image":{"reference":"registry.example/app:v1"},"configuration":{"documents":["settings"]}}}}}}}}}
            """u8)!;
        var target = ConfiguredDeploymentUpdateTarget.PrepareEnvelope(envelope, baseline);
        var store = new InMemoryManifestImportStore();
        var importer = new ImportApplicationManifest(store,
            new ManifestContractCatalog([], [KubernetesContract.Definition], [], []), TimeProvider.System);

        var result = await importer.ExecuteAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), "qualification");

        Assert.IsType<ImportApplicationManifestResult.Succeeded>(result);
        Assert.Single(store.Revisions);
        Assert.Equal("configured-update-v2", envelope["applicationVersion"]!.GetValue<string>());
        Assert.Equal("ghcr.io:443/hamstix/haby-runtime-consumer@sha256:192203c5c93a3d21989b0b7b54a1e687fa03c35ea75b14fb11018aef99fd378a", target);
    }
}
