using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public abstract partial class ApplicationLoginScenario
{
    private protected sealed class NamingCreateBarrier : IDurableCreateDispatchFaultHooks
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sessions;
        public ValueTask BeforeDispatchMarkerAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask AfterDispatchMarkerCommittedAsync(CancellationToken token) => ValueTask.CompletedTask;
        public async ValueTask DuringCreateCommandAsync(CancellationToken token)
        {
            // Both absence checks and markers have completed, and both dedicated SQL
            // sessions are open. Neither CREATE may run before its competing session.
            if (Interlocked.Increment(ref _sessions) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
        }
        public ValueTask BeforeSuccessPersistedAsync(CancellationToken token) => ValueTask.CompletedTask;
    }
    private protected async Task<Scenario> NamingPlan(string logical, string prefix)
    {
        var catalog = await NamingCatalog();
        await using var services = Services(catalog.Connection, full: true);
        var policies = services.GetRequiredService<IResourceNamingStore>();
        await policies.CreateAsync(catalog.Environment, new("contour", 1), new("prefixed", prefix), "tester", default);
        await policies.SelectAsync(catalog.Environment, ProviderAlias.Create("postgresql.primary"),
            new(catalog.Provider, ProviderConfigurationRevisionNumber.Create(1)), null, new("contour", 1), "tester", default);
        var envelope = PostgreSqlPairPlanningTests.Example();
        envelope["manifest"]!["spec"]!["resources"]!["db"]!["properties"]!["name"] = logical;
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), PostgreSqlPairPlanningTests.Catalog, TimeProvider.System).ExecuteAsync(PostgreSqlPairPlanningTests.Bytes(envelope), "tester"));
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(catalog.Environment, imported.ManifestRevisionRef.ApplicationRecordId, imported.ManifestRevisionRef,
            new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
        var plan = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
        Assert.NotNull(plan);
        var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, plan.Id, "tester", default)).Commit;
        return new(catalog.Connection, prefix + logical, commit, plan) { DeclaredDatabase = logical };
    }

}

public sealed partial class ApplicationGenerationTests
{

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Naming_overlapping_prefixes_and_concurrent_external_database_conflicts_never_adopt_or_change_the_winner(bool concurrent)
    {
        var suffix = Guid.NewGuid().ToString("N")[..16];
        // staging_ + shard_orders == staging_shard_ + orders, including across independent catalogs.
        var scenarios = new[] { await NamingPlan("shard_orders_" + suffix, "staging_"), await NamingPlan("orders_" + suffix, "staging_shard_") };
        Assert.Equal(scenarios[0].Database, scenarios[1].Database);
        var barrier = concurrent ? new NamingCreateBarrier() : null;
        async Task<DurableGenerationRunResult> Run(Scenario scenario)
        {
            await using var services = Services(scenario.Connection, full: true, access: true, createFault: barrier);
            return await services.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
        }
        Scenario winner; Scenario loser;
        if (concurrent)
        {
            var results = await Task.WhenAll(scenarios.Select(Run));
            var success = Array.FindIndex(results, r => r.Progress?.AppliedGeneration == 1);
            Assert.InRange(success, 0, 1); Assert.Single(results, r => r.Progress?.AppliedGeneration == 1);
            winner = scenarios[success]; loser = scenarios[1 - success];
            foreach (var scenario in scenarios)
            {
                await using var verify = Context(scenario.Connection);
                var attempt = await verify.DurableOperationAttempts.AsNoTracking().SingleAsync(x => x.OperationId == "db-create");
                Assert.NotNull(attempt.DispatchedAt);
            }
        }
        else
        {
            winner = scenarios[0]; loser = scenarios[1];
            Assert.Equal(1, (await Run(winner)).Progress!.AppliedGeneration);
        }
        var password = await Password(winner); var material = await Material(winner, password);
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        { Database = winner.Database, Username = winner.Role, Password = Encoding.ASCII.GetString(material), Pooling = false };
        Array.Clear(material);
        try
        {
            await using (var runtime = new NpgsqlConnection(builder.ConnectionString))
            {
                await runtime.OpenAsync();
                await new NpgsqlCommand("CREATE TABLE app.naming_conflict_sentinel(value text); INSERT INTO app.naming_conflict_sentinel VALUES ('original-owner'); CREATE TABLE app.naming_conflict_writes(value integer)", runtime).ExecuteNonQueryAsync();
            }
            var external = await ExternalIdentity(winner); var history = await ReuseHistory(winner.Connection);
            var refused = await Run(loser);
            Assert.NotEqual(1, refused.Progress!.AppliedGeneration);
            Assert.Equal(DurableExecutionOutcome.Failed, refused.StopOutcome);
            Assert.False(await Exists(loser.Role));
            await using (var verify = Context(loser.Connection))
            { Assert.Empty(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToArrayAsync(verify.GeneratedCredentialSlots)); }
            Assert.Equal(external, await ExternalIdentity(winner)); Assert.Equal(history, await ReuseHistory(winner.Connection));
            await using var received = new NpgsqlConnection(builder.ConnectionString); await received.OpenAsync();
            Assert.Equal("original-owner", await new NpgsqlCommand("SELECT value FROM app.naming_conflict_sentinel", received).ExecuteScalarAsync());
            await new NpgsqlCommand("INSERT INTO app.naming_conflict_writes VALUES (175)", received).ExecuteNonQueryAsync();
            Assert.Equal(password, await Password(winner));
        }
        finally { builder.Password = null; }
    }

}
