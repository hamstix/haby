using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_membership_competing_commits_have_one_lock_observed_atomic_winner(bool reintroduce)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var name = "retain_race_" + Guid.NewGuid().ToString("N"); var baseline = reintroduce ? 3 : 2;
        await EstablishRetained(scenario, client, name, baseline);
        var history = await ReuseHistory(scenario.Connection);
        var removed = await UpdatePlan(client, await ImportUpdate(client, UpdateEnvelope(scenario, baseline + 1)), baseline);
        var declared = await UpdatePlan(client, await ImportUpdate(client, MixedEnvelope(scenario, baseline + 2, name)), baseline, added: true);
        var lockKey = $"haby-deployment:{Guid.Parse(scenario.Plan.Inputs.Environment.ToString()):N}:{Guid.Parse(scenario.Plan.Inputs.Revision.ApplicationRecordId.ToString()):N}";
        await using var blocker = new NpgsqlConnection(scenario.Connection); await blocker.OpenAsync(); await using var tx = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", blocker, tx))
        { hold.Parameters.AddWithValue(lockKey); await hold.ExecuteNonQueryAsync(); }
        var one = client.CommitDesiredStateAsync(new() { ChangeSetId = removed.ChangeSetId, PlanId = removed.PlanId }).ResponseAsync;
        var two = client.CommitDesiredStateAsync(new() { ChangeSetId = declared.ChangeSetId, PlanId = declared.PlanId }).ResponseAsync;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var observer = new NpgsqlConnection(scenario.Connection))
        {
            await observer.OpenAsync(deadline.Token);
            while (true)
            {
                await using var waiting = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())", observer);
                if ((long)(await waiting.ExecuteScalarAsync(deadline.Token))! >= 2) break;
                Assert.False(one.IsCompleted); Assert.False(two.IsCompleted); await Task.Delay(10, deadline.Token);
            }
        }
        await tx.CommitAsync();
        var results = await Task.WhenAll(one, two);
        var winner = Assert.Single(results, x => x.Status == CommitStatus.Committed); Assert.Single(results, x => x.Status == CommitStatus.ChangeSetStale);
        await using var verify = Context(scenario.Connection);
        var saved = await verify.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == baseline + 1);
        var graph = DurableOperationGraphCodec.Decode(saved.GraphJson);
        Assert.Equal(winner.Commit.PlanId == removed.PlanId ? 1 : 0, graph.RetainedResources.Count);
        Assert.Equal(baseline + 1, await verify.DeploymentGenerations.CountAsync());
        Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.NotNull(await DatabaseOid(name));
        await host.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_membership_acceptance_cancellation_rolls_back_or_keeps_exact_confirmed_receipt(bool reintroduce)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        using var before = new CancellationTokenSource(); using var after = new CancellationTokenSource();
        var cancelBefore = new ReuseCancelBeforeCommit(before); var cancelAfter = new ReuseCancelAfterCommit(after);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"],
            s => s.AddDbContextFactory<ManifestCatalogDbContext>(o => o.AddInterceptors(cancelBefore, cancelAfter)));
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var name = "retain_cancel_" + Guid.NewGuid().ToString("N"); var baseline = reintroduce ? 3 : 2;
        await EstablishRetained(scenario, client, name, baseline);
        var envelope = reintroduce ? MixedEnvelope(scenario, baseline + 1, name) : UpdateEnvelope(scenario, baseline + 1);
        var plan = await UpdatePlan(client, await ImportUpdate(client, envelope), baseline, added: reintroduce);
        var history = await ReuseHistory(scenario.Connection);
        var committer = host.Services.GetRequiredService<IDurableDesiredStateCommitter>();
        cancelBefore.Enabled = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await committer.CommitAsync(DeploymentChangeSetId.Parse(plan.ChangeSetId), DeploymentPlanId.Parse(plan.PlanId), "tester", before.Token));
        await using (var verify = Context(scenario.Connection))
        { Assert.Equal(baseline, await verify.DeploymentGenerations.CountAsync()); Assert.Equal(baseline, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration); }
        cancelBefore.Enabled = false; cancelAfter.Enabled = true;
        var result = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(DeploymentChangeSetId.Parse(plan.ChangeSetId), DeploymentPlanId.Parse(plan.PlanId), "tester", after.Token));
        Assert.True(after.IsCancellationRequested); Assert.Equal(baseline + 1, result.Commit.Generation.Value);
        var saved = (await committer.FindCommitAsync(result.Commit.ChangeSetId, default))!;
        Assert.Equal(saved, Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(result.Commit.ChangeSetId, result.Commit.PlanId, "retry", default)).Commit);
        Assert.Equal(history, await ReuseHistory(scenario.Connection));
        await using (var verify = Context(scenario.Connection))
        {
            Assert.Equal(baseline + 1, await verify.DeploymentGenerations.CountAsync());
            Assert.Equal(baseline, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            var graph = DurableOperationGraphCodec.Decode((await verify.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == baseline + 1)).GraphJson);
            Assert.Equal(reintroduce ? 0 : 1, graph.RetainedResources.Count);
        }
        await host.StopAsync();
    }

    [Fact]
    public async Task Retained_absent_noop_and_real_restore_keep_membership_and_allow_direct_original_reintroduction()
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var name = "retain_restore_" + Guid.NewGuid().ToString("N"); string history;
        await using (var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            await EstablishRetained(scenario, client, name);
            var noop = await UpdatePlan(client, await ImportUpdate(client, UpdateEnvelope(scenario, 3)), 3);
            Assert.Equal(3, (await client.CommitDesiredStateAsync(new() { ChangeSetId = noop.ChangeSetId, PlanId = noop.PlanId })).Commit.Generation);
            history = await ReuseHistory(scenario.Connection);
            await using var verify = Context(scenario.Connection); Assert.Equal(3, await verify.DeploymentGenerations.CountAsync());
            await host.StopAsync();
        }
        var oid = await DatabaseOid(name);
        var restored = _catalogPools.Register(new NpgsqlConnectionStringBuilder(await Database("retain_restore_")));
        var dump = "/tmp/haby-retained-" + Guid.NewGuid().ToString("N") + ".dump";
        try
        {
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + new NpgsqlConnectionStringBuilder(scenario.Connection).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
            await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + restored, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            var retained = Assert.Single((await client.GetGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 3 })).DesiredGeneration.RetainedResources);
            Assert.Equal(2, retained.SourceGeneration);
            var plan = await UpdatePlan(client, await ImportUpdate(client, MixedEnvelope(scenario, 4, name)), 3, host.Urls.Single(), added: true);
            Assert.True(plan.ReusedResources.Single(x => x.ResourceId == "extra").Reintroduced);
            Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
            Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 4 })).Status);
            Assert.Equal(history, await ReuseHistory(restored)); Assert.Equal(oid, await DatabaseOid(name));
            await host.StopAsync();
        }
        finally { await fixture.RunClientUtilityAsync("rm", "-f", dump); }
    }
}
