using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class InternalDeploymentPlacementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_component_uses_its_frozen_exact_placement(bool distinct)
    {
        var contexts = Contexts(distinct);
        var handler = new InternalDeploymentAction(new NoSecrets());
        var api = DeploymentIntents.Decode(handler.Prepare(contexts[0]));
        var worker = DeploymentIntents.Decode(handler.Prepare(contexts[1]));
        Assert.Equal("accepted-api", api.Namespace);
        Assert.Equal(distinct ? "accepted-worker" : "accepted-api", worker.Namespace);
        Assert.Equal("api", api.Component); Assert.Equal("worker", worker.Component);
        Assert.Equal(distinct ? "worker" : "standard", worker.Placement);
        Assert.True(handler.Validate(contexts[0], DeploymentIntents.Encode(api)));
        Assert.True(handler.Validate(contexts[1], DeploymentIntents.Encode(worker)));
    }
    [Fact]
    public void Preparing_one_subject_retains_whole_generation_object_collision_refusal()
    {
        var handler = new InternalDeploymentAction(new NoSecrets());
        var contexts = Contexts(false, collision: true);
        foreach (var context in contexts)
            Assert.Equal("name.collision", Assert.Throws<WorkloadContractException>(() => handler.Prepare(context)).Code);
    }
    private static InternalActionContext[] Contexts(bool distinct, bool collision = false)
    {
        var envelope = JsonNode.Parse("""{"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"orders"},"spec":{"components":{"api":{"workloads":{"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary","properties":{"image":{"reference":"registry.example/app:v1"}}}}},"worker":{"workloads":{"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary","properties":{"image":{"reference":"registry.example/worker:v1"}}}}}}}}}"""u8)!;
        if (distinct) envelope["manifest"]!["spec"]!["components"]!["worker"]!["workloads"]!["main"]!["placementRef"] = "worker";
        if (collision) foreach (var component in new[] { "api", "worker" })
            envelope["manifest"]!["spec"]!["components"]![component]!["workloads"]!["main"]!["properties"]!["nameOverride"] = "shared";
        var manifest = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Encoding.UTF8.GetBytes(envelope.ToJsonString()),
            new([], [KubernetesContract.Definition], [], []))).Candidate.Declaration;
        var environment = EnvironmentRecordId.New(); var provider = ProviderInstanceId.New(); var revision = ProviderConfigurationRevisionNumber.Create(1);
        var reference = ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1));
        var scopes = ConfigurationPlanningSnapshot.Targets(manifest).Select(t => new ConfigurationPlanningScope(t, null, new(t, [])));
        var configuration = ConfigurationPlanningSnapshot.Prepare(reference, manifest, scopes, []).Snapshot!;
        PlacementDeclaration Placement(string name, string ns) => new KubernetesPlacementCodec().Decode(Encoding.UTF8.GetBytes(
            "{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"KubernetesPlacement\",\"metadata\":{\"name\":\"" + name + "\",\"revision\":1},\"spec\":{\"namespace\":\"" + ns + "\"}}"));
        var standard = Placement("standard", "accepted-api"); var worker = distinct ? Placement("worker", "accepted-worker") : standard;
        var placements = new[] { standard, worker }; var placementIds = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var subjects = new[] { "api", "worker" }.Select(c => new ProviderInput.Workload(ManifestLocalId.Create(c), ManifestLocalId.Create("main"))).ToArray();
        if (!distinct) placementIds[1] = placementIds[0];
        var exact = subjects.Select((s, i) => new ExactProviderResolution(s, environment, ProviderAlias.Create("kubernetes.primary"), provider,
            revision, ProviderImplementationId.Create(KubernetesProviderConfigurationCodec.Implementation), Placement: InternalPlacementInput.Encode(placementIds[i], placements[i]))).ToArray();
        var inputs = new DurablePlanningInputs(environment, EnvironmentId.Create("test"), reference, new(manifest, environment, exact)) { Configuration = configuration };
        var access = new KubernetesProviderConfigurationCodec().Encode(new(new Uri("https://api.example"), null, new(SecretId.New(), SecretVersionId.New()), null, null));
        var deployment = ApplicationDeploymentId.New();
        return subjects.Select((s, i) => new InternalActionContext(new(deployment, DeploymentGeneration.Create(2), OperationId.Create("effect-" + s.Component.Value)),
            inputs, s, exact[i], access, "v1", Guid.CreateVersion7(), Guid.CreateVersion7(), placements[i])).ToArray();
    }
    private sealed class NoSecrets : ISecretStore
    {
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken token) => throw new InvalidOperationException("Pure preparation resolved material.");
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken token) => throw new InvalidOperationException("Pure preparation resolved material.");
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material, CancellationToken token) => throw new InvalidOperationException("Pure preparation resolved material.");
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken token) => throw new InvalidOperationException("Pure preparation resolved material.");
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes, CancellationToken token) => throw new InvalidOperationException("Pure preparation resolved material.");
    }
}
