using Grpc.Net.Client;
using Haby.Operator.Contracts;
using Haby.Application;
using Microsoft.EntityFrameworkCore;
using Haby.Operator.Host;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    [Fact]
    public async Task Retained_standalone_removal_is_an_explicit_zero_operation_update()
    {
        var scenario = await Prepare(full: true, render: true);
        await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var added = "retained_" + Guid.NewGuid().ToString("N");
        var second = await UpdatePlan(client, await ImportUpdate(client, MixedEnvelope(scenario, 2, added)), 1, created: 1);
        Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = second.ChangeSetId, PlanId = second.PlanId })).Status);
        Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 })).Status);
        var history = await ReuseHistory(scenario.Connection);
        var oid = await DatabaseOid(added); Assert.NotNull(oid);
        var removal = await UpdatePlan(client, await ImportUpdate(client, UpdateEnvelope(scenario, 3)), 2);
        Assert.Empty(removal.Operations);
        var retained = Assert.Single(removal.RetainedResources); Assert.Equal("extra", retained.ResourceId); Assert.Equal(2, retained.SourceGeneration);
        var request = new CommitDesiredStateRequest { ChangeSetId = removal.ChangeSetId, PlanId = removal.PlanId };
        var receipt = await client.CommitDesiredStateAsync(request); Assert.Equal(CommitStatus.Committed, receipt.Status);
        Assert.Equal(receipt.Commit, (await client.CommitDesiredStateAsync(request)).Commit);
        await using (var verify = Context(scenario.Connection))
        {
            var owner = await verify.ApplicationDeployments.AsNoTracking().SingleAsync();
            Assert.Equal(3, owner.DesiredGeneration); Assert.Equal(2, owner.AppliedGeneration);
            var graph = DurableOperationGraphCodec.Decode((await verify.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == 3)).GraphJson);
            Assert.Equal(Guid.Parse(retained.ManagementId), Assert.Single(graph.RetainedResources).ManagementId);
            Assert.Empty(await verify.DurableOperationAttempts.Where(x => x.Generation == 3).ToArrayAsync());
        }
        await host.StopAsync();
        await using var restarted = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(restarted);
        using var restartedChannel = GrpcChannel.ForAddress(restarted.Urls.Single()); var restartedClient = OperatorTestCredentials.Client(restartedChannel);
        Assert.Single((await restartedClient.GetGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 3 })).DesiredGeneration.RetainedResources);
        var result = await restartedClient.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 3 });
        Assert.Equal(GenerationExecutionStatus.Succeeded, result.Status); Assert.Equal(1, result.Progress.RetainedResources); Assert.Equal(3, result.Progress.AppliedGeneration);
        Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.Equal(oid, await DatabaseOid(added));
        await restarted.StopAsync();
    }
}
