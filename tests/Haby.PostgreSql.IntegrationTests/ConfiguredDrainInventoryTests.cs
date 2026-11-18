using Haby.Application;
using Haby.Kubernetes;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Fact]
    public async Task Configured_contour_drain_inventory_contains_initial_peer_and_unready_confirmed_Deployments()
    {
        JsonNode envelope = null!;
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: value =>
        { ConfiguredDocuments(value, secondDocument: true); envelope = value.DeepClone(); });
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var deployment = new ConfiguredSubmissionFixture { DistinctOwnedUids = true };
        var identity = new IdentityFixture();
        var confirmed = new List<DeploymentReceipt>();
        var initialActions = await PrepareRollout(services, scenario, deployment, identity);
        var initial = await SubmitRollout(initialActions, scenario, deployment);
        confirmed.Add(DeploymentIntents.DecodeReceipt((await initialActions.HistoryAsync(initial, default))!.Output!.Payload));
        var peer = await AcceptBatchPeerAsync(services, scenario, envelope);
        var peerActions = await PrepareRollout(services, scenario with { Commit = peer }, deployment, identity);
        var peerKey = await SubmitRollout(peerActions, scenario with { Commit = peer }, deployment);
        confirmed.Add(DeploymentIntents.DecodeReceipt((await peerActions.HistoryAsync(peerKey, default))!.Output!.Payload));
        var unready = await AcceptBatchPeerAsync(services, scenario, envelope);
        deployment.Pending = true;
        var unreadyActions = await PrepareRollout(services, scenario with { Commit = unready }, deployment, identity);
        var unreadyKey = await SubmitRollout(unreadyActions, scenario with { Commit = unready }, deployment);
        confirmed.Add(DeploymentIntents.DecodeReceipt((await unreadyActions.HistoryAsync(unreadyKey, default))!.Output!.Payload));
        Assert.False((await unreadyActions.ObserveRolloutAsync(unready, "unready-fixture", new(MaximumRounds: 1), default)).Ready);
        await using (var verify = await contexts.CreateDbContextAsync())
            Assert.Equal(3, await verify.OwnedObjects.CountAsync(x => x.Role == "deployment"));
        var inventory = await ConfiguredDrainInventory.ReadAsync(contexts, confirmed, default);
        Assert.Equal(3, inventory.Length);
        Assert.Equal(confirmed.Select(x => (x.Owner, x.ManagedWorkload, x.Namespace, x.Name, x.Uid)).OrderBy(x => x.Owner),
            inventory.Select(x => (x.Owner, x.ManagedWorkload, x.Namespace, x.Name, x.Uid)).OrderBy(x => x.Owner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfiguredDrainInventory.ReadAsync(contexts, confirmed.Take(2).ToArray(), default));
        var replaced = confirmed.ToArray(); replaced[2] = replaced[2] with { Uid = "replacement" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfiguredDrainInventory.ReadAsync(contexts, replaced, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfiguredDrainInventory.ReadAsync(contexts, [confirmed[0], confirmed[1], confirmed[1]], default));
    }
}
