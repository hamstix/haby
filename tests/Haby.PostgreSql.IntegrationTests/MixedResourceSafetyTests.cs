using Grpc.Net.Client;
using Haby.Application;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public abstract partial class ApplicationLoginScenario
{
    private protected sealed class MixedHooks : IDurableCreateDispatchFaultHooks
    {
        public Func<CancellationToken, ValueTask>? AfterMarker;
        public Func<CancellationToken, ValueTask>? BeforeSuccess;
        public ValueTask BeforeDispatchMarkerAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask AfterDispatchMarkerCommittedAsync(CancellationToken token) => AfterMarker?.Invoke(token) ?? ValueTask.CompletedTask;
        public ValueTask DuringCreateCommandAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask BeforeSuccessPersistedAsync(CancellationToken token) => BeforeSuccess?.Invoke(token) ?? ValueTask.CompletedTask;
    }

}

public sealed partial class ApplicationGenerationTests
{
    [Fact]
    public async Task Mixed_external_name_conflict_preserves_old_resources_and_blocks_next_generation()
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var name = "mixed_conflict_" + Guid.NewGuid().ToString("N");
        await Sql(fixture.ConnectionString, $"CREATE DATABASE \"{name}\"");
        var oid = await DatabaseOid(name); var identity = await ExternalIdentity(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var plan = await UpdatePlan(client, await ImportUpdate(client, MixedEnvelope(scenario, 2, name)), 1, created: 1);
        Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
        for (var repeat = 0; repeat < 2; repeat++) Assert.Equal(GenerationExecutionStatus.Failed,
            (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 })).Status);
        Assert.Equal(oid, await DatabaseOid(name)); Assert.Equal(identity, await ExternalIdentity(scenario));
        await using var verify = Context(scenario.Connection);
        var attempt = Assert.Single(await verify.DurableOperationAttempts.AsNoTracking().Where(x => x.Generation == 2).ToArrayAsync());
        Assert.Equal("Conflict", attempt.Outcome); Assert.Null(attempt.DispatchedAt);
        Assert.Empty(await verify.OperationOutputs.AsNoTracking().Where(x => x.Generation == 2).ToArrayAsync());
        Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("contract")]
    [InlineData("source")]
    public async Task Mixed_new_original_producer_requires_its_own_output_contract_and_creation_source(string fault)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var name = "mixed_evidence_" + Guid.NewGuid().ToString("N");
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var plan = await UpdatePlan(client, await ImportUpdate(client, MixedEnvelope(scenario, 2, name)), 1, created: 1);
        Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
        Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 })).Status);
        var oid = await DatabaseOid(name); Assert.NotNull(oid);
        await Sql(scenario.Connection, "SET session_replication_role = replica; " + (fault switch
        {
            "missing" => "DELETE FROM operation_outputs WHERE generation = 2",
            "contract" => "UPDATE operation_outputs SET output_contract = 'sample.invalid/v1' WHERE generation = 2",
            _ => "UPDATE resource_management SET source_generation = 1 WHERE resource_local_id = 'extra'"
        }));
        var revision = await ImportUpdate(client, MixedEnvelope(scenario, 3, name));
        var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = 2 });
        Assert.Equal(PlanningStatus.PlanInputUnavailable, (await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId })).Status);
        Assert.Equal(oid, await DatabaseOid(name));
        await using var verify = Context(scenario.Connection);
        Assert.Equal(2, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
        Assert.Equal(6, await verify.DurableOperationAttempts.CountAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task Mixed_generation_and_operation_overlap_share_gate_and_canceled_waiter_never_dispatches()
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new MixedHooks { AfterMarker = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"],
            s => s.AddSingleton<IDurableCreateDispatchFaultHooks>(hooks));
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var plan = await UpdatePlan(client, await ImportUpdate(client, MixedEnvelope(scenario, 2, "mixed_gate_" + Guid.NewGuid().ToString("N"))), 1, created: 1);
        Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
        var request = new GenerationRequest { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 };
        using var first = client.ExecuteGenerationAsync(request);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var second = client.ExecuteGenerationAsync(request);
            using var direct = client.ExecuteOperationAsync(new() { DeploymentId = request.DeploymentId, Generation = 2, OperationId = "extra-create" });
            using var cancellation = new CancellationTokenSource();
            using var waiter = client.ExecuteGenerationAsync(request, cancellationToken: cancellation.Token);
            cancellation.Cancel();
            Assert.Equal(Grpc.Core.StatusCode.Cancelled, (await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () => await waiter)).StatusCode);
            var progress = await client.GetGenerationAsync(request);
            Assert.Equal(1, progress.Progress.AppliedGeneration); Assert.Equal(2, progress.Progress.ReusedResources);
            Assert.Equal(OperationState.Dispatching, Assert.Single(progress.Progress.Nodes).Operation.State);
            Assert.False(second.ResponseAsync.IsCompleted); Assert.False(direct.ResponseAsync.IsCompleted);
            release.TrySetResult();
            Assert.Equal(GenerationExecutionStatus.Succeeded, (await first.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(20))).Status);
            Assert.Equal(GenerationExecutionStatus.Succeeded, (await second.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(20))).Status);
            Assert.Equal(OperationStatus.Succeeded, (await direct.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(20))).Status);
        }
        finally { release.TrySetResult(); }
        await using var verify = Context(scenario.Connection);
        Assert.Single(await verify.DurableOperationAttempts.AsNoTracking().Where(x => x.Generation == 2).ToArrayAsync());
        await host.StopAsync();
    }

    [Theory]
    [InlineData("management")]
    [InlineData("output")]
    [InlineData("contract")]
    [InlineData("attempt")]
    public async Task Mixed_corrupt_reuse_prevents_new_dispatch_and_attempts(string fault)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var name = "mixed_block_" + Guid.NewGuid().ToString("N");
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var plan = await UpdatePlan(client, await ImportUpdate(client, MixedEnvelope(scenario, 2, name)), 1, created: 1);
        Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
        await Sql(scenario.Connection, "SET session_replication_role = replica; " + (fault switch
        {
            "management" => "UPDATE resource_management SET environment_record_id = '00000000-0000-0000-0000-000000000001' WHERE resource_local_id = 'app-login'",
            "output" => "DELETE FROM operation_outputs WHERE operation_id = 'app-login-access'",
            "contract" => "UPDATE operation_outputs SET output_contract = 'sample.invalid/v1' WHERE operation_id = 'app-login-access'",
            _ => "UPDATE durable_create_operation_attempts SET outcome = 'Uncertain' WHERE operation_id = 'db-create'"
        }));
        for (var repeat = 0; repeat < 2; repeat++) Assert.Equal(GenerationExecutionStatus.InputUnavailable,
            (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 })).Status);
        Assert.Null(await DatabaseOid(name));
        await using var verify = Context(scenario.Connection);
        Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().Where(x => x.Generation == 2).ToArrayAsync());
        Assert.Equal("Pending", (await verify.DurableOperations.AsNoTracking().SingleAsync(x => x.Generation == 2)).State);
        Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        await host.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mixed_changed_reuse_at_finalization_or_lost_success_never_advances_or_replays(bool uncertain)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var name = "mixed_final_" + Guid.NewGuid().ToString("N");
        var hooks = new MixedHooks
        {
            BeforeSuccess = async _ =>
            {
                if (uncertain) throw new IOException("Injected lost acknowledgment.");
                await Sql(scenario.Connection, "UPDATE durable_create_operation_attempts SET outcome = 'Uncertain' WHERE operation_id = 'db-create' AND generation = 1");
            }
        };
        await using (var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"],
            s => s.AddSingleton<IDurableCreateDispatchFaultHooks>(hooks)))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            var plan = await UpdatePlan(client, await ImportUpdate(client, MixedEnvelope(scenario, 2, name)), 1, created: 1);
            Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
            if (uncertain)
                await Assert.ThrowsAsync<IOException>(() => host.Services.GetRequiredService<DurableGenerationRunner>()
                    .RunAsync(scenario.Commit.DeploymentId, Haby.Domain.DeploymentGeneration.Create(2), default).AsTask());
            else
            {
                var executed = await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 });
                Assert.Equal(GenerationExecutionStatus.InputUnavailable, executed.Status);
                Assert.Equal(1, executed.Progress.Completed); Assert.Equal(1, executed.Progress.AppliedGeneration);
            }
            await host.StopAsync();
        }
        var oid = await DatabaseOid(name); Assert.NotNull(oid);
        // Remove only the test-injected metadata fault. This is not a product repair path.
        if (!uncertain) await Sql(scenario.Connection, "UPDATE durable_create_operation_attempts SET outcome = 'Succeeded' WHERE operation_id = 'db-create' AND generation = 1");
        await using var restart = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(restart);
        using var nextChannel = GrpcChannel.ForAddress(restart.Urls.Single()); var next = OperatorTestCredentials.Client(nextChannel);
        for (var repeat = 0; repeat < 2; repeat++) Assert.Equal(uncertain ? GenerationExecutionStatus.NeedsAttention : GenerationExecutionStatus.Succeeded,
            (await next.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 })).Status);
        Assert.Equal(oid, await DatabaseOid(name));
        var change = await next.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = await ImportUpdate(next, MixedEnvelope(scenario, 3, name)), Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = 2 });
        var planned = await next.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
        Assert.Equal(uncertain ? PlanningStatus.PlanInputUnavailable : PlanningStatus.Planned, planned.Status);
        // Mixed graphs do not acquire standalone uncertain-create assignment semantics.
        var recovery = await next.InspectOperationRecoveryAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2, OperationId = "extra-create" });
        Assert.Equal(RecoveryStatus.Found, recovery.Status); Assert.False(recovery.Preview.HasUncertainAttemptId);
        await using var verify = Context(scenario.Connection);
        Assert.Single(await verify.DurableOperationAttempts.AsNoTracking().Where(x => x.Generation == 2).ToArrayAsync());
        Assert.Equal(uncertain ? 1 : 2, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        await restart.StopAsync();
    }

}
