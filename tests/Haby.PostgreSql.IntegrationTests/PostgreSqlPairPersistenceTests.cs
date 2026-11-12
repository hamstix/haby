using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class PostgreSqlPairPersistenceTests(PostgreSqlFixture fixture, ITestOutputHelper output) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Fixture_servers_are_isolated_and_the_no_effects_oracle_remains_global()
    {
        var setup = await Prepare();
        var role = "haby_a_isolation_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        var originalCount = "SELECT count(*) FROM pg_roles WHERE rolname LIKE 'haby_a_%'";
        Assert.Equal(0L, (long)(await new NpgsqlCommand(originalCount, admin).ExecuteScalarAsync())!);
        var identityA = (string)(await new NpgsqlCommand("SELECT system_identifier::text FROM pg_control_system()", admin).ExecuteScalarAsync())!;
        await new NpgsqlCommand($"CREATE ROLE \"{role}\" NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS", admin).ExecuteNonQueryAsync();
        var oid = (uint)(await new NpgsqlCommand($"SELECT oid FROM pg_roles WHERE rolname = '{role}'", admin).ExecuteScalarAsync())!;
        var other = new PostgreSqlFixture();
        try
        {
            Assert.Equal(1L, (long)(await new NpgsqlCommand(originalCount, admin).ExecuteScalarAsync())!);
            Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = 'sample_app'", admin).ExecuteScalarAsync())!);
            await Assert.ThrowsAsync<EqualException>(() => AssertNoEffects(setup.Connection, setup.Policy));
            Assert.Equal(setup.Policy, await ExternalPolicy());
            await other.InitializeAsync();
            await using var independent = new NpgsqlConnection(other.ConnectionString);
            await independent.OpenAsync();
            var identityB = (string)(await new NpgsqlCommand("SELECT system_identifier::text FROM pg_control_system()", independent).ExecuteScalarAsync())!;
            var countB = (long)(await new NpgsqlCommand(originalCount, independent).ExecuteScalarAsync())!;
            output.WriteLine($"FIXTURE_ISOLATION A={identityA} B={identityB} roleCountA=1 roleCountB={countB} strictOracleRejectedA=true");
            Assert.NotEqual(identityA, identityB);
            Assert.Equal(0L, countB);
        }
        finally
        {
            try { await other.DisposeAsync(); }
            finally
            {
                await using var ownership = new NpgsqlCommand($"SELECT count(*) FROM pg_roles WHERE oid = {oid} AND rolname = '{role}' AND NOT rolcanlogin", admin);
                Assert.Equal(1L, (long)(await ownership.ExecuteScalarAsync())!);
                await new NpgsqlCommand($"DROP ROLE \"{role}\"", admin).ExecuteNonQueryAsync();
            }
        }
        Assert.Equal(0L, (long)(await new NpgsqlCommand(originalCount, admin).ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Persisted_pair_refuses_conflicting_same_alias_entries_before_read_check_or_commit()
    {
        var setup = await Prepare();
        Assert.Equal(2, setup.Plan.Inputs.Providers.Entries.Count);
        await using (var arrange = new NpgsqlConnection(setup.Connection))
        {
            await arrange.OpenAsync();
            // Canonical set order is app-login then db. Corrupt only the second
            // exact resolution; checking a singleton projection would miss this.
            await new NpgsqlCommand("""
                SET session_replication_role = replica;
                UPDATE deployment_plans SET provider_inputs_json =
                    jsonb_set(provider_inputs_json::jsonb, '{Entries,1,Configuration}', '2'::jsonb)::text;
                """, arrange).ExecuteNonQueryAsync();
        }
        await using (var act = Services(setup.Connection))
        {
            var planner = act.GetRequiredService<IDurableDeploymentPlanner>();
            await Assert.ThrowsAsync<InvalidDataException>(async () => await planner.FindPlanAsync(setup.Plan.Id, default));
            Assert.Equal(DurablePlanCurrentStatus.PlanInputUnavailable, await planner.CheckPlanAsync(setup.Plan.Id, default));
            Assert.IsType<DurableCommitResult.PlanInputUnavailable>(await act.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(setup.Change.Id, setup.Plan.Id, "tester", default));
        }
        await AssertNoEffects(setup.Connection, setup.Policy);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 2, 16, 0, 0, TimeSpan.Zero);
    }
    private static ServiceProvider Services(string connection, bool pair = true, bool restrict = false)
    {
        var services = new ServiceCollection().AddSingleton<TimeProvider>(new Clock())
            .AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore()
            .AddPostgreSqlProviderConfigurationStore().AddPostgreSqlProviderAliasStore()
            .AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
            .AddPostgreSqlProviderConfigurationCodec().AddPostgreSqlProviderRevisionPreflight()
            .AddPostgreSqlDurableDesiredStateCommitter().AddPostgreSqlDurableOperationJournal()
            .AddPostgreSqlDurableOperationExecution();
        if (restrict) services.AddPostgreSqlDatabaseRestrict();
        if (pair)
        {
            services.RemoveAll<IDurableActionAdmissionHandler>();
            services.AddSingleton<IDurableActionAdmissionHandler>(new PostgreSqlPairResourceAdmission(PostgreSqlDatabaseContract.Id));
            services.AddSingleton<IDurableActionAdmissionHandler>(new PostgreSqlPairResourceAdmission(PostgreSqlApplicationLoginContract.Id));
        }
        return services.BuildServiceProvider();
    }
    private static ManifestCatalogDbContext Context(string connection) => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
    private async Task<string> Database(string prefix)
    {
        var name = prefix + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(fixture.ConnectionString); await admin.OpenAsync();
        await new NpgsqlCommand($"""CREATE DATABASE "{name}" """, admin).ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }
    private async Task<(string Connection, DurablePlan Plan, DeploymentChangeSet Change, string Policy)> Prepare()
    {
        var connection = await Database("pair_");
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = services.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        var target = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var bytes = Encoding.UTF8.GetBytes(target.Password!);
        try { await secrets.CreateAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator), secret, bytes, default); }
        finally { Array.Clear(bytes); }
        var config = new PostgreSqlProviderConfiguration(1, target.Host!, target.Port, target.Database!, target.Username!, secret,
            PostgreSqlDatabaseCheckpointCapability.ImplementationId);
        var providers = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await providers.CreateInstanceAsync(environment, config, default);
        await providers.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), config, default);
        Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment,
            ProviderAlias.Create("postgresql.primary"), null, new(instance, ProviderConfigurationRevisionNumber.Create(1)), "tester", default)).Outcome);
        var policy = await ExternalPolicy();
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), PostgreSqlPairPlanningTests.Catalog, TimeProvider.System)
            .ExecuteAsync(PostgreSqlPairPlanningTests.Bytes(PostgreSqlPairPlanningTests.Example()), "tester"));
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(environment, imported.ManifestRevisionRef.ApplicationRecordId,
            imported.ManifestRevisionRef, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
        var result = await planner.CreatePlanAsync(change.Id, "tester", default);
        Assert.Equal(DurablePlanStatus.Planned, result.Status);
        return (connection, result.Plan!, change, policy);
    }

    private async Task<string> ExternalPolicy()
    {
        await using var external = new NpgsqlConnection(fixture.ConnectionString); await external.OpenAsync();
        // Read only non-confidential ownership/ACL evidence for the untouched maintenance database.
        await using var command = new NpgsqlCommand("""
            SELECT json_build_array(d.datdba, d.datacl, n.nspowner, n.nspacl)::text
            FROM pg_database d CROSS JOIN pg_namespace n
            WHERE d.datname = current_database() AND n.nspname = 'public'
            """, external);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task AssertNoEffects(string connection, string policy)
    {
        await using var context = Context(connection);
        Assert.Empty(await context.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.GeneratedCredentialCreationAudits.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
        Assert.DoesNotContain(await context.ApplicationDeployments.AsNoTracking().ToArrayAsync(), d => d.AppliedGeneration is not null);
        // Read independent external catalogs, not a test handler's counter.
        await using var external = new NpgsqlConnection(fixture.ConnectionString); await external.OpenAsync();
        Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = 'sample_app'", external).ExecuteScalarAsync())!);
        Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM pg_roles WHERE rolname LIKE 'haby_a_%'", external).ExecuteScalarAsync())!);
        Assert.Equal(policy, await ExternalPolicy());
    }

    [Fact]
    public async Task Planning_only_pair_survives_commit_restart_restore_and_production_refuses_partial_execution()
    {
        var setup = await Prepare(); var plan = setup.Plan;
        PostgreSqlPairPlanningTests.AssertGraph(plan.Graph);
        await AssertNoEffects(setup.Connection, setup.Policy);
        await using (var production = Services(setup.Connection, pair: false))
        {
            var planner = production.GetRequiredService<IDurableDeploymentPlanner>();
            Assert.NotEqual(DurablePlanStatus.Planned, (await planner.CreatePlanAsync(setup.Change.Id, "tester", default)).Status);
            Assert.Equal(DurablePlanCurrentStatus.PlanInputUnavailable, await planner.CheckPlanAsync(plan.Id, default));
            Assert.IsNotType<DurableCommitResult.Committed>(await production.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(setup.Change.Id, plan.Id, "tester", default));
        }
        DurableChangeSetCommit commit;
        await using (var restart = Services(setup.Connection))
        {
            var planner = restart.GetRequiredService<IDurableDeploymentPlanner>();
            Assert.Equal(plan, await planner.FindPlanAsync(plan.Id, default));
            Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(plan.Id, default));
            commit = Assert.IsType<DurableCommitResult.Committed>(await restart.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(setup.Change.Id, plan.Id, "tester", default)).Commit;
            var providers = restart.GetRequiredService<IProviderConfigurationStore>();
            var pinned = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(plan.Inputs.Environment,
                plan.Inputs.Providers.RequireUniformResourceProvider().Instance, plan.Inputs.Providers.RequireUniformResourceProvider().Configuration, default))!;
            var next = ProviderConfigurationRevisionNumber.Create(2);
            await providers.AddRevisionAsync(plan.Inputs.Environment, plan.Inputs.Providers.RequireUniformResourceProvider().Instance, next, pinned.Configuration, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await restart.GetRequiredService<IProviderAliasStore>().ChangeAsync(
                plan.Inputs.Environment, plan.Inputs.Providers.RequireUniformResourceProvider().Alias, new(plan.Inputs.Providers.RequireUniformResourceProvider().Instance, plan.Inputs.Providers.RequireUniformResourceProvider().Configuration),
                new(plan.Inputs.Providers.RequireUniformResourceProvider().Instance, next), "tester", default)).Outcome);
            var saved = (await restart.GetRequiredService<IDurableDesiredStateCommitter>().FindGenerationAsync(commit.DeploymentId, commit.Generation, default))!;
            Assert.Equal(plan.Inputs, saved.Inputs);
            Assert.Equal(["app-login", "db"], saved.Inputs.Providers.Entries.Keys.OfType<ProviderInput.Resource>().Select(x => x.Id.Value).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(saved.Inputs.Providers.Resource(ManifestLocalId.Create("db")).Instance,
                saved.Inputs.Providers.Resource(ManifestLocalId.Create("app-login")).Instance);
            Assert.Equal(plan.Graph, saved.Graph);
        }
        await using (var production = Services(setup.Connection, pair: false))
        {
            var key = new DurableOperationKey(commit.DeploymentId, commit.Generation, OperationId.Create("db-create"));
            Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await production.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(key, default)).Outcome);
            Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await production.GetRequiredService<PostgreSqlDurableOperationExecutor>().ExecuteAsync(key, default)).Outcome);
            Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await production.GetRequiredService<DurableGenerationRunner>().RunAsync(commit.DeploymentId, commit.Generation, default)).StopOutcome);
        }
        await AssertNoEffects(setup.Connection, setup.Policy);
        var restoredConnection = await Database("pair_restore_");
        var dump = "/tmp/pair-" + Guid.NewGuid().ToString("N") + ".dump";
        try
        {
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + new NpgsqlConnectionStringBuilder(setup.Connection).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + new NpgsqlConnectionStringBuilder(restoredConnection).Database, dump);
            await using var restored = Services(restoredConnection);
            await restored.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var generation = (await restored.GetRequiredService<IDurableDesiredStateCommitter>().FindGenerationAsync(commit.DeploymentId, commit.Generation, default))!;
            Assert.Equal(plan.Graph, generation.Graph); Assert.Equal(plan.Inputs, generation.Inputs);
            await using var verify = Context(restoredConnection);
            Assert.Equal(plan.Fingerprint, (await verify.DeploymentGenerations.AsNoTracking().SingleAsync()).Fingerprint);
            Assert.Equal(5, await verify.DurableOperations.CountAsync());
            Assert.All(await verify.DurableOperations.AsNoTracking().ToArrayAsync(), n => Assert.Equal("Pending", n.State));
            await AssertNoEffects(restoredConnection, setup.Policy);
        }
        finally { await fixture.RunClientUtilityAsync("rm", "-f", dump); }
    }

    [Theory]
    [InlineData("producer")]
    [InlineData("key")]
    [InlineData("contract")]
    [InlineData("action")]
    [InlineData("input")]
    [InlineData("owner")]
    [InlineData("dependency")]
    public async Task Check_and_atomic_commit_reject_tampered_graph_even_with_valid_fingerprint(string mode)
    {
        var setup = await Prepare();
        var graph = PostgreSqlPairPlanningTests.Tamper(setup.Plan.Graph, mode);
        // Privileged maintenance fixture bypasses append-only guards, simulating corrupt retained content.
        await using (var database = new NpgsqlConnection(setup.Connection))
        {
            await database.OpenAsync();
            await new NpgsqlCommand("SET session_replication_role = replica", database).ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand("UPDATE deployment_plans SET graph_json = @graph, fingerprint = @fingerprint WHERE id = @id", database);
            command.Parameters.AddWithValue("graph", DurableOperationGraphCodec.Encode(graph));
            command.Parameters.AddWithValue("fingerprint", DurableOperationGraphCodec.Fingerprint(setup.Plan.Inputs, graph));
            command.Parameters.AddWithValue("id", Guid.Parse(setup.Plan.Id.ToString())); await command.ExecuteNonQueryAsync();
        }
        await using (var act = Services(setup.Connection))
        {
            Assert.Equal(DurablePlanCurrentStatus.PlanInputUnavailable, await act.GetRequiredService<IDurableDeploymentPlanner>().CheckPlanAsync(setup.Plan.Id, default));
            Assert.IsType<DurableCommitResult.PlanInputUnavailable>(await act.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(setup.Change.Id, setup.Plan.Id, "tester", default));
        }
        await using (var verify = Context(setup.Connection))
        {
            Assert.Empty(await verify.ApplicationDeployments.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.ChangeSetCommits.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.DurableOperations.AsNoTracking().ToArrayAsync());
        }
        await AssertNoEffects(setup.Connection, setup.Policy);
    }

    [Fact]
    public async Task Opt_in_restrict_does_not_allow_partial_five_node_pair_execution()
    {
        var setup = await Prepare();
        DurableChangeSetCommit commit;
        await using (var accept = Services(setup.Connection))
            commit = Assert.IsType<DurableCommitResult.Committed>(await accept.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(setup.Change.Id, setup.Plan.Id, "tester", default)).Commit;
        await using (var act = Services(setup.Connection, restrict: true))
        {
            var key = new DurableOperationKey(commit.DeploymentId, commit.Generation, OperationId.Create("db-create"));
            Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(key, default)).Outcome);
            Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await act.GetRequiredService<PostgreSqlDurableOperationExecutor>().ExecuteAsync(key, default)).Outcome);
            Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await act.GetRequiredService<DurableGenerationRunner>().RunAsync(commit.DeploymentId, commit.Generation, default)).StopOutcome);
        }
        await AssertNoEffects(setup.Connection, setup.Policy);
    }

    [Fact]
    public async Task Plan_Check_and_atomic_Commit_reject_competing_database_owner_requests_without_effects()
    {
        var setup = await Prepare();
        await using (var arrange = new NpgsqlConnection(setup.Connection))
        {
            await arrange.OpenAsync();
            await new NpgsqlCommand("""
                SET session_replication_role = replica;
                UPDATE manifest_revisions SET snapshot = jsonb_set(snapshot::jsonb, '{spec,resources,other-login}',
                    snapshot::jsonb #> '{spec,resources,app-login}')::text
                """, arrange).ExecuteNonQueryAsync();
        }
        await using (var act = Services(setup.Connection))
        {
            var planner = act.GetRequiredService<IDurableDeploymentPlanner>();
            Assert.Equal(DurablePlanStatus.UnsupportedShape, (await planner.CreatePlanAsync(setup.Change.Id, "tester", default)).Status);
            Assert.Equal(DurablePlanCurrentStatus.PlanInputUnavailable, await planner.CheckPlanAsync(setup.Plan.Id, default));
            Assert.IsType<DurableCommitResult.UnsupportedShape>(await act.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(setup.Change.Id, setup.Plan.Id, "tester", default));
        }
        await AssertNoEffects(setup.Connection, setup.Policy);
    }

    [Fact]
    public async Task Actual_Host_registers_login_metadata_and_complete_production_composition()
    {
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false"]);
        var catalog = host.Services.GetRequiredService<ManifestContractCatalog>();
        Assert.IsType<ValidateApplicationManifestResult.Valid>(ValidateApplicationManifest.Execute(PostgreSqlPairPlanningTests.Bytes(PostgreSqlPairPlanningTests.Example()), catalog));
        Assert.Equal(2, host.Services.GetServices<IDurableActionAdmissionHandler>().Count());
        Assert.Equal(5, host.Services.GetServices<IDurableActionHandler>().Count());
    }
}
