using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Kubernetes;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    internal static void PortsAndProbes(JsonNode envelope)
    {
        var properties = envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!;
        properties["ports"] = JsonNode.Parse("""{"web":{"containerPort":8080},"dns":{"containerPort":5353,"protocol":"UDP"}}"""u8);
        properties["probes"] = JsonNode.Parse("""
            {"startup":{"httpGet":{"port":"web","path":"/health"}},"liveness":{"tcpSocket":{"port":"web"}},
            "readiness":{"httpGet":{"port":"web","path":"/health"},"periodSeconds":1,"failureThreshold":1}}
            """u8);
    }

    private static void ExactPortsAndProbes(DeploymentProjection projection)
    {
        Assert.Equal(new[] { "dns", "web" }, projection.Ports.Keys);
        Assert.Equal(new Haby.Kubernetes.Contracts.ContainerPort(8080, "TCP"), projection.Ports["web"]);
        Assert.Equal(new Haby.Kubernetes.Contracts.ContainerPort(5353, "UDP"), projection.Ports["dns"]);
        Assert.Equal(new("web", "/health", "HTTP", 0, 10, 1, 1, 3), projection.Probes.Startup);
        Assert.Equal(new("web", null, null, 0, 10, 1, 1, 3), projection.Probes.Liveness);
        Assert.Equal(new("web", "/health", "HTTP", 0, 1, 1, 1, 1), projection.Probes.Readiness);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Ports_probes_survive_accepted_inputs_submission_restart_and_fresh_Applied(bool initial, bool configured)
    {
        var scenario = await InternalScenarioAsync(initialOnly: initial, configureManifest: e =>
        {
            if (configured) ConfiguredDocuments(e);
            PortsAndProbes(e);
        });
        var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        string payload, output; DurableOperationKey key;
        await using (var services = InternalServices(scenario.Connection, scenario.Time))
        {
            var actions = await PrepareRollout(services, scenario, deployment, identity, configured);
            key = await SubmitRollout(actions, scenario, deployment);
            var history = (await actions.HistoryAsync(key, default))!;
            payload = history.Intent.Payload; output = history.Output!.Payload;
            ExactPortsAndProbes(DeploymentIntents.Decode(payload).Target);
            ExactPortsAndProbes(DeploymentIntents.DecodeReceipt(output).Projection);
        }
        await using var restarted = InternalServices(scenario.Connection, scenario.Time);
        var freshActions = ConfiguredActions(restarted, deployment, identity);
        var reloaded = (await freshActions.HistoryAsync(key, default))!;
        Assert.Equal(payload, reloaded.Intent.Payload); Assert.Equal(output, reloaded.Output!.Payload);
        var pending = BatchFacade(restarted, scenario, freshActions, deployment, identity);
        deployment.Pending = true;
        var failed = await pending.WaitAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, "probe-pending", "operator", new(MaximumRounds: 1), null, default);
        Assert.False(failed.CurrentReady); Assert.False(failed.CurrentApplied);
        deployment.Pending = false;
        var ready = await pending.WaitAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, "probe-ready", "operator", new(MaximumRounds: 1), null, default);
        Assert.True(ready.CurrentReady); Assert.True(ready.CurrentApplied, ready.CompletionReason);
        var historyAfter = (await freshActions.HistoryAsync(key, default))!;
        Assert.Equal(payload, historyAfter.Intent.Payload); Assert.Equal(output, historyAfter.Output!.Payload);
        Assert.Equal(1, deployment.Writes);
        await using var verify = await restarted.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(scenario.Commit.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }
}
