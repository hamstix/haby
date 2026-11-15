using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Manifest;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class WorkloadPlanDescriptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_keyed_descriptions_are_safe_and_missing_or_ambiguous_support_preserves_inputs(bool configured)
    {
        var contexts = InternalDeploymentPlacementTests.Contexts(true, configured: configured);
        var inputs = contexts[0].Inputs;
        var manifest = inputs.Providers.Declaration;
        var revision = new ManifestRevision(ManifestRevisionId.New(), inputs.Revision, manifest.ApplicationId,
            ApplicationVersion.Create("v1"), manifest.InputApiVersion, 3, manifest, new([], false), new("operator", DateTimeOffset.UtcNow));
        var query = new Query(revision);
        var describer = new KubernetesWorkloadPlanDescriber();
        var result = await new DescribeWorkloadPlan(query, [describer]).ExecuteAsync(inputs, default);
        Assert.Equal(WorkloadPlanDescriptionStatus.Available, result.Status);
        Assert.Equal(new[] { "api", "worker" }, result.Items.Select(x => x.Component));
        Assert.Equal("orders-api-main", result.Items[0].TargetName);
        Assert.Equal("registry.example/worker:v1", result.Items[1].ImageReference);
        Assert.Equal(configured ? new[] { "settings" } : [], result.Items[0].Documents);
        Assert.Equal(WorkloadPlanDescriptionStatus.Unavailable, (await new DescribeWorkloadPlan(query, []).ExecuteAsync(inputs, default)).Status);
        Assert.Equal(WorkloadPlanDescriptionStatus.Unavailable, (await new DescribeWorkloadPlan(query, [describer, describer]).ExecuteAsync(inputs, default)).Status);
        Assert.Same(inputs, contexts[0].Inputs);
        Assert.DoesNotContain("Payload", result.ToString());
    }

    private sealed class Query(ManifestRevision revision) : IManifestRevisionQuery
    {
        public ValueTask<ManifestRevision?> FindAsync(ManifestRevisionRef reference, CancellationToken cancellationToken = default) => ValueTask.FromResult<ManifestRevision?>(revision);
    }
}
