using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    private static readonly ProviderInput.Workload RuntimeSubject = new(ManifestLocalId.Create("api"), ManifestLocalId.Create("main"));
    internal static InternalDurableActions IdentityActions(ServiceProvider services, IInternalActionHandler handler, Action<InternalActionFault>? fault = null) =>
        new(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), [handler], services.GetServices<IProviderConfigurationCodec>(),
            services.GetServices<IExternalOperationOutputContract>(), services.GetRequiredService<DurableActionAdmission>(),
            services.GetRequiredService<DurableOperationSerialization>(), services.GetRequiredService<TimeProvider>(), fault,
            ConfigurationPreparation(services).Preparation);
    private static InternalRuntimeIdentityPreparation IdentityPreparation(ServiceProvider services, IInternalActionHandler handler, Action<InternalActionFault>? fault = null) =>
        new(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), ConfigurationPreparation(services).Preparation,
            IdentityActions(services, handler, fault), handler.Contract.Action);
    private sealed class IdentityFixture : IInternalActionHandler
    {
        private readonly InternalRuntimeIdentityAction adapter = new(null!);
        internal int Writes; internal int Reads; internal bool Exists; internal string Uid = "persisted-uid"; internal bool LoseResponse = false;
        public InternalActionContract Contract => adapter.Contract;
        public bool ObservationRecovery => true;
        public bool RequiresPreparedConfiguration => true;
        public InternalManagedTarget ManagedTarget(InternalActionContext context) => adapter.ManagedTarget(context);
        public string Prepare(InternalActionContext context) => adapter.Prepare(context);
        public bool Validate(InternalActionContext context, string payload) => adapter.Validate(context, payload);
        public bool ValidateReceipt(InternalActionContext context, string payload, string receipt, bool observation) => adapter.ValidateReceipt(context, payload, receipt, observation);
        public InternalIdentityConfirmation IdentityConfirmation(InternalActionContext context, string payload, string receipt) => adapter.IdentityConfirmation(context, payload, receipt);
        public ValueTask<InternalActionResult> ExecuteAsync(InternalActionContext context, string payload, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Writes++; Exists = true;
            return ValueTask.FromResult(LoseResponse ? new InternalActionResult(InternalActionOutcome.Uncertain, "fixture-response-lost") : Result(payload, false));
        }
        public ValueTask<InternalActionResult> ObserveAsync(InternalActionContext context, string payload, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Reads++; return ValueTask.FromResult(Exists ? Result(payload, true) : new(InternalActionOutcome.NotConfirmed, "object-absent")); }
        private InternalActionResult Result(string payload, bool observation) => new(InternalActionOutcome.Confirmed, "fixture-confirmed",
            RuntimeIdentityCodec.Encode(new RuntimeAccountReceipt(RuntimeIdentityCodec.Decode(payload), Uid, false,
                observation ? ConfirmationSource.Observation : ConfirmationSource.Response)));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exact_initial_and_mixed_runtime_preparation_survives_restart_and_replay_without_access(bool initial)
    {
        var scenario = await InternalScenarioAsync(initialOnly: initial, configureManifest: e => ConfiguredDocuments(e, secondDocument: true));
        var handler = new IdentityFixture(); Guid binding; string audience;
        await using (var services = InternalServices(scenario.Connection, scenario.Time))
        {
            Assert.Equal(2, (await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default)).Prepared.Count);
            var first = Assert.Single(await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default));
            Assert.Equal(InternalActionOutcome.Confirmed, first.Result.Outcome); binding = first.Binding!.Value;
        }
        var restored = await RestoreRuntimeCatalog(scenario.Connection);
        await using (var restarted = InternalServices(restored, scenario.Time))
        {
            await restarted.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var replay = Assert.Single(await IdentityPreparation(restarted, handler).PrepareAsync(scenario.Commit, default));
            Assert.Equal(binding, replay.Binding); Assert.Equal(1, handler.Writes); Assert.Equal(1, handler.Reads);
        }
        await using (var verify = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(scenario.Connection).Options))
        {
            var identity = await verify.RuntimeIdentityBindings.AsNoTracking().SingleAsync(); audience = identity.Audience;
            var installation = await verify.InstallationIdentities.AsNoTracking().SingleAsync();
            Assert.Equal($"urn:haby:{installation.Id:D}:{identity.ProviderId:D}", audience);
            Assert.Equal("Prepared", identity.Status); Assert.Single(await verify.OwnedObjects.AsNoTracking().ToArrayAsync());
            Assert.Single(await verify.ManagedWorkloads.AsNoTracking().ToArrayAsync());
            Assert.Equal(initial ? null : (long?)1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
            Assert.Single(await verify.DurableOperationAttempts.Where(x => x.Generation == scenario.Commit.Generation.Value).AsNoTracking().ToArrayAsync());
        }
    }
    private async Task<string> RestoreRuntimeCatalog(string source)
    {
        var target = await NewDatabaseAsync(); var directory = "/tmp/haby211-" + Guid.NewGuid().ToString("N"); var dump = directory + "/catalog.dump";
        Exception? primary = null;
        try
        {
            await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump,
                "--dbname=" + new Npgsql.NpgsqlConnectionStringBuilder(source).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges",
                "--dbname=" + new Npgsql.NpgsqlConnectionStringBuilder(target).Database, dump);
            return target;
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { await fixture.RunClientUtilityAsync("rm", "-f", dump); await fixture.RunClientUtilityAsync("rmdir", directory); }
            catch when (primary is not null) { Console.Error.WriteLine("Owned runtime dump cleanup failed; primary failure retained."); }
        }
    }
    [Theory]
    [InlineData((int)InternalActionFault.AfterExternalResult)]
    [InlineData((int)InternalActionFault.BeforeCompletionCommit)]
    public async Task Object_confirmation_before_binding_commit_rolls_back_and_resumes_by_observation_only(int boundary)
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e)); var handler = new IdentityFixture();
        await using (var services = InternalServices(scenario.Connection, scenario.Time))
        {
            await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
            await Assert.ThrowsAsync<IOException>(() => IdentityPreparation(services, handler, point => { if ((int)point == boundary) throw new IOException("injected"); }).PrepareAsync(scenario.Commit, default));
        }
        await using (var verify = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(scenario.Connection).Options))
        {
            Assert.Empty(await verify.RuntimeIdentityBindings.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.OwnedObjects.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
        }
        await using (var restarted = InternalServices(scenario.Connection, scenario.Time))
            Assert.Equal(InternalActionOutcome.Confirmed, Assert.Single(await IdentityPreparation(restarted, handler).PrepareAsync(scenario.Commit, default)).Result.Outcome);
        Assert.Equal(1, handler.Writes); Assert.Equal(1, handler.Reads);
        await using var check = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(scenario.Connection).Options);
        Assert.Single(await check.RuntimeIdentityBindings.AsNoTracking().ToArrayAsync()); Assert.Single(await check.OwnedObjects.AsNoTracking().ToArrayAsync());
        Assert.Equal(nameof(DurableAttemptOutcome.Uncertain), (await check.DurableOperationAttempts.AsNoTracking().SingleAsync()).Outcome);
        Assert.True((await check.ActionObservations.AsNoTracking().SingleAsync()).Confirmed);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_replaced_known_account_never_recreates_or_changes_binding(bool replaced)
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e)); var handler = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        var first = Assert.Single(await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default));
        if (replaced) handler.Uid = "replacement-uid"; else handler.Exists = false;
        var refused = Assert.Single(await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default));
        Assert.NotEqual(InternalActionOutcome.Confirmed, refused.Result.Outcome); Assert.Null(refused.Binding); Assert.Equal(1, handler.Writes);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(first.Binding, (await verify.RuntimeIdentityBindings.AsNoTracking().SingleAsync()).Id);
        Assert.Contains("persisted-uid", (await verify.RuntimeIdentityBindings.AsNoTracking().SingleAsync()).ValidationPayload);
    }
    [Fact]
    public async Task Every_selected_artifact_and_recipient_is_required_before_identity_acceptance()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e, secondDocument: true)); var handler = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        await Assert.ThrowsAsync<InvalidDataException>(() => IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default));
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        await using (var corrupt = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            await corrupt.Database.ExecuteSqlRawAsync("UPDATE workload_configuration_recipients SET actor = 'foreign' WHERE document = 'z-last'");
        await Assert.ThrowsAsync<InvalidDataException>(() => IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default));
        Assert.Equal(0, handler.Writes);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verify.ActionIntents.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.RuntimeIdentityBindings.AsNoTracking().ToArrayAsync());
    }
    [Fact]
    public async Task No_configuration_creates_no_runtime_objects()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true); var handler = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        Assert.Empty(await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default));
        Assert.Equal(0, handler.Writes);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verify.RuntimeIdentityBindings.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.OwnedObjects.AsNoTracking().ToArrayAsync());
    }
    [Fact]
    public async Task Installation_first_use_converges_across_independent_transactions()
    {
        var connection = await NewDatabaseAsync();
        await using var services = InternalServices(connection, TimeProvider.System);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var values = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = await contexts.CreateDbContextAsync(); await using var tx = await db.Database.BeginTransactionAsync();
            var value = await InternalDurableActions.InstallationAsync(db, default);
            await db.SaveChangesAsync(); await tx.CommitAsync(); return value;
        }));
        Assert.Single(values.Distinct()); Assert.NotEqual(Guid.Empty, values[0]);
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Equal(values[0], (await verify.InstallationIdentities.AsNoTracking().SingleAsync()).Id);
    }
    [Fact]
    public async Task Old_Workload_intents_refuse_before_migration_and_without_initializer()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true);
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var handler = new InternalDeploymentAction(null!); var actions = Actions(services, handler);
        var key = await actions.AcceptAsync(scenario.Commit, RuntimeSubject, handler.Contract.Action, "old-workload", default);
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        await using (var downgrade = await contexts.CreateDbContextAsync())
            await downgrade.GetService<IMigrator>().MigrateAsync("20261114015351_PrepareWorkloadConfiguration");
        var before = await ReadMigrationHistory(contexts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync());
        Assert.Equal(before, await ReadMigrationHistory(contexts));
        // A current schema with privileged unbinding also refuses instead of backfilling intent identity.
        await using (var schema = await contexts.CreateDbContextAsync()) await schema.Database.MigrateAsync();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await actions.ExecuteAsync(key, default));
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Empty(await verify.ManagedWorkloads.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.RuntimeIdentityBindings.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
    }
    private static async Task<string[]> ReadMigrationHistory(IDbContextFactory<ManifestCatalogDbContext> contexts)
    { await using var db = await contexts.CreateDbContextAsync(); return (await db.Database.GetAppliedMigrationsAsync()).ToArray(); }
    [Fact]
    public async Task P2_only_history_upgrades_additively_without_rewriting_applied_generation()
    {
        var prepared = await PrepareCommitAsync(); await using var services = InternalServices(prepared.Connection, prepared.Time);
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(Envelope)!;
        envelope["manifest"]!["metadata"]!["id"] = "upgrade-" + Guid.NewGuid().ToString("N")[..12];
        envelope["manifest"]!["spec"]!["resources"]!["db"]!["properties"]!["name"] = "haby211_" + Guid.NewGuid().ToString("N");
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), Catalog, prepared.Time).ExecuteAsync(System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString()), "tester"));
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(prepared.Environment, imported.ManifestRevisionRef.ApplicationRecordId, imported.ManifestRevisionRef,
            new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
        var plan = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
        var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, plan.Id, "tester", default)).Commit;
        Assert.Null((await services.GetRequiredService<DurableGenerationRunner>().RunAsync(commit.DeploymentId, commit.Generation, default)).StopOutcome);
        string originalFingerprint;
        await using (var original = await contexts.CreateDbContextAsync()) originalFingerprint = (await original.DeploymentGenerations.AsNoTracking().SingleAsync()).Fingerprint;
        await using (var downgrade = await contexts.CreateDbContextAsync())
            await downgrade.GetService<IMigrator>().MigrateAsync("20261114015351_PrepareWorkloadConfiguration");
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Equal(verify.Database.GetMigrations(), await verify.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Equal(originalFingerprint, (await verify.DeploymentGenerations.AsNoTracking().SingleAsync()).Fingerprint);
        Assert.Single(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.ManagedWorkloads.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.InstallationIdentities.AsNoTracking().ToArrayAsync());
    }
    [Fact]
    public async Task Concurrent_equivalent_preparation_keeps_one_identity_account_and_binding()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e)); var handler = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default)));
        Assert.Single(results.Select(x => Assert.Single(x).Binding).Distinct()); Assert.Equal(1, handler.Writes);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Single(await verify.ManagedWorkloads.AsNoTracking().ToArrayAsync()); Assert.Single(await verify.InstallationIdentities.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.RuntimeIdentityBindings.AsNoTracking().ToArrayAsync());
    }
}
