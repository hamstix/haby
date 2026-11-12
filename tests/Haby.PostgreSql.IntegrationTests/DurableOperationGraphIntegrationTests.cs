using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ExactActionAdmissionTests
{
    private async Task<(string Connection, DurablePlan Plan, DurableChangeSetCommit Commit)> GraphCase(QueueBoundary boundary, Func<string, DurablePlan, Task>? beforeCommit = null)
    {
        var database = "graph_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin).ExecuteNonQueryAsync();
        }
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        await using var services = Services(connection, boundary);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var environment = await services.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var providers = services.GetRequiredService<IProviderConfigurationStore>();
        var config = new SampleConfiguration("queue-broker", "sample.impl/v1");
        var instance = await providers.CreateInstanceAsync(environment, config, default);
        await providers.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), config, default);
        await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment, ProviderAlias.Create("queues.primary"), null,
            new(instance, ProviderConfigurationRevisionNumber.Create(1)), "tester", default);
        var catalog = new ManifestContractCatalog([new ResourceTypeDefinition(QueueContract, "{\"type\":\"object\"}")], [], [], []);
        var payload = Encoding.UTF8.GetBytes("""
            {"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application",
            "metadata":{"id":"graph-app"},"spec":{"resources":{"jobs":{"type":"sample.queue/v1",
            "providerRef":"queues.primary","properties":{"queueName":"jobs"}}}}}}
            """);
        var revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), catalog, TimeProvider.System).ExecuteAsync(payload, "tester")).ManifestRevisionRef;
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, revision,
            new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
        var plan = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(plan.Id, default));
        Assert.Equal(0, boundary.Calls);
        // Planning code can change after calculation; Commit accepts the exact saved graph.
        boundary.PlannedName = "changed-planner";
        if (beforeCommit is not null) await beforeCommit(connection, plan);
        var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>()
            .CommitAsync(change.Id, plan.Id, "tester", default)).Commit;
        Assert.Equal(0, boundary.Calls);
        return (connection, plan, commit);
    }

    private static async Task AssertGraphCatalog(string connection, int successes, long? applied, int dispatched)
    {
        await using var verification = Services(connection, new QueueBoundary(), installed: false, codec: false);
        await using var context = await verification.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(3, await context.DurableOperations.AsNoTracking().CountAsync());
        Assert.Equal(successes, await context.DurableOperations.AsNoTracking().CountAsync(r => r.State == "Succeeded"));
        Assert.Equal(applied, (await context.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Equal(dispatched, await context.DurableOperationAttempts.AsNoTracking().CountAsync(r => r.DispatchedAt != null));
        var plan = await context.DurablePlans.AsNoTracking().SingleAsync();
        var generation = await context.DeploymentGenerations.AsNoTracking().SingleAsync();
        Assert.Equal(plan.GraphJson, generation.GraphJson);
        Assert.Equal(plan.Fingerprint, generation.Fingerprint);
    }

    [Theory]
    [InlineData("retry")]
    [InlineData("cancel")]
    [InlineData("failure")]
    [InlineData("uncertain")]
    [InlineData("crash")]
    public async Task Persisted_graph_stops_all_work_and_resumes_only_safe_states_after_restart(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        var boundary = new QueueBoundary { Graph = true, Mode = mode, Cancellation = cancellation };
        var scenario = await GraphCase(boundary);
        var id = scenario.Commit.DeploymentId;
        var gen = scenario.Commit.Generation;
        await using (var services = Services(scenario.Connection, boundary))
        {
            var direct = services.GetRequiredService<IDurableOperationExecutor>();
            Assert.Equal(DurableExecutionOutcome.Blocked, (await direct.ExecuteAsync(new(id, gen, OperationId.Create("20-dependent")), default)).Outcome);
            var runner = services.GetRequiredService<DurableGenerationRunner>();
            if (mode == "crash") await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(id, gen, default).AsTask());
            else
            {
                var stopped = await runner.RunAsync(id, gen, cancellation.Token);
                Assert.Equal(1, stopped.Progress!.Completed);
                Assert.Null(stopped.Progress.AppliedGeneration);
                Assert.Equal(mode == "cancel", stopped.Canceled);
            }
        }
        Assert.Equal(mode == "uncertain" ? 2 : 1, boundary.Calls);
        Assert.DoesNotContain(boundary.Effects, e => e.StartsWith("30-independent", StringComparison.Ordinal));
        await AssertGraphCatalog(scenario.Connection, 1, null, mode is "uncertain" or "crash" ? 2 : 1);
        // Pending/Preparing absence is a refusal with no new attempt; terminal/history reads need no support.
        await using (var missing = Services(scenario.Connection, boundary, installed: false, codec: false))
        {
            var runner = missing.GetRequiredService<DurableGenerationRunner>();
            var before = await runner.ProgressAsync(id, gen, default);
            var result = await runner.RunAsync(id, gen, default);
            Assert.Equal(1, result.Progress!.Completed);
            Assert.Equal(mode is "retry" or "cancel" ? DurableExecutionOutcome.HandlerUnavailable :
                mode == "failure" ? DurableExecutionOutcome.Failed : DurableExecutionOutcome.NeedsAttention, result.StopOutcome);
            Assert.Equal(before!.Nodes.Sum(n => n.Operation.AttemptNumber), result.Progress.Nodes.Sum(n => n.Operation.AttemptNumber));
        }
        boundary.Mode = null;
        await using (var restarted = Services(scenario.Connection, boundary))
        {
            var result = await restarted.GetRequiredService<DurableGenerationRunner>().RunAsync(id, gen, default);
            Assert.Equal(mode is "retry" or "cancel" ? 3 : 1, result.Progress!.Completed);
            Assert.Equal(mode is "retry" or "cancel" ? 1L : null, result.Progress.AppliedGeneration);
            await restarted.GetRequiredService<DurableGenerationRunner>().RunAsync(id, gen, default);
        }
        Assert.Equal(mode is "retry" or "cancel" ? 3 : mode == "uncertain" ? 2 : 1, boundary.Calls);
        Assert.All(boundary.Effects, effect => Assert.Contains(":jobs:", effect));
        Assert.Equal("10-first:jobs:1", boundary.Effects[0]);
        if (mode is "retry" or "cancel")
        {
            Assert.Equal(["10-first:jobs:1", "20-dependent:jobs:2", "30-independent:jobs:3"], boundary.Effects);
            await AssertGraphCatalog(scenario.Connection, 3, 1, 3);
        }
    }

    [Fact]
    public async Task Generation_and_direct_calls_share_one_gate_and_progress_does_not_wait_for_effects()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundary = new QueueBoundary { Graph = true, Pause = async token => { entered.SetResult(); await release.Task.WaitAsync(token); } };
        var scenario = await GraphCase(boundary);
        await using var services = Services(scenario.Connection, boundary);
        var runner = services.GetRequiredService<DurableGenerationRunner>();
        var running = runner.RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var direct = services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(
            new(scenario.Commit.DeploymentId, scenario.Commit.Generation, OperationId.Create("20-dependent")), default).AsTask();
        Assert.False(direct.IsCompleted);
        Assert.Equal(1, (await runner.ProgressAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default))!.Completed);
        release.SetResult();
        Assert.Equal(3, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Progress!.Completed);
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await direct).Outcome);
        Assert.Equal(3, boundary.Calls);
        await AssertGraphCatalog(scenario.Connection, 3, 1, 3);
    }

    private static async Task GraphSql(string connection, string sql)
    {
        await using var database = new NpgsqlConnection(connection); await database.OpenAsync();
        await new NpgsqlCommand(sql, database).ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Atomic_acceptance_rolls_back_the_complete_graph_and_can_retry_the_same_plan()
    {
        string connection = null!; DurablePlan plan = null!;
        var boundary = new QueueBoundary { Graph = true };
        await Assert.ThrowsAsync<DbUpdateException>(() => GraphCase(boundary, async (c, p) =>
        {
            connection = c; plan = p;
            await GraphSql(c, "ALTER TABLE durable_create_operations ADD CONSTRAINT test_reject_pending CHECK (state <> 'Pending')");
        }));
        await using (var verification = Services(connection, boundary))
        await using (var context = await verification.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(0, await context.ApplicationDeployments.AsNoTracking().CountAsync());
            Assert.Equal(0, await context.DeploymentGenerations.AsNoTracking().CountAsync());
            Assert.Equal(0, await context.ChangeSetCommits.AsNoTracking().CountAsync());
            Assert.Equal(0, await context.DurableOperations.AsNoTracking().CountAsync());
            Assert.Equal(1, await context.DurablePlans.AsNoTracking().CountAsync());
        }
        Assert.Equal(0, boundary.Calls);
        await GraphSql(connection, "ALTER TABLE durable_create_operations DROP CONSTRAINT test_reject_pending");
        await using var act = Services(connection, boundary);
        var committed = Assert.IsType<DurableCommitResult.Committed>(await act.GetRequiredService<IDurableDesiredStateCommitter>()
            .CommitAsync(plan.ChangeSetId, plan.Id, "tester", default)).Commit;
        Assert.Equal(3, (await act.GetRequiredService<DurableGenerationRunner>().RunAsync(committed.DeploymentId, committed.Generation, default)).Progress!.Completed);
        await AssertGraphCatalog(connection, 3, 1, 3);
    }

    [Fact]
    public async Task Aggregate_persistence_failure_retains_confirmed_last_node_and_resumes_without_replay()
    {
        var boundary = new QueueBoundary { Graph = true, Mode = "retry", StopId = "30-independent" };
        var scenario = await GraphCase(boundary);
        await using (var first = Services(scenario.Connection, boundary))
            Assert.Equal(2, (await first.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default)).Progress!.Completed);
        boundary.Mode = null;
        // GraphCase changes the planner after acceptance. Restore exact current support
        // so the all-succeeded retry reaches the injected persistence failure.
        boundary.PlannedName = "jobs";
        await GraphSql(scenario.Connection, "ALTER TABLE application_deployments ADD CONSTRAINT test_reject_applied CHECK (applied_generation IS NULL)");
        await using (var failing = Services(scenario.Connection, boundary))
            await Assert.ThrowsAsync<PostgresException>(() => failing.GetRequiredService<DurableGenerationRunner>()
                .RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default).AsTask());
        await AssertGraphCatalog(scenario.Connection, 3, null, 3);
        await using (var verify = Services(scenario.Connection, boundary))
        await using (var context = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            var operation = await context.DurableOperations.AsNoTracking().SingleAsync(r => r.OperationId == "30-independent");
            Assert.Equal("Succeeded", operation.State);
            var attempt = await context.DurableOperationAttempts.AsNoTracking().SingleAsync(r => r.AttemptId == operation.CurrentAttemptId);
            Assert.NotNull(attempt.FinishedAt); Assert.Equal("Succeeded", attempt.Outcome);
            Assert.Empty(await context.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
        }
        await GraphSql(scenario.Connection, "ALTER TABLE application_deployments DROP CONSTRAINT test_reject_applied");
        await using var restart = Services(scenario.Connection, boundary);
        var stopped = await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
        Assert.Null(stopped.StopOutcome); Assert.Equal(1, stopped.Progress!.AppliedGeneration);
        Assert.Equal(3, boundary.Calls); // The confirmed final step never repeats.
    }

    [Fact]
    public async Task Unsupported_saved_input_format_is_a_value_free_refusal_without_attempts_and_restored_support_resumes()
    {
        var boundary = new QueueBoundary { Graph = true };
        var scenario = await GraphCase(boundary);
        await using var services = Services(scenario.Connection, boundary);
        var executor = services.GetRequiredService<IDurableOperationExecutor>();
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await executor.ExecuteAsync(
            new(scenario.Commit.DeploymentId, scenario.Commit.Generation, OperationId.Create("10-first")), default)).Outcome);
        boundary.Format = "test.unavailable/v99";
        var runner = services.GetRequiredService<DurableGenerationRunner>();
        var unavailable = await runner.RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
        Assert.Equal(DurableExecutionOutcome.InputUnavailable, unavailable.StopOutcome);
        Assert.Equal(1, unavailable.Progress!.Completed); Assert.Null(unavailable.Progress.AppliedGeneration);
        Assert.Equal(1, unavailable.Progress.Nodes.Sum(n => n.Operation.AttemptNumber));
        boundary.Format = "sample.queue-create/v1";
        Assert.Equal(3, (await runner.RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default)).Progress!.Completed);
        Assert.Equal(3, boundary.Calls);
        await AssertGraphCatalog(scenario.Connection, 3, 1, 3);
    }

    [Fact]
    public async Task Pre_graph_catalog_is_rejected_without_reset_or_implicit_upgrade()
    {
        var name = "unsupported_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        { await admin.OpenAsync(); await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin).ExecuteNonQueryAsync(); }
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
        await using (var prepare = Services(connection, new QueueBoundary()))
        await using (var context = await prepare.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            await context.Database.MigrateAsync("20261102074004_AddProviderNeutralOperationReasons");
        await using var current = Services(connection, new QueueBoundary());
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => current.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync());
        Assert.Contains("fresh catalog", refused.Message);
        await using var verification = await current.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.DoesNotContain((await verification.Database.GetAppliedMigrationsAsync()), migration => migration.EndsWith("PersistOperationGraph", StringComparison.Ordinal));
        Assert.True(await verification.Database.CanConnectAsync());
    }
}
