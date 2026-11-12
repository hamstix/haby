using System.Data.Common;
using System.Text;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

[CollectionDefinition("Login logging isolated", DisableParallelization = true)]
public sealed class LoginLoggingCollection;

[Collection("Login logging isolated")]
public sealed partial class PostgreSqlApplicationLoginTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>, IDisposable
{
    private readonly TestCatalogPools _catalogPools = new();
    public void Dispose() => _catalogPools.Dispose();

    private sealed record Scenario(string Connection, string Database, DurableChangeSetCommit Commit, DurablePlan Plan)
    {
        public DurableOperationKey Key(string id) => new(Commit.DeploymentId, Commit.Generation, OperationId.Create(id));
        public ManagedResourceKey Owner => new(Plan.Inputs.Environment, Plan.Inputs.Revision.ApplicationRecordId, ManifestLocalId.Create("app-login"));
        public string DeclaredDatabase { get; init; } = Database;
        public string Role => PostgreSqlResourceNaming.Role(Owner, Plan.Inputs.Providers.RequireUniformResourceProvider().ResourceNaming().Policy)!;
    }
    private sealed class Prefix(ResourceTypeId contract) : IDurableActionAdmissionHandler, IDurableResourcePlanComposer
    {
        private readonly PostgreSqlPairResourceAdmission pair = new(contract);
        public DurableActionExecutionKey Key => pair.Key;
        public bool Validate(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) => false;
        public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resource, NormalizedResource value, DurablePlanningInputs inputs) => throw new NotSupportedException();
        public bool Validate(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => pair.Validate(spec, resource, inputs, configuration);
        public IReadOnlyList<DurableOperationNode> Plan(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
            pair.Plan(spec, resource, inputs, configuration).Where(n => n.Id.Value != "app-login-access").ToArray();
        public bool ValidateGraph(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration, DurableOperationGraph graph) =>
            new DurableOperationGraph(spec.Resources.SelectMany(r => new Prefix(r.Value.Type).Plan(spec, r.Key, inputs, configuration))).Equals(graph);
    }
    private static ServiceProvider Services(string connection, IPostgreSqlLoginFaultHooks? fault = null, bool full = false, bool access = false, IPostgreSqlAccessFaultHooks? accessFault = null, IInterceptor? queryTrap = null, IDurableCreateDispatchFaultHooks? createFault = null, ISecretStore? secretStore = null)
    {
        var services = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddSingleton(PostgreSqlConnectionRendering.CreateCatalog())
            .AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore()
            .AddPostgreSqlProviderConfigurationStore().AddPostgreSqlProviderAliasStore()
            .AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
            .AddPostgreSqlProviderConfigurationCodec().AddPostgreSqlProviderRevisionPreflight()
            .AddPostgreSqlDurableDesiredStateCommitter().AddPostgreSqlDurableOperationJournal()
            .AddPostgreSqlDurableOperationExecution().AddPostgreSqlDatabaseRecoveryJournal().AddPostgreSqlDatabaseRecovery()
            .AddSingleton<IGeneratedCredentialOperations, PostgreSqlGeneratedCredentialOperations>()
            .AddPostgreSqlDatabaseRestrict().AddPostgreSqlProviderResourceReservations().AddPostgreSqlApplicationLogin();
        if (queryTrap is not null) services.AddDbContextFactory<ManifestCatalogDbContext>(options => options.AddInterceptors(queryTrap));
        services.RemoveAll<IDurableActionAdmissionHandler>();
        foreach (var contract in new[] { PostgreSqlDatabaseContract.Id, PostgreSqlApplicationLoginContract.Id })
            services.AddSingleton<IDurableActionAdmissionHandler>(full ? new PostgreSqlPairResourceAdmission(contract) : new Prefix(contract));
        services.AddSingleton<IDurableActionHandler>(p => new GeneratedCredentialAction(PostgreSqlApplicationLoginAction.RegistrationKey.Admission,
            p.GetRequiredService<IDurableOperationJournal>(), p.GetRequiredService<IGeneratedCredentialSlots>(), p.GetRequiredService<IGeneratedCredentialOperations>(), TimeProvider.System));
        if (fault is not null) services.AddSingleton(fault);
        if (access) services.AddPostgreSqlApplicationAccess();
        if (accessFault is not null) services.AddSingleton(accessFault);
        if (createFault is not null) services.AddSingleton(createFault);
        if (secretStore is not null) services.AddSingleton(secretStore);
        return services.BuildServiceProvider();
    }
    private static ManifestCatalogDbContext Context(string connection) => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
    private async Task<string> Database(string prefix)
    {
        var name = prefix + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }
    private async Task<Scenario> Prepare(bool full = false, bool render = false, string? renderingProfile = null,
        Action<System.Text.Json.Nodes.JsonNode>? configureManifest = null, string? namingPrefix = null)
    {
        var connection = _catalogPools.Register(new NpgsqlConnectionStringBuilder(await Database("login_catalog_")));
        var name = "login_target_" + Guid.NewGuid().ToString("N");
        await using var services = Services(connection, full: full);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = services.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var target = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        var bytes = Encoding.UTF8.GetBytes(target.Password!);
        try { await secrets.CreateAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator), reference, bytes, default); }
        finally { Array.Clear(bytes); }
        var config = new PostgreSqlProviderConfiguration(1, target.Host!, target.Port, target.Database!, target.Username!, reference, PostgreSqlDatabaseCheckpointCapability.ImplementationId);
        var providers = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await providers.CreateInstanceAsync(environment, config, default);
        await providers.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), config, default);
        Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment,
            ProviderAlias.Create("postgresql.primary"), null, new(instance, ProviderConfigurationRevisionNumber.Create(1)), "tester", default)).Outcome);
        if (namingPrefix is not null)
        {
            var naming = services.GetRequiredService<IResourceNamingStore>();
            Assert.Equal(ResourceNamingOutcome.Created, (await naming.CreateAsync(environment, new("contour", 1), new("prefixed", namingPrefix), "tester", default)).Outcome);
            Assert.Equal(ResourceNamingOutcome.Changed, (await naming.SelectAsync(environment, ProviderAlias.Create("postgresql.primary"),
                new(instance, ProviderConfigurationRevisionNumber.Create(1)), null, new("contour", 1), "tester", default)).Outcome);
        }
        var envelope = PostgreSqlPairPlanningTests.Example();
        envelope["manifest"]!["spec"]!["resources"]!["db"]!["properties"]!["name"] = name;
        if (render)
        {
            foreach (var component in new[] { "api", "worker" })
                envelope["manifest"]!["spec"]!["components"]![component]!["bindings"]!["storage"]!["configurationSlot"] = "main-database";
            envelope["manifest"]!["spec"]!["components"]!["api"]!["bindings"]!["storage"]!["options"] = System.Text.Json.Nodes.JsonNode.Parse("{\"timeout\":200}"u8);
            var profile = Assert.IsType<ImportConfigurationProfileResult.Succeeded>(await new ImportConfigurationProfile(
                services.GetRequiredService<IConfigurationProfileImportStore>(), TimeProvider.System).ExecuteAsync(Encoding.UTF8.GetBytes(renderingProfile ?? RenderingProfile), "tester"));
            var environmentMetadata = (await services.GetRequiredService<IEnvironmentCatalog>().GetAsync(EnvironmentId.Create("pilot"), default))!;
            var targetScope = new ProfileAssignmentTarget.Environment();
            Assert.Equal(AssignmentOutcome.Created, (await services.GetRequiredService<IProfileAssignmentStore>().ApplyAsync(environmentMetadata,
                new(targetScope, [new ProfileDecision.Use(profile.Revision.Reference)]), new AssignmentPrecondition.ExpectedAbsence(),
                new("tester", DateTimeOffset.UtcNow, null), default)).Outcome);
        }
        configureManifest?.Invoke(envelope);
        var importCatalog = render ? new ManifestContractCatalog(
            [PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition], [],
            [new(OutputContractId.Create(PostgreSqlApplicationConnectionContract.Contract), PostgreSqlConsumerOptions.Schema)],
            [new(PostgreSqlConnectionRendering.Renderer)]) : PostgreSqlPairPlanningTests.Catalog;
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), importCatalog, TimeProvider.System)
            .ExecuteAsync(PostgreSqlPairPlanningTests.Bytes(envelope), "tester"));
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(environment, imported.ManifestRevisionRef.ApplicationRecordId,
            imported.ManifestRevisionRef, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
        var planned = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
        var plan = (await planner.FindPlanAsync(planned.Id, default))!;
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(plan.Id, default));
        var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, plan.Id, "tester", default)).Commit;
        return new(connection, PostgreSqlResourceNaming.Database(name, plan.Inputs.Providers.RequireUniformResourceProvider().ResourceNaming().Policy)!, (await services.GetRequiredService<IDurableDesiredStateCommitter>().FindCommitAsync(change.Id, default))!, plan) { DeclaredDatabase = name };
    }
    private static async Task<DurableExecutionResult> Execute(Scenario scenario, string id, IPostgreSqlLoginFaultHooks? fault = null, CancellationToken token = default)
    {
        await using var services = Services(scenario.Connection, fault);
        return await services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(scenario.Key(id), token);
    }
    private static async Task Ready(Scenario scenario)
    {
        foreach (var id in new[] { "db-create", "db-restrict", "app-login-material" })
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, id)).Outcome);
    }
    private async Task<string> Policy(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database, Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString); await connection.OpenAsync();
        return (string)(await new NpgsqlCommand("""
            SELECT json_build_array(d.oid, d.datdba, d.datacl, n.nspowner, n.nspacl,
              (SELECT count(*) FROM pg_catalog.pg_class WHERE relnamespace = n.oid))::text
            FROM pg_catalog.pg_database d CROSS JOIN pg_catalog.pg_namespace n
            WHERE d.datname = current_database() AND n.nspname = 'public'
            """, connection).ExecuteScalarAsync())!;
    }
    private async Task<string> UnrelatedPolicy()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        var databases = (string)(await new NpgsqlCommand("SELECT json_agg(json_build_array(oid, datname, datdba, datacl) ORDER BY datname)::text FROM pg_catalog.pg_database WHERE datistemplate OR datname = 'postgres'", connection).ExecuteScalarAsync())!;
        return databases + await Policy("postgres") + await Policy("template1");
    }


    private async Task<bool> Exists(string role)
    {
        await using var admin = new NpgsqlConnection(fixture.ConnectionString); await admin.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = $1)", admin);
        command.Parameters.AddWithValue(role); return (bool)(await command.ExecuteScalarAsync())!;
    }
    private static async Task<SecretRef> Password(Scenario scenario)
    {
        await using var context = Context(scenario.Connection);
        var row = await context.OperationOutputs.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-material");
        return GeneratedCredentialOutputCodec.Decode(row.Contract, row.Payload)!.Reference;
    }
    private static async Task<byte[]> Material(Scenario scenario, SecretRef reference)
    {
        await using var services = Services(scenario.Connection);
        using var material = await services.GetRequiredService<ISecretStore>().ResolveAsync(
            new(new SecretOwner.Resource(scenario.Owner), SecretPurpose.ApplicationLogin), reference, default);
        return material!.CopyBytes();
    }
    private async Task AssertLogin(Scenario scenario, byte[] bytes)
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        { Database = scenario.Database, Username = scenario.Role, Password = Encoding.ASCII.GetString(bytes), Pooling = false };
        await using (var connection = new NpgsqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT current_user = $1 AND current_database() = $2", connection);
            command.Parameters.AddWithValue(scenario.Role); command.Parameters.AddWithValue(scenario.Database);
            Assert.True(await command.ExecuteScalarAsync() is true);
        }
        builder.Password = Guid.NewGuid().ToString("N");
        await using var wrong = new NpgsqlConnection(builder.ConnectionString);
        var error = await Assert.ThrowsAsync<PostgresException>(() => wrong.OpenAsync());
        Assert.True(error.SqlState == PostgresErrorCodes.InvalidPassword);
        builder.Password = null;
    }
    private async Task AssertRole(Scenario scenario, PostgreSqlRoleEvidence evidence)
    {
        await using var admin = new NpgsqlConnection(fixture.ConnectionString); await admin.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT r.oid = $1 AND r.rolname = $2 AND r.rolcanlogin
              AND NOT (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls)
              AND NOT EXISTS (SELECT 1 FROM pg_auth_members WHERE member = r.oid)
              AND has_database_privilege(r.oid, d.oid, 'CONNECT')
              AND NOT has_database_privilege(r.oid, d.oid, 'CREATE') AND NOT has_database_privilege(r.oid, d.oid, 'TEMP')
              AND d.datdba <> r.oid
            FROM pg_roles r CROSS JOIN pg_database d WHERE r.rolname = $2 AND d.datname = $3
            """, admin);
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, evidence.RoleOid);
        command.Parameters.AddWithValue(scenario.Role); command.Parameters.AddWithValue(scenario.Database);
        Assert.True(await command.ExecuteScalarAsync() is true);
    }

    [Fact]
    public async Task Four_step_test_prefix_authenticates_saved_password_without_completing_database_owner_policy()
    {
        var scenario = await Prepare(); var unrelated = await UnrelatedPolicy();
        Assert.False(await Exists(scenario.Role));
        await Ready(scenario); var reference = await Password(scenario); var bytes = await Material(scenario, reference);
        var before = await Policy(scenario.Database);
        var ownershipAndObjects = await OwnershipAndObjects(scenario.Database);
        try
        {
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, "app-login-create")).Outcome);
            await AssertLogin(scenario, bytes);
            await using (var verify = Context(scenario.Connection))
            {
                var row = await verify.OperationOutputs.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-create");
                var evidence = PostgreSqlRoleEvidenceContract.Decode(new(row.Contract, row.Key, row.Payload))!;
                Assert.NotNull(evidence); Assert.True(evidence.RoleOid > 0); await AssertRole(scenario, evidence);
                var operation = await verify.DurableOperations.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-create");
                var attempt = await verify.DurableOperationAttempts.AsNoTracking().SingleAsync(r => r.AttemptId == operation.CurrentAttemptId);
                Assert.Equal("Succeeded", operation.State); Assert.Equal("Succeeded", attempt.Outcome); Assert.NotNull(attempt.DispatchedAt); Assert.NotNull(attempt.FinishedAt);
                Assert.Equal(3, await verify.OperationOutputs.CountAsync());
                // Only this deliberately shortened private fixture is complete. The real
                // five-node pair is separately required to remain Pending and unapplied.
                Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            }
            Assert.Equal(unrelated, await UnrelatedPolicy());
            Assert.Equal(ownershipAndObjects, await OwnershipAndObjects(scenario.Database));
            // CONNECT is the only intended database ACL difference. Check schema/owner/object state independently.
            await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = scenario.Database }.ConnectionString))
            {
                await admin.OpenAsync();
                Assert.True(await new NpgsqlCommand("SELECT NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'app') AND NOT EXISTS (SELECT 1 FROM pg_default_acl)", admin).ExecuteScalarAsync() is true);
            }
            Assert.NotEqual(before, await Policy(scenario.Database));
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, "app-login-create", new Fault("trap"))).Outcome);
            Assert.Equal(reference, await Password(scenario));
            var again = await Material(scenario, reference); try { Assert.True(bytes.SequenceEqual(again)); } finally { Array.Clear(again); }
            await using var final = Context(scenario.Connection); Assert.Equal(4, await final.DurableOperationAttempts.CountAsync());
        }
        finally { Array.Clear(bytes); }
    }

    private async Task<string> OwnershipAndObjects(string database)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        { Database = database, Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        // Read every schema owner/ACL and creator-default row, not just the absence of
        // one expected application schema. The database CONNECT ACL is intentionally separate.
        await using var command = new NpgsqlCommand("""
            SELECT json_build_array(d.datdba,
              (SELECT json_agg(json_build_array(nspname, nspowner, nspacl) ORDER BY nspname) FROM pg_catalog.pg_namespace),
              (SELECT json_agg(json_build_array(defaclrole, defaclnamespace, defaclobjtype, defaclacl)
                ORDER BY defaclrole, defaclnamespace, defaclobjtype) FROM pg_catalog.pg_default_acl),
              (SELECT json_agg(json_build_array(setrole, setdatabase, setconfig) ORDER BY setrole)
                FROM pg_catalog.pg_db_role_setting WHERE setdatabase = d.oid))::text
            FROM pg_catalog.pg_database d WHERE datname = pg_catalog.current_database()
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed class Fault(string mode, CancellationTokenSource? cancellation = null) : IPostgreSqlLoginFaultHooks
    {
        public int Markers;
        public ValueTask AfterMarkerAsync(CancellationToken token)
        {
            Markers++;
            if (mode == "cancel") { cancellation!.Cancel(); token.ThrowIfCancellationRequested(); }
            if (mode == "trap") throw new InvalidOperationException("Unexpected redispatch.");
            return ValueTask.CompletedTask;
        }
        public async ValueTask AfterRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        {
            if (mode is "rollback" or "rollback-unknown")
            {
                try { await new NpgsqlCommand("SELECT 1/0", connection, transaction).ExecuteScalarAsync(token); }
                catch (PostgresException) { if (mode == "rollback-unknown") await connection.CloseAsync(); throw; }
            }
        }
        public ValueTask AfterCommitAsync(CancellationToken token) => mode == "commit" ? ValueTask.FromException(new IOException("Lost response.")) : ValueTask.CompletedTask;
        public ValueTask BeforeVerificationAsync(CancellationToken token) => mode == "verification" ? ValueTask.FromException(new IOException("Unavailable.")) : ValueTask.CompletedTask;
        public ValueTask BeforeAuthenticationAsync(CancellationToken token) => mode == "authentication" ? ValueTask.FromException(new IOException("Unavailable.")) : ValueTask.CompletedTask;
        public ValueTask BeforeFinalizationAsync(CancellationToken token) => mode == "finalization" ? ValueTask.FromException(new IOException("Unavailable.")) : ValueTask.CompletedTask;
    }
    [Theory]
    [InlineData("rollback")]
    [InlineData("rollback-unknown")]
    [InlineData("commit")]
    [InlineData("verification")]
    [InlineData("authentication")]
    [InlineData("finalization")]
    [InlineData("cancel")]
    public async Task Post_marker_faults_preserve_rollback_or_uncertainty_without_replay(string mode)
    {
        var scenario = await Prepare(); await Ready(scenario); var reference = await Password(scenario);
        var before = await Policy(scenario.Database); using var cancel = new CancellationTokenSource(); var fault = new Fault(mode, cancel);
        var expected = mode == "rollback" ? DurableExecutionOutcome.Failed : DurableExecutionOutcome.NeedsAttention;
        Assert.Equal(expected, (await Execute(scenario, "app-login-create", fault, cancel.Token)).Outcome);
        Assert.Equal(mode is not ("rollback" or "rollback-unknown" or "cancel"), await Exists(scenario.Role));
        if (mode is "rollback" or "rollback-unknown" or "cancel") Assert.Equal(before, await Policy(scenario.Database));
        Assert.Equal(expected, (await Execute(scenario, "app-login-create", fault)).Outcome); Assert.Equal(1, fault.Markers);
        Assert.Equal(reference, await Password(scenario));
        await using var verify = Context(scenario.Connection);
        Assert.False(await verify.OperationOutputs.AnyAsync(r => r.OperationId == "app-login-create"));
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        await using var services = Services(scenario.Connection);
        var inspection = await services.GetRequiredService<IPostgreSqlDatabaseRecovery>().InspectAsync(scenario.Key("app-login-create"), default);
        Assert.Null(inspection!.DatabaseName); Assert.Null(inspection.UncertainAttemptId);
    }

    [Fact]
    public async Task All_new_effects_composed_still_refuse_full_five_node_pair_in_common_direct_and_generation_paths()
    {
        var scenario = await Prepare(full: true);
        await using var services = Services(scenario.Connection, full: true);
        Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(scenario.Key("db-create"), default)).Outcome);
        Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await services.GetRequiredService<PostgreSqlDurableOperationExecutor>().ExecuteAsync(scenario.Key("db-create"), default)).Outcome);
        Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await services.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default)).StopOutcome);
        var journal = services.GetRequiredService<IDurableOperationJournal>();
        foreach (var handler in services.GetServices<IDurableActionHandler>().Where(h => h is PostgreSqlApplicationLoginAction or PostgreSqlDatabaseRestrictAction))
        {
            var id = handler is PostgreSqlApplicationLoginAction ? "app-login-create" : "db-restrict";
            Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await handler.ExecuteAsync((await journal.FindAsync(scenario.Key(id), default))!, default)).Outcome);
        }
        await using var verify = Context(scenario.Connection);
        Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.False(await Exists(scenario.Role));
        await using var admin = new NpgsqlConnection(fixture.ConnectionString); await admin.OpenAsync();
        await using var absence = new NpgsqlCommand("SELECT NOT EXISTS (SELECT 1 FROM pg_catalog.pg_database WHERE datname = $1)", admin);
        absence.Parameters.AddWithValue(scenario.Database);
        Assert.True(await absence.ExecuteScalarAsync() is true);
    }
}
