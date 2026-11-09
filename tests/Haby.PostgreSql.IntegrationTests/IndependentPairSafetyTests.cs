using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    private sealed class PairStageFault(string stage, CancellationTokenSource? cancel) :
        IDurableCreateDispatchFaultHooks, IPostgreSqlRestrictFaultHooks, IPostgreSqlLoginFaultHooks, IPostgreSqlAccessFaultHooks
    {
        public int Calls;
        private ValueTask Stop(string current)
        {
            if (stage != current) return ValueTask.CompletedTask;
            Calls++;
            if (cancel is not null) { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); }
            return ValueTask.FromException(new IOException("Injected lost confirmation."));
        }
        public ValueTask BeforeDispatchMarkerAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask AfterDispatchMarkerCommittedAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DuringCreateCommandAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask BeforeSuccessPersistedAsync(CancellationToken token) => Stop("db-b-create");
        ValueTask IPostgreSqlRestrictFaultHooks.BeforeFinalizationAsync(CancellationToken token) => Stop("db-b-restrict");
        ValueTask IPostgreSqlLoginFaultHooks.BeforeFinalizationAsync(CancellationToken token) => Stop("login-b-create");
        ValueTask IPostgreSqlAccessFaultHooks.BeforeFinalizationAsync(CancellationToken token) => Stop("login-b-access");
    }
    private sealed class PairMaterialFault(IDbContextFactory<ManifestCatalogDbContext> contexts, CancellationTokenSource? cancel) : IGeneratedCredentialSlots
    {
        public bool Enabled = true;
        public ValueTask<GeneratedCredentialResult> GetOrCreateAsync(GeneratedCredentialSlot slot, string profile, CancellationToken token)
        {
            if (Enabled)
            {
                cancel?.Cancel(); token.ThrowIfCancellationRequested();
                return new(new GeneratedCredentialResult(GeneratedCredentialOutcome.MaterialUnavailable));
            }
            return new PostgreSqlGeneratedCredentialSlots(contexts).GetOrCreateAsync(slot, profile, token);
        }
    }

    [Theory]
    [InlineData("db-b-create", false)]
    [InlineData("db-b-restrict", false)]
    [InlineData("login-b-material", false)]
    [InlineData("login-b-create", false)]
    [InlineData("login-b-access", false)]
    [InlineData("db-b-create", true)]
    [InlineData("db-b-restrict", true)]
    [InlineData("login-b-material", true)]
    [InlineData("login-b-create", true)]
    [InlineData("login-b-access", true)]
    public async Task Independent_pair_each_stage_interruption_preserves_A_completed_B_and_never_replays_uncertainty(string stage, bool cancellation)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var identity = await ExternalIdentity(scenario);
        using var source = new CancellationTokenSource();
        var hooks = new PairStageFault(stage, cancellation ? source : null);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
        {
            services.AddSingleton<IDurableCreateDispatchFaultHooks>(hooks).AddSingleton<IPostgreSqlRestrictFaultHooks>(hooks)
                .AddSingleton<IPostgreSqlLoginFaultHooks>(hooks).AddSingleton<IPostgreSqlAccessFaultHooks>(hooks);
            if (stage == "login-b-material") services.AddSingleton<IGeneratedCredentialSlots>(p => new PairMaterialFault(p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), cancellation ? source : null));
        });
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var plan = await IndependentPlan(client, await ImportUpdate(client, IndependentEnvelope(scenario, 2, "pair_stop_" + Guid.NewGuid().ToString("N"))), 1, host.Urls.Single(), 5);
        Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
        var runner = host.Services.GetRequiredService<DurableGenerationRunner>();
        try { _ = await runner.RunAsync(scenario.Commit.DeploymentId, DeploymentGeneration.Create(2), source.Token); }
        catch (OperationCanceledException) when (cancellation && source.IsCancellationRequested) { }
        catch (IOException) when (stage == "db-b-create" && !cancellation) { }
        var order = new[] { "db-b-create", "db-b-restrict", "login-b-material", "login-b-create", "login-b-access" };
        var completed = Array.IndexOf(order, stage);
        await using (var verify = Context(scenario.Connection))
        {
            Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Equal(completed, await verify.DurableOperations.CountAsync(n => n.Generation == 2 && n.State == "Succeeded"));
            Assert.Equal(5, await verify.DurableOperationAttempts.CountAsync(n => n.Generation == 1));
            Assert.All(await verify.DurableOperations.AsNoTracking().Where(n => n.Generation == 1).ToArrayAsync(), n => Assert.Equal("Succeeded", n.State));
        }
        Assert.Equal(identity, await ExternalIdentity(scenario));
        if (stage == "login-b-material") ((PairMaterialFault)host.Services.GetRequiredService<IGeneratedCredentialSlots>()).Enabled = false;
        var repeat = await runner.RunAsync(scenario.Commit.DeploymentId, DeploymentGeneration.Create(2), default);
        Assert.Equal(stage == "login-b-material" ? 2 : 1, repeat.Progress!.AppliedGeneration);
        if (stage != "login-b-material") Assert.Equal(1, hooks.Calls);
        Assert.Equal(identity, await ExternalIdentity(scenario));
        await using (var verify = Context(scenario.Connection))
        {
            foreach (var id in order.Take(completed)) Assert.Single(await verify.DurableOperationAttempts.AsNoTracking().Where(n => n.Generation == 2 && n.OperationId == id).ToArrayAsync());
        }
        await host.StopAsync();
    }

    [Theory]
    [InlineData("old-output")]
    [InlineData("key")]
    [InlineData("attempt")]
    [InlineData("source")]
    [InlineData("password")]
    public async Task Independent_pair_corrupt_original_sources_refuse_without_new_work(string fault)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var identity = await ExternalIdentity(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var database = "pair_evidence_" + Guid.NewGuid().ToString("N");
        var plan = await IndependentPlan(client, await ImportUpdate(client, IndependentEnvelope(scenario, 2, database)), 1, host.Urls.Single(), 5);
        Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
        if (fault == "old-output")
        {
            await Sql(scenario.Connection, "SET session_replication_role = replica; DELETE FROM operation_outputs WHERE operation_id = 'app-login-access'");
            Assert.Equal(GenerationExecutionStatus.InputUnavailable, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 })).Status);
            Assert.Null(await DatabaseOid(database));
            await using var verify = Context(scenario.Connection);
            Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().Where(n => n.Generation == 2).ToArrayAsync());
        }
        else
        {
            Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 })).Status);
            await Sql(scenario.Connection, "SET session_replication_role = replica; " + (fault switch
            {
                "key" => "UPDATE operation_outputs SET output_key = 'wrong' WHERE operation_id = 'login-b-access'",
                "attempt" => "UPDATE durable_create_operation_attempts SET outcome = 'Uncertain' WHERE operation_id = 'login-b-create'",
                "source" => "UPDATE resource_management SET source_generation = 1 WHERE resource_local_id = 'login-b'",
                _ => "UPDATE operation_outputs SET payload = jsonb_set(payload::jsonb, '{password}', (SELECT payload::jsonb->'password' FROM operation_outputs WHERE operation_id = 'app-login-access'))::text WHERE operation_id = 'login-b-access'"
            }));
            var revision = await ImportUpdate(client, IndependentEnvelope(scenario, 3, database));
            var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = 2 });
            Assert.Equal(PlanningStatus.PlanInputUnavailable, (await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId })).Status);
            await using var verify = Context(scenario.Connection);
            Assert.Equal(2, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
            Assert.Equal(10, await verify.DurableOperationAttempts.CountAsync());
        }
        Assert.Equal(identity, await ExternalIdentity(scenario)); await host.StopAsync();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_pair_conflicts_preserve_old_ownership_and_never_adopt(bool roleReservation)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var identity = await ExternalIdentity(scenario);
        var database = "pair_conflict_" + Guid.NewGuid().ToString("N");
        if (!roleReservation) await Sql(fixture.ConnectionString, $"CREATE DATABASE \"{database}\"");
        var oldOid = await DatabaseOid(database);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var plan = await IndependentPlan(client, await ImportUpdate(client, IndependentEnvelope(scenario, 2, database)), 1, host.Urls.Single(), 5);
        if (roleReservation)
        {
            var owner = scenario.Owner;
            var name = PostgreSqlPairResourceAdmission.RoleName(owner with { ResourceLocalId = ManifestLocalId.Create("login-b") });
            Assert.True(await host.Services.GetRequiredService<IProviderResourceReservations>().TryReserveAsync(new(
                scenario.Plan.Inputs.ProviderInstance, owner.EnvironmentRecordId, owner.ApplicationRecordId, ManifestLocalId.Create("foreign-owner"),
                PostgreSqlApplicationLoginAction.RoleReservationNamespace, name), default));
        }
        Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
        for (var repeat = 0; repeat < 2; repeat++) Assert.Equal(GenerationExecutionStatus.Failed,
            (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 })).Status);
        Assert.Equal(identity, await ExternalIdentity(scenario));
        if (!roleReservation) Assert.Equal(oldOid, await DatabaseOid(database));
        await using var verify = Context(scenario.Connection);
        Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Equal(roleReservation ? 4 : 1, await verify.DurableOperationAttempts.CountAsync(a => a.Generation == 2));
        Assert.Empty(await verify.OperationOutputs.AsNoTracking().Where(o => o.OperationId == "login-b-access").ToArrayAsync());
        await host.StopAsync();
    }

}
