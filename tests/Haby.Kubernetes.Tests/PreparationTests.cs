using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class PreparationTests
{
    internal static EffectiveWorkload Resolve(string properties = """{"image":{"reference":"registry.example/app:v1"}}""", string placementSpec = """{"namespace":"test"}""")
    {
        var envelope = JsonNode.Parse("""
            {"applicationVersion":"1.0.0","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"orders"},
            "spec":{"components":{"api":{"documents":{"settings":{"fileName":"settings.json","format":"json","template":{}}},
            "workloads":{"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary","properties":{}}}}}}}}
            """u8)!;
        envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"] = JsonNode.Parse(properties);
        var catalog = new ManifestContractCatalog([], [KubernetesContract.Definition], [], []);
        var manifest = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Encoding.UTF8.GetBytes(envelope.ToJsonString()), catalog)).Candidate.Declaration;
        var environment = EnvironmentRecordId.New(); var provider = ProviderInstanceId.New(); var revision = ProviderConfigurationRevisionNumber.Create(3);
        var implementation = ProviderImplementationId.Create("haby.kubernetes/v1"); var alias = ProviderAlias.Create("kubernetes.primary");
        var exact = new ExactProviderResolution(new ProviderInput.Workload(ManifestLocalId.Create("api"), ManifestLocalId.Create("main")), environment, alias, provider, revision, implementation);
        var placement = KubernetesContract.DecodePlacement(Encoding.UTF8.GetBytes("""
            {"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"standard","revision":1},"spec":
            """ + placementSpec + "}"));
        var inputs = new PlacementInputs([new(environment, alias, provider, revision, implementation, placement)], [new(new(environment, alias), placement.Reference)]);
        return Assert.Single(KubernetesWorkloadPlanner.Resolve(manifest, ApplicationVersion.Create("1.0.0"), new(manifest, environment, [exact]), inputs));
    }
    [Fact]
    public void Minimal_existing_contract_freezes_effective_defaults_and_pure_name()
    {
        var workload = Resolve(); var i = DeploymentIntents.Prepare(workload, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid());
        Assert.Equal("orders-api-main", i.Name); Assert.Equal("app", i.Target.Container); Assert.Equal("test", i.Namespace);
        Assert.Equal(1, i.Target.Replicas); Assert.Equal(600, i.Target.ProgressDeadlineSeconds); Assert.Equal("25%", i.Target.MaxSurge);
        Assert.Empty(i.Target.NodeSelector); Assert.Empty(i.Target.ImagePullSecrets);
    }
    [Theory]
    [InlineData("\"env\":{\"MODE\":{\"value\":\"requested\"}}")]
    [InlineData("\"ports\":{\"http\":{\"containerPort\":8080}}")]
    [InlineData("\"ports\":{\"http\":{\"containerPort\":8080}},\"probes\":{\"readiness\":{\"tcpSocket\":{\"port\":\"http\"}}}")]
    [InlineData("\"volumes\":{\"data\":{\"secret\":{\"name\":\"settings\"}}}")]
    [InlineData("\"volumes\":{\"data\":{\"configMap\":{\"name\":\"settings\"}}},\"mounts\":[{\"volume\":\"data\",\"mountPath\":\"/data\"}]")]
    [InlineData("\"ports\":{\"http\":{\"containerPort\":8080}},\"routes\":[{\"hostLabel\":\"orders\",\"path\":\"/\",\"port\":\"http\"}]")]
    [InlineData("\"configuration\":{\"documents\":[\"settings\"]}")]
    [InlineData("\"overrides\":{\"resources\":{\"requests\":{\"cpu\":\"1\"}}}")]
    public void Unsupported_requested_fields_refuse_before_any_adapter_can_execute(string extra)
    {
        var workload = Resolve("{\"image\":{\"reference\":\"registry.example/app:v1\"}," + extra + "}");
        Assert.Throws<WorkloadContractException>(() => DeploymentIntents.Prepare(workload, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid()));
    }
    [Theory]
    [InlineData("\"service\":{\"enabled\":true}")]
    [InlineData("\"headlessService\":{\"enabled\":true}")]
    [InlineData("\"resources\":{\"requests\":{\"memory\":\"64Mi\"}}")]
    [InlineData("\"ingress\":{\"enabled\":true,\"domain\":\"example.test\",\"className\":\"ingress\"}")]
    public void Unsupported_effective_Placement_defaults_refuse(string defaults)
    {
        var workload = Resolve();
        var placement = KubernetesContract.DecodePlacement(Encoding.UTF8.GetBytes("{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"KubernetesPlacement\",\"metadata\":{\"name\":\"standard\",\"revision\":1},\"spec\":{\"namespace\":\"test\"," + defaults + "}}"));
        // Exercise each kernel admission branch independently. The pure planner also refuses
        // companion defaults lacking ports/routes; those prerequisites must not mask this check.
        workload = workload with
        {
            Placement = workload.Placement with { Declaration = placement },
            Settings = workload.Settings with
            {
                Service = placement.Settings.Service,
                HeadlessService = placement.Settings.HeadlessService,
                Resources = placement.Settings.Resources,
                Ingress = placement.Settings.Ingress
            }
        };
        Assert.Throws<WorkloadContractException>(() => DeploymentIntents.Prepare(workload, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid()));
    }
    [Fact]
    public void Updates_cannot_rename_relocate_or_change_container_identity()
    {
        var workload = Resolve(); var owner = Guid.NewGuid(); var managed = Guid.NewGuid();
        var i = DeploymentIntents.Prepare(workload, owner, managed, 1, Guid.NewGuid());
        var receipt = DeploymentObjects.Receipt(KernelTests.Object(i), i, ConfirmationSource.Response);
        var renamed = workload with { Objects = [workload.Objects[0] with { Name = "renamed" }] };
        Assert.Throws<WorkloadContractException>(() => DeploymentIntents.Prepare(renamed, owner, managed, 2, Guid.NewGuid(), receipt));
        var moved = workload with { Objects = [workload.Objects[0] with { Namespace = "elsewhere" }] };
        Assert.Throws<WorkloadContractException>(() => DeploymentIntents.Prepare(moved, owner, managed, 2, Guid.NewGuid(), receipt));
        var container = workload with { Declaration = workload.Declaration with { ContainerName = "renamed" } };
        Assert.Throws<WorkloadContractException>(() => DeploymentIntents.Prepare(container, owner, managed, 2, Guid.NewGuid(), receipt));
    }
    [Fact]
    public void Keyed_removal_preserves_unowned_entries_and_escapes_map_keys()
    {
        var old = KernelTests.Intent(); var obj = KernelTests.Object(old);
        var spec = obj["spec"]!["template"]!["spec"]!;
        spec["nodeSelector"]!["injected"] = "keep";
        spec["imagePullSecrets"]!.AsArray().Insert(0, new JsonObject { ["name"] = "injected" });
        var target = old.Target with { NodeSelector = old.Target.NodeSelector.Remove("disk").Add("pool.example/role", "api"), ImagePullSecrets = ["next"] };
        var update = old with { ExpectedUid = "saved-uid", LastConfirmed = old.Target, Target = target };
        var patch = DeploymentObjects.Patch(obj, update);
        Assert.Contains(patch, n => n!["op"]!.GetValue<string>() == "remove" && n["path"]!.GetValue<string>() == "/spec/template/spec/nodeSelector/disk");
        Assert.Contains(patch, n => n!["path"]!.GetValue<string>() == "/spec/template/spec/nodeSelector/pool.example~1role");
        Assert.Contains(patch, n => n!["op"]!.GetValue<string>() == "test" && n["path"]!.GetValue<string>() == "/spec/template/spec/imagePullSecrets/1/name" && n["value"]!.GetValue<string>() == "registry");
        Assert.Contains(patch, n => n!["op"]!.GetValue<string>() == "remove" && n["path"]!.GetValue<string>() == "/spec/template/spec/imagePullSecrets/1");
        Assert.DoesNotContain(patch, n => n!["path"]!.GetValue<string>().EndsWith("/injected", StringComparison.Ordinal) || n["path"]!.GetValue<string>() == "/spec/template/spec/imagePullSecrets/0");
    }
    [Theory]
    [InlineData("node")]
    [InlineData("pull")]
    public void Newly_requested_key_cannot_claim_preexisting_injected_entry_even_when_equal(string kind)
    {
        var old = KernelTests.Intent(); var obj = KernelTests.Object(old);
        obj["spec"]!["template"]!["spec"]!["nodeSelector"]!["injected"] = "keep";
        obj["spec"]!["template"]!["spec"]!["imagePullSecrets"]!.AsArray().Add(new JsonObject { ["name"] = "injected" });
        var target = kind == "node" ? old.Target with { NodeSelector = old.Target.NodeSelector.Add("injected", "keep") } : old.Target with { ImagePullSecrets = ["registry", "injected"] };
        var update = old with { ExpectedUid = "saved-uid", LastConfirmed = old.Target, Target = target };
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Patch(obj, update));
    }
}
