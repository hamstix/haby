using System.Data.Common;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Haby.Tests.ProfilePersistenceSamples;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    private static readonly ProfileAssignmentTarget ConfigEnvironment = new ProfileAssignmentTarget.Environment();
    private static ProfileDecision ConfigDisable(string name = "irrelevant") => new ProfileDecision.Disable(ProfileId.Create(name));
    private sealed record ConfigSetup(string Connection, AdjustableTimeProvider Time, EnvironmentMetadata Environment,
        ManifestRevisionRef Revision, ProfileRevisionRef Profile);
    private async Task<ConfigSetup> PrepareConfigurationAsync()
    {
        var setup = await PrepareCommitAsync();
        await using var services = Services(setup.Connection, setup.Time);
        var profile = Node(false);
        profile["spec"]!["bindingRules"]!["connection"]!["documentProjections"]!.AsArray().RemoveAt(1);
        var imported = Assert.IsType<ImportConfigurationProfileResult.Succeeded>(await new ImportConfigurationProfile(
            services.GetRequiredService<IConfigurationProfileImportStore>(), setup.Time).ExecuteAsync(Bytes(profile), "author"));
        var node = JsonNode.Parse(Envelope)!;
        node["applicationVersion"] = "2";
        node["manifest"]!["spec"]!["components"] = JsonNode.Parse("""
            {"api":{"bindings":{"consume":{"target":{"kind":"local-resource","resourceRef":"db"},"configurationSlot":"main","options":{"timeout":60}}}},
             "worker":{"bindings":{"consume":{"target":{"kind":"local-resource","resourceRef":"db"},"configurationSlot":"main"}}}}
            """u8);
        var revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), Catalog, setup.Time).ExecuteAsync(Bytes(node), "author"));
        var environment = await services.GetRequiredService<IEnvironmentCatalog>().GetAsync(EnvironmentId.Create("pilot"), default);
        await new PostgreSqlProfileAssignmentStore(new ConfigFactory(setup.Connection)).ApplyAsync(environment!,
            new(ConfigEnvironment, [new ProfileDecision.Use(imported.Revision.Reference)]), new AssignmentPrecondition.ExpectedAbsence(),
            new("operator", Time, null), default);
        return new(setup.Connection, setup.Time, environment!, revision.ManifestRevisionRef, imported.Revision.Reference);
    }
    private static async Task<DurablePlan> ConfigurationPlan(ConfigSetup setup, IServiceProvider services)
    {
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(setup.Environment.RecordId, setup.Revision.ApplicationRecordId, setup.Revision,
            new DeploymentPrecondition.ExpectedAbsence(), "operator", default)).ChangeSet!;
        var result = await planner.CreatePlanAsync(change.Id, "operator", default);
        Assert.Equal(DurablePlanStatus.Planned, result.Status);
        return result.Plan!;
    }
    private static async Task<AssignmentResult> ReplaceConfiguration(ConfigSetup setup, ProfileAssignmentTarget target,
        params ProfileDecision[] decisions)
    {
        var store = new PostgreSqlProfileAssignmentStore(new ConfigFactory(setup.Connection));
        var current = await store.FindAsync(setup.Environment, target, default);
        return await store.ApplyAsync(setup.Environment, new(target, decisions), current is null
            ? new AssignmentPrecondition.ExpectedAbsence() : new AssignmentPrecondition.ExpectedConcurrencyToken(current.Token),
            new("writer", Time, target is ProfileAssignmentTarget.Component ? setup.Revision : null), default);
    }

    [Fact]
    public async Task Relevant_tokens_and_absence_stale_but_saved_reads_and_unrelated_scopes_remain_frozen()
    {
        var setup = await PrepareConfigurationAsync();
        await using var services = Services(setup.Connection, setup.Time);
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var plan = await ConfigurationPlan(setup, services);
        var frozen = plan.Inputs.Configuration!;
        Assert.Equal(4, frozen.Scopes.Count);
        Assert.All(frozen.Layouts, x => Assert.Single(x.Documents));
        Assert.Equal(AssignmentOutcome.Unchanged, (await ReplaceConfiguration(setup, ConfigEnvironment, new ProfileDecision.Use(setup.Profile))).Outcome);
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(plan.Id, default));
        foreach (var target in new ProfileAssignmentTarget[]
        {
            new ProfileAssignmentTarget.Application(Haby.Domain.ApplicationId.Create("other")),
            new ProfileAssignmentTarget.Component(frozen.Manifest.ApplicationId, ManifestLocalId.Create("absent"))
        }) await ReplaceConfiguration(setup, target, ConfigDisable());
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(plan.Id, default));
        var otherEnvironment = (await services.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("other"), default)).Environment;
        await new PostgreSqlProfileAssignmentStore(new ConfigFactory(setup.Connection)).ApplyAsync(otherEnvironment,
            new(ConfigEnvironment, [ConfigDisable()]), new AssignmentPrecondition.ExpectedAbsence(), new("other", Time, null), default);
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(plan.Id, default));
        // Explicit empty and absence inherit equally, but are different exact inputs.
        var app = new ProfileAssignmentTarget.Application(frozen.Manifest.ApplicationId);
        await ReplaceConfiguration(setup, app);
        Assert.Equal(DurablePlanCurrentStatus.PlanStale, await planner.CheckPlanAsync(plan.Id, default));
        Assert.Equal(frozen, (await planner.FindPlanAsync(plan.Id, default))!.Inputs.Configuration);
        var second = (await planner.CreatePlanAsync(plan.ChangeSetId, "operator", default)).Plan!;
        Assert.NotEqual(plan.Fingerprint, second.Fingerprint);
        Assert.NotNull(second.Inputs.Configuration!.Scopes[1].Token);
        Assert.IsType<DurableCommitResult.PlanStale>(await services.GetRequiredService<IDurableDesiredStateCommitter>()
            .CommitAsync(plan.ChangeSetId, plan.Id, "operator", default));
        await ReplaceConfiguration(setup, app, ConfigDisable());
        await ReplaceConfiguration(setup, app);
        Assert.Equal(DurablePlanCurrentStatus.PlanStale, await planner.CheckPlanAsync(second.Id, default));
        var current = (await planner.CreatePlanAsync(plan.ChangeSetId, "operator", default)).Plan!;
        Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>()
            .CommitAsync(current.ChangeSetId, current.Id, "operator", default));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Scope_writes_and_commit_have_a_synchronized_atomic_order_including_absent_insertion(bool existing, bool commitFirst)
    {
        var setup = await PrepareConfigurationAsync();
        var app = new ProfileAssignmentTarget.Application(Haby.Domain.ApplicationId.Create("operator-example"));
        if (existing) await ReplaceConfiguration(setup, app);
        await using var services = Services(setup.Connection, setup.Time);
        var plan = await ConfigurationPlan(setup, services);
        var current = await new PostgreSqlProfileAssignmentStore(new ConfigFactory(setup.Connection)).FindAsync(setup.Environment, app, default);
        var save = new ConfigSavePause(); var signal = new ConfigLockSignal(commitFirst ? "pg_advisory_xact_lock(" : "pg_advisory_xact_lock_shared(");
        var factory = new ConfigFactory(setup.Connection, save);
        var contenderFactory = new ConfigFactory(setup.Connection, signal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var committer = new PostgreSqlDurableDesiredStateCommitter(commitFirst ? factory : contenderFactory,
            services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>(), setup.Time);
        var writer = new PostgreSqlProfileAssignmentStore(commitFirst ? contenderFactory : factory);
        var precondition = current is null ? (AssignmentPrecondition)new AssignmentPrecondition.ExpectedAbsence()
            : new AssignmentPrecondition.ExpectedConcurrencyToken(current.Token);
        Task<DurableCommitResult> commit;
        Task<AssignmentResult> write;
        if (commitFirst)
        {
            commit = committer.CommitAsync(plan.ChangeSetId, plan.Id, "commit", timeout.Token).AsTask();
            await save.Entered.Task.WaitAsync(timeout.Token);
            write = writer.ApplyAsync(setup.Environment, new(app, [ConfigDisable()]), precondition, new("writer", Time, null), timeout.Token).AsTask();
        }
        else
        {
            write = writer.ApplyAsync(setup.Environment, new(app, [ConfigDisable()]), precondition, new("writer", Time, null), timeout.Token).AsTask();
            await save.Entered.Task.WaitAsync(timeout.Token);
            commit = committer.CommitAsync(plan.ChangeSetId, plan.Id, "commit", timeout.Token).AsTask();
        }
        try
        {
            await signal.Entered.Task.WaitAsync(timeout.Token);
            await ObserveConfigurationLockWait(setup.Connection, save.BackendPid, signal.BackendPid,
                commitFirst ? write : commit, commitFirst ? "ExclusiveLock" : "ShareLock", timeout.Token);
        }
        finally { save.Resume.TrySetResult(); await Task.WhenAll(commit, write); }
        Assert.Equal(existing ? AssignmentOutcome.Updated : AssignmentOutcome.Created, (await write).Outcome);
        var result = await commit;
        if (commitFirst)
        {
            var accepted = Assert.IsType<DurableCommitResult.Committed>(result).Commit;
            var generation = (await committer.FindGenerationAsync(accepted.DeploymentId, accepted.Generation, default))!;
            Assert.Equal(plan.Inputs, generation.Inputs);
            Assert.Equal(plan.Inputs.Configuration, generation.Inputs.Configuration);
            Assert.Equal(accepted, Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(plan.ChangeSetId, plan.Id, "retry", default)).Commit);
        }
        else Assert.IsType<DurableCommitResult.PlanStale>(result);
        await using var verify = new ConfigFactory(setup.Connection).CreateDbContext();
        Assert.Equal(commitFirst ? 1 : 0, await verify.DeploymentGenerations.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Multi_scope_capture_blocks_a_writer_between_scope_reads_and_saves_one_coherent_view()
    {
        var setup = await PrepareConfigurationAsync();
        var save = new ConfigReadPause();
        await using var services = Services(setup.Connection, setup.Time, commandInterceptor: save);
        // Pause after the Environment scope query and before the Application scope read.
        DeploymentChangeSet change;
        await using (var arrange = Services(setup.Connection, setup.Time))
            change = (await arrange.GetRequiredService<IDurableDeploymentPlanner>().CreateChangeSetAsync(setup.Environment.RecordId,
                setup.Revision.ApplicationRecordId, setup.Revision, new DeploymentPrecondition.ExpectedAbsence(), "operator", default)).ChangeSet!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var pending = services.GetRequiredService<IDurableDeploymentPlanner>().CreatePlanAsync(change.Id, "operator", timeout.Token).AsTask();
        await save.Entered.Task.WaitAsync(timeout.Token);
        var signal = new ConfigLockSignal("pg_advisory_xact_lock(");
        var writer = new PostgreSqlProfileAssignmentStore(new ConfigFactory(setup.Connection, signal));
        var app = new ProfileAssignmentTarget.Application(Haby.Domain.ApplicationId.Create("operator-example"));
        var write = writer.ApplyAsync(setup.Environment, new(app, [ConfigDisable()]), new AssignmentPrecondition.ExpectedAbsence(),
            new("writer", Time, null), timeout.Token).AsTask();
        try
        {
            await signal.Entered.Task.WaitAsync(timeout.Token);
            await ObserveConfigurationLockWait(setup.Connection, save.BackendPid, signal.BackendPid,
                write, "ExclusiveLock", timeout.Token);
        }
        finally { save.Resume.TrySetResult(); await Task.WhenAll(pending, write); }
        var plan = (await pending).Plan!;
        await write;
        Assert.Null(plan.Inputs.Configuration!.Scopes[1].Token);
        Assert.All(plan.Inputs.Configuration.Layouts, x => Assert.Single(x.Documents));
        Assert.Equal(DurablePlanCurrentStatus.PlanStale, await services.GetRequiredService<IDurableDeploymentPlanner>().CheckPlanAsync(plan.Id, default));
    }

    [Fact]
    public async Task Restart_and_real_dump_restore_retain_full_plan_generation_fingerprints_and_audit_after_assignment_changes()
    {
        var setup = await PrepareConfigurationAsync();
        DurablePlan plan; DurableChangeSetCommit committed;
        await using (var services = Services(setup.Connection, setup.Time))
        {
            plan = await ConfigurationPlan(setup, services);
            committed = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(plan.ChangeSetId, plan.Id, "acceptor", default)).Commit;
        }
        await ReplaceConfiguration(setup, ConfigEnvironment, ConfigDisable());
        var restored = await NewDatabaseAsync();
        var directory = "/tmp/haby-config-" + Guid.NewGuid().ToString("N");
        var dump = directory + "/catalog.dump";
        Exception? failure = null;
        try
        {
            await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump,
                "--dbname=" + new NpgsqlConnectionStringBuilder(setup.Connection).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges",
                "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
            foreach (var connection in new[] { setup.Connection, restored })
            {
                await using var fresh = Services(connection, setup.Time);
                await fresh.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
                Assert.Equal(plan, await fresh.GetRequiredService<IDurableDeploymentPlanner>().FindPlanAsync(plan.Id, default));
                var committer = fresh.GetRequiredService<IDurableDesiredStateCommitter>();
                var generation = (await committer.FindGenerationAsync(committed.DeploymentId, committed.Generation, default))!;
                Assert.Equal(plan.Inputs, generation.Inputs); Assert.Equal(plan.Graph, generation.Graph);
                Assert.Equal(committed.CommittedAt, generation.AcceptedAt); Assert.Equal("acceptor", generation.ActorId);
                Assert.Equal(committed, await committer.FindCommitAsync(plan.ChangeSetId, default));
                Assert.Equal(committed, Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(plan.ChangeSetId, plan.Id, "retry", default)).Commit);
                await using var check = new ConfigFactory(connection).CreateDbContext();
                Assert.False(check.Database.HasPendingModelChanges());
                Assert.Equal(plan.Fingerprint, (await check.DeploymentGenerations.AsNoTracking().SingleAsync()).Fingerprint);
            }
        }
        catch (Exception e) { failure = e; throw; }
        finally { try { await fixture.RunClientUtilityAsync("rm", "-f", dump); await fixture.RunClientUtilityAsync("rmdir", directory); } catch when (failure is not null) { } }
    }

    [Theory]
    [InlineData("missing-slot", "profile.slot.not-found")]
    [InlineData("ambiguous-slot", "profile.slot.ambiguous")]
    [InlineData("missing-document", "profile.document.not-found")]
    [InlineData("duplicate-document", "profile.document.duplicate")]
    [InlineData("file-conflict", "profile.document.file-conflict")]
    [InlineData("path-conflict", "render.projection.path-conflict")]
    [InlineData("template-collision", "render.projection.template-collision")]
    [InlineData("secret", "profile.secret-projections.unsupported")]
    public async Task Persisted_expansion_gates_keep_original_codes_and_accept_no_partial_plan(string mode, string code)
    {
        var setup = await PrepareConfigurationAsync();
        var node = Node(mode == "secret"); node["metadata"]!["revision"] = 2;
        var rule = node["spec"]!["bindingRules"]!["connection"]!;
        if (mode != "missing-document") rule["documentProjections"]!.AsArray().RemoveAt(1);
        if (mode == "missing-slot") node["spec"]!["bindingRules"] = new JsonObject();
        if (mode == "ambiguous-slot") node["spec"]!["bindingRules"]!["duplicate"] = rule.DeepClone();
        if (mode == "duplicate-document" || mode == "file-conflict")
        {
            var other = node.DeepClone(); other["metadata"]!["id"] = "other";
            other["spec"]!["staticContributions"] = new JsonObject(); other["spec"]!["bindingRules"] = new JsonObject();
            if (mode == "file-conflict")
            {
                other["spec"]!["documents"]!["different"] = other["spec"]!["documents"]!["settings"]!.DeepClone();
                other["spec"]!["documents"]!.AsObject().Remove("settings");
            }
            await using var importOther = Services(setup.Connection, setup.Time);
            await new ImportConfigurationProfile(importOther.GetRequiredService<IConfigurationProfileImportStore>(), setup.Time).ExecuteAsync(Bytes(other), "author");
        }
        if (mode == "path-conflict") node["spec"]!["staticContributions"]!["logging"]!["path"] = "/Connections";
        if (mode == "template-collision") node["spec"]!["documents"]!["settings"]!["template"]!["Connections"] = new JsonObject { ["Main"] = "private-template" };
        await using var services = Services(setup.Connection, setup.Time);
        var imported = Assert.IsType<ImportConfigurationProfileResult.Succeeded>(await new ImportConfigurationProfile(
            services.GetRequiredService<IConfigurationProfileImportStore>(), setup.Time).ExecuteAsync(Bytes(node), "author"));
        var choices = new List<ProfileDecision> { new ProfileDecision.Use(imported.Revision.Reference) };
        if (mode is "duplicate-document" or "file-conflict") choices.Add(new ProfileDecision.Use(new(ProfileId.Create("other"), 2)));
        await ReplaceConfiguration(setup, ConfigEnvironment, choices.ToArray());
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(setup.Environment.RecordId, setup.Revision.ApplicationRecordId, setup.Revision,
            new DeploymentPrecondition.ExpectedAbsence(), "operator", default)).ChangeSet!;
        var result = await planner.CreatePlanAsync(change.Id, "operator", default);
        Assert.Equal(DurablePlanStatus.ConfigurationRefused, result.Status); Assert.Null(result.Plan);
        Assert.Null(result.ConfigurationReport!.Layouts);
        Assert.Contains(result.ConfigurationReport.Diagnostics, x => x.Code == code);
        await using var verify = new ConfigFactory(setup.Connection).CreateDbContext();
        Assert.Empty(await verify.DurablePlans.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.DurableOperations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Corrupt_saved_content_and_unsupported_formats_refuse_instead_of_using_current_assignments()
    {
        var setup = await PrepareConfigurationAsync();
        await using var services = Services(setup.Connection, setup.Time);
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var plan = await ConfigurationPlan(setup, services);
        await using var corrupt = new NpgsqlConnection(setup.Connection);
        await corrupt.OpenAsync();
        await new NpgsqlCommand("SET session_replication_role = replica", corrupt).ExecuteNonQueryAsync();
        foreach (var json in new[] { "", "{}", ConfigurationPlanningSnapshotCodec.Encode(plan.Inputs.Configuration!).Replace("\"format\":1", "\"format\":999", StringComparison.Ordinal) })
        {
            await using var command = new NpgsqlCommand("UPDATE deployment_plans SET configuration_json = @json WHERE id = @id", corrupt);
            command.Parameters.AddWithValue("json", json); command.Parameters.AddWithValue("id", Guid.Parse(plan.Id.ToString()));
            await command.ExecuteNonQueryAsync();
            Assert.Equal(DurablePlanCurrentStatus.PlanInputUnavailable, await planner.CheckPlanAsync(plan.Id, default));
            Assert.IsType<DurableCommitResult.PlanInputUnavailable>(await services.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(plan.ChangeSetId, plan.Id, "operator", default));
        }
        await using var check = new ConfigFactory(setup.Connection).CreateDbContext();
        Assert.Empty(await check.DeploymentGenerations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Prior_durable_catalog_requires_explicit_reinitialization_without_backfill_or_automatic_reset()
    {
        var setup = await PrepareConfigurationAsync();
        await using (var services = Services(setup.Connection, setup.Time)) await ConfigurationPlan(setup, services);
        await using (var arrange = new ConfigFactory(setup.Connection).CreateDbContext())
            await arrange.Database.ExecuteSqlRawAsync("""
                ALTER TABLE deployment_plans DROP COLUMN configuration_json;
                ALTER TABLE application_deployment_generations DROP COLUMN configuration_json;
                DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" LIKE '%FreezeConfigurationPlanning';
                """);
        await using (var act = Services(setup.Connection, setup.Time))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => act.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync());
            Assert.Contains("fresh catalog", error.Message);
        }
        await using var check = new NpgsqlConnection(setup.Connection); await check.OpenAsync();
        Assert.Equal(1L, await new NpgsqlCommand("SELECT count(*) FROM deployment_plans", check).ExecuteScalarAsync());
        Assert.Equal(19L, await new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\"", check).ExecuteScalarAsync());
    }

    private static async Task ObserveConfigurationLockWait(string connection, int owner, int contender,
        Task operation, string mode, CancellationToken token)
    {
        // An independent backend observes the actual PostgreSQL wait, not the interceptor's
        // pre-execution notification. The matching granted lock must belong to the paused owner.
        await using var observer = new NpgsqlConnection(connection);
        await observer.OpenAsync(token);
        Assert.NotEqual(owner, contender);
        Assert.NotEqual(owner, observer.ProcessID);
        Assert.NotEqual(contender, observer.ProcessID);
        await using var query = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_locks AS waiting
                JOIN pg_locks AS held ON held.locktype = waiting.locktype
                    AND held.database IS NOT DISTINCT FROM waiting.database
                    AND held.classid = waiting.classid AND held.objid = waiting.objid
                    AND held.objsubid = waiting.objsubid
                WHERE waiting.locktype = 'advisory' AND waiting.pid = @contender
                    AND NOT waiting.granted AND waiting.mode = @mode
                    AND held.pid = @owner AND held.granted
                    AND @owner = ANY(pg_blocking_pids(@contender)))
            """, observer);
        query.Parameters.AddWithValue("owner", owner);
        query.Parameters.AddWithValue("contender", contender);
        query.Parameters.AddWithValue("mode", mode);
        while (true)
        {
            var observation = query.ExecuteScalarAsync(token);
            var first = await Task.WhenAny(observation, operation);
            if (first == operation)
            {
                // Drain the observer command before disposal, preserving the useful assertion.
                await observation;
                Assert.Fail("The competing operation completed before PostgreSQL reported it blocked by the paused owner.");
            }
            if ((bool)(await observation)!) return;
            token.ThrowIfCancellationRequested();
        }
    }

    private sealed class ConfigFactory(string connection, params IInterceptor[] interceptors) : IDbContextFactory<ManifestCatalogDbContext>
    {
        public ManifestCatalogDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>()
            .UseNpgsql(connection).AddInterceptors(interceptors).Options);
    }
    private sealed class ConfigSavePause : SaveChangesInterceptor
    {
        internal int BackendPid { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            BackendPid = ((NpgsqlConnection)eventData.Context!.Database.GetDbConnection()).ProcessID;
            Entered.TrySetResult(); await Resume.Task.WaitAsync(cancellationToken); return result;
        }
    }
    private sealed class ConfigReadPause : DbCommandInterceptor
    {
        internal int BackendPid { get; private set; }
        private int _entered;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("configuration_profile_assignments", StringComparison.Ordinal) && Interlocked.Exchange(ref _entered, 1) == 0)
            {
                BackendPid = ((NpgsqlConnection)command.Connection!).ProcessID;
                Entered.TrySetResult(); await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
    private sealed class ConfigLockSignal(string commandText) : DbCommandInterceptor
    {
        internal int BackendPid { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(commandText, StringComparison.Ordinal))
            {
                BackendPid = ((NpgsqlConnection)command.Connection!).ProcessID;
                Entered.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }
}
