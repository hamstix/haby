using System.Text;
using Haby.Kubernetes.Contracts;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class PlacementDefaultsTests
{
    private const string Defaults = """{"namespace":"test","ports":{"http":{"containerPort":8080}},"probes":{"readiness":{"httpGet":{"port":"http","path":"/health"}}}}""";

    [Theory]
    [InlineData("", 8080, true)]
    [InlineData(",\"probes\":{}", 8080, false)]
    [InlineData(",\"ports\":{\"http\":{\"containerPort\":9090}}", 9090, true)]
    [InlineData(",\"ports\":{},\"probes\":{}", 0, false)]
    public void Environment_defaults_and_independent_whole_group_replacement_reach_frozen_intent(string extra, int port, bool readiness)
    {
        var effective = PreparationTests.Resolve("{\"image\":{\"reference\":\"registry.example/app:v1\"}" + extra + "}", Defaults);
        var intent = DeploymentIntents.Prepare(effective, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid());
        Assert.Equal(port == 0 ? 0 : 1, intent.Target.Ports.Count);
        if (port != 0) Assert.Equal(port, intent.Target.Ports["http"].Number);
        Assert.Equal(readiness, intent.Target.Probes.Readiness is not null);
        var reloaded = DeploymentIntents.Decode(DeploymentIntents.Encode(intent));
        Assert.Equal(intent.Target.Probes, reloaded.Target.Probes);
        Assert.Equal(intent.Target.Ports.ToArray(), reloaded.Target.Ports.ToArray());
    }

    [Fact]
    public void Portable_probe_references_can_be_supplied_by_the_other_group_at_planning()
    {
        const string properties = """{"image":{"reference":"registry.example/app:v1"},"probes":{"readiness":{"tcpSocket":{"port":"http"}}}}""";
        var declaration = KubernetesContract.DecodeWorkload(Encoding.UTF8.GetBytes(properties));
        Assert.NotNull(declaration.Probes.Readiness);
        var effective = PreparationTests.Resolve(properties, """{"namespace":"test","ports":{"http":{"containerPort":8080}}}""");
        var intent = DeploymentIntents.Prepare(effective, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid());
        Assert.Equal(8080, intent.Target.Ports["http"].Number);
        Assert.NotNull(intent.Target.Probes.Readiness);
    }

    [Fact]
    public void Portable_placement_probes_do_not_require_self_contained_ports()
    {
        const string defaults = """{"namespace":"test","probes":{"readiness":{"tcpSocket":{"port":"http"}}}}""";
        var effective = PreparationTests.Resolve("""{"image":{"reference":"registry.example/app:v1"},"ports":{"http":{"containerPort":8080}}}""", defaults);
        Assert.NotNull(DeploymentIntents.Prepare(effective, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid()).Target.Probes.Readiness);
    }

    [Theory]
    [InlineData("\"ports\":{}")]
    [InlineData("\"ports\":{\"http\":{\"containerPort\":8080,\"protocol\":\"UDP\"}}")]
    [InlineData("\"ports\":{\"other\":{\"containerPort\":8080}}")]
    public void Missing_or_UDP_effective_probe_ports_refuse_during_pure_plan(string group)
    {
        Assert.Throws<WorkloadContractException>(() => PreparationTests.Resolve(
            "{\"image\":{\"reference\":\"registry.example/app:v1\"}," + group + "}", Defaults));
    }

    [Fact]
    public void Probe_group_replaces_all_slots_without_partial_merge()
    {
        var effective = PreparationTests.Resolve("""{"image":{"reference":"registry.example/app:v1"},"probes":{"startup":{"tcpSocket":{"port":"http"}}}}""", Defaults);
        Assert.NotNull(effective.Probes.Startup);
        Assert.Null(effective.Probes.Readiness);
        var intent = DeploymentIntents.Prepare(effective, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid());
        var obj = KernelTests.Object(intent);
        var main = obj["spec"]!["template"]!["spec"]!["containers"]![0]!;
        Assert.Null(main["readinessProbe"]);
        main["startupProbe"]!["tcpSocket"]!["port"] = "other";
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response));
    }

    [Fact]
    public void Empty_groups_emit_no_ports_or_probe_slots_and_do_not_retarget_frozen_output()
    {
        var effective = PreparationTests.Resolve("""{"image":{"reference":"registry.example/app:v1"},"ports":{},"probes":{}}""", Defaults);
        var intent = DeploymentIntents.Prepare(effective, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid());
        var obj = KernelTests.Object(intent);
        var main = obj["spec"]!["template"]!["spec"]!["containers"]![0]!;
        Assert.Null(main["ports"]); Assert.Null(main["startupProbe"]); Assert.Null(main["livenessProbe"]); Assert.Null(main["readinessProbe"]);
        Assert.Empty(DeploymentIntents.Decode(DeploymentIntents.Encode(intent)).Target.Ports);
        Assert.Equal(new Probes(null, null, null), intent.Target.Probes);
    }

    [Fact]
    public void Route_and_Service_validation_uses_effective_inherited_ports_without_enabling_execution()
    {
        var effective = PreparationTests.Resolve("""{"image":{"reference":"registry.example/app:v1"},"routes":[{"hostLabel":"api","path":"/","port":"http"}],"overrides":{"service":{"enabled":true}}}""", Defaults);
        Assert.Contains(effective.Objects, x => x.Kind == "Service");
        Assert.Throws<WorkloadContractException>(() => DeploymentIntents.Prepare(effective, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid()));
    }

}
