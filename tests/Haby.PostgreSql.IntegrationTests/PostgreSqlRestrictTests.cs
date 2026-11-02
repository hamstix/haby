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

public sealed partial class PostgreSqlRestrictTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed record Scenario(string Connection, string Database, DurableChangeSetCommit Commit)
    {
        public DurableOperationKey Create => new(Commit.DeploymentId, Commit.Generation, OperationId.Create("db-create"));
        public DurableOperationKey Restrict => Create with { OperationId = OperationId.Create("db-restrict") };
    }
    private static OperationOutputRef Selector => new(OperationId.Create("db-create"), PostgreSqlDatabaseOutputContract.Contract, "database");
    private static readonly ManifestContractCatalog Catalog = new([PostgreSqlDatabaseContract.Definition], [], [], []);

    // Narrow internal real-effect fixture, not a production pair or fake-success executor.
    private sealed class Admission : IDurableActionAdmissionHandler, IDurableResourcePlanComposer
    {
        public DurableActionExecutionKey Key => PostgreSqlDatabaseCreateAction.RegistrationKey;
        public bool Validate(NormalizedResource resource, DurablePlanningInputs inputs) => true;
        public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) => throw new NotSupportedException();
        public bool Validate(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
            spec.Resources.Count == 1 && spec.Components.Count == 0 && spec.Resources[resource].Type == PostgreSqlDatabaseContract.Id;
        public IReadOnlyList<DurableOperationNode> Plan(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration)
        {
            var references = new DurableActionInputReferences(inputs.Environment, inputs.ProviderInstance, inputs.ConfigurationRevision, inputs.ImplementationId);
            var name = ((ConfigurationString)spec.Resources[resource].Properties.Members["name"]).Value;
            var admin = JsonDocument.Parse(configuration.PayloadJson).RootElement.GetProperty("AdministratorUserName"u8).GetString()!;
            return [new(OperationId.Create("db-create"), resource, Key, references, new(PostgreSqlDatabaseCreateAction.Format,
                "{\"DatabaseName\":\"" + name + "\"}"), []),
                new(OperationId.Create("db-restrict"), resource, PostgreSqlDatabaseRestrictAction.RegistrationKey, references,
                    new(PostgreSqlDatabaseRestrictAction.Format, "{\"databaseName\":\"" + name + "\",\"databaseSource\":{\"producerOperationId\":\"db-create\",\"outputContract\":\"" +
                    Selector.OutputContract + "\",\"outputKey\":\"database\"},\"expectedOwnerUserName\":\"" + admin + "\"}"), [Selector.ProducerOperationId])
                { OutputReferences = [Selector] }];
        }
        public bool ValidateGraph(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs,
            PreparedProviderConfiguration configuration, DurableOperationGraph graph) => new DurableOperationGraph(Plan(spec, resource, inputs, configuration)).Equals(graph);
    }

    private static ServiceProvider Services(string connection, IPostgreSqlRestrictFaultHooks? fault = null, bool standalone = false)
    {
        var services = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore()
            .AddPostgreSqlProviderConfigurationStore().AddPostgreSqlProviderAliasStore()
            .AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
            .AddPostgreSqlProviderConfigurationCodec().AddPostgreSqlProviderRevisionPreflight()
            .AddPostgreSqlDurableDesiredStateCommitter().AddPostgreSqlDurableOperationJournal()
            .AddPostgreSqlDurableOperationExecution().AddPostgreSqlDatabaseRecoveryJournal().AddPostgreSqlDatabaseRecovery()
            .AddPostgreSqlDatabaseRestrict();
        if (!standalone)
        {
            services.RemoveAll<IDurableActionAdmissionHandler>();
            services.AddSingleton<IDurableActionAdmissionHandler, Admission>();
        }
        if (fault is not null) services.AddSingleton(fault);
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
    private async Task<Scenario> Prepare(bool standalone = false)
    {
        var connection = await Database("restrict_catalog_");
        var name = "restrict_target_" + Guid.NewGuid().ToString("N");
        await using var services = Services(connection, standalone: standalone);
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
        var envelope = Encoding.UTF8.GetBytes("""
            {"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"restrict-test"},"spec":{"resources":{"db":{"type":"postgresql.database/v1alpha1","providerRef":"postgresql.primary","properties":{"name":"NAME"}}}}}}
            """.Replace("NAME", name, StringComparison.Ordinal));
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), Catalog, TimeProvider.System).ExecuteAsync(envelope, "tester"));
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(environment, imported.ManifestRevisionRef.ApplicationRecordId,
            imported.ManifestRevisionRef, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
        var plan = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(plan.Id, default));
        var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, plan.Id, "tester", default)).Commit;
        return new(connection, name, commit);
    }
    private static async Task<DurableExecutionResult> Execute(Scenario scenario, bool restrict, IPostgreSqlRestrictFaultHooks? fault = null, CancellationToken token = default)
    {
        await using var services = Services(scenario.Connection, fault);
        return await services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(restrict ? scenario.Restrict : scenario.Create, token);
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

    [Fact]
    public async Task Confirmed_create_publishes_exact_evidence_then_restrict_revokes_PUBLIC_and_survives_restart()
    {
        var scenario = await Prepare();
        var unrelated = await UnrelatedPolicy();
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, false)).Outcome);
        var before = await Policy(scenario.Database);
        await using (var verify = Context(scenario.Connection))
        {
            var row = await verify.OperationOutputs.AsNoTracking().SingleAsync();
            var output = PostgreSqlDatabaseOutputContract.Decode(new(row.Contract, row.Key, row.Payload));
            Assert.NotNull(output); Assert.Equal(scenario.Database, output.DatabaseName); Assert.True(output.DatabaseOid > 0);
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Equal("Succeeded", (await verify.DurableOperationAttempts.AsNoTracking().SingleAsync()).Outcome);
        }
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, true)).Outcome);
        Assert.NotEqual(before, await Policy(scenario.Database)); Assert.Equal(unrelated, await UnrelatedPolicy());
        await AssertPublicDenied(scenario.Database);
        var after = await Policy(scenario.Database);
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, false)).Outcome);
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, true, new Fault("trap"))).Outcome);
        Assert.Equal(after, await Policy(scenario.Database));
        await using var final = Context(scenario.Connection);
        Assert.Equal(1, (await final.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Equal(2, await final.DurableOperationAttempts.CountAsync());
        Assert.Equal(1, await final.OperationOutputs.CountAsync());
    }

    private async Task AssertPublicDenied(string database)
    {
        var probe = "probe_" + Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(fixture.ConnectionString); await admin.OpenAsync();
        await new NpgsqlCommand($"CREATE ROLE \"{probe}\" NOSUPERUSER NOCREATEDB NOCREATEROLE LOGIN PASSWORD '{password}'", admin).ExecuteNonQueryAsync();
        try
        {
            await using var command = new NpgsqlCommand("SELECT has_database_privilege($1, $2, 'CONNECT') OR has_database_privilege($1, $2, 'CREATE') OR has_database_privilege($1, $2, 'TEMP')", admin);
            command.Parameters.AddWithValue(probe); command.Parameters.AddWithValue(database);
            Assert.Equal(false, await command.ExecuteScalarAsync());
            await using (var denied = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            { Database = database, Username = probe, Password = password, Pooling = false }.ConnectionString))
            {
                var refused = await Assert.ThrowsAsync<PostgresException>(() => denied.OpenAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
            }
            await using var target = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString);
            await target.OpenAsync();
            await new NpgsqlCommand($"SET ROLE \"{probe}\"", target).ExecuteNonQueryAsync();
            Assert.Equal(false, await new NpgsqlCommand("SELECT has_schema_privilege(current_user, 'public', 'USAGE') OR has_schema_privilege(current_user, 'public', 'CREATE')", target).ExecuteScalarAsync());
            var error = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("CREATE TABLE public.probe(id int)", target).ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        finally { await new NpgsqlCommand($"DROP ROLE \"{probe}\"", admin).ExecuteNonQueryAsync(); }
    }

    private sealed class Fault(string mode, CancellationTokenSource? source = null) : IPostgreSqlRestrictFaultHooks
    {
        public int Dispatches;
        public ValueTask AfterMarkerAsync(CancellationToken token)
        {
            Dispatches++;
            if (mode == "cancel") { source!.Cancel(); token.ThrowIfCancellationRequested(); }
            if (mode == "trap") throw new InvalidOperationException("Unexpected redispatch.");
            return ValueTask.CompletedTask;
        }
        public async ValueTask AfterDatabaseRevokeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        {
            if (mode == "rollback") await new NpgsqlCommand("SELECT 1/0", connection, transaction).ExecuteScalarAsync(token);
            if (mode == "rollback-unknown")
            {
                try { await new NpgsqlCommand("SELECT 1/0", connection, transaction).ExecuteScalarAsync(token); }
                catch (PostgresException) { await connection.CloseAsync(); throw; }
            }
        }
        public ValueTask AfterCommitAsync(CancellationToken token) => mode == "commit" ? ValueTask.FromException(new IOException("Lost commit response.")) : ValueTask.CompletedTask;
        public ValueTask BeforeVerificationAsync(CancellationToken token) => mode == "verification" ? ValueTask.FromException(new IOException("Verification unavailable.")) : ValueTask.CompletedTask;
        public ValueTask BeforeFinalizationAsync(CancellationToken token) => mode == "finalization" ? ValueTask.FromException(new IOException("Catalog unavailable.")) : ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("rollback-unknown")]
    [InlineData("commit")]
    [InlineData("verification")]
    [InlineData("cancel")]
    [InlineData("finalization")]
    public async Task External_transaction_faults_are_honest_and_never_redispatched(string mode)
    {
        var scenario = await Prepare();
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, false)).Outcome);
        var before = await Policy(scenario.Database); var unrelated = await UnrelatedPolicy();
        using var cancellation = new CancellationTokenSource(); var fault = new Fault(mode, cancellation);
        var outcome = mode == "rollback" ? DurableExecutionOutcome.Failed : DurableExecutionOutcome.NeedsAttention;
        Assert.Equal(outcome, (await Execute(scenario, true, fault, cancellation.Token)).Outcome);
        var after = await Policy(scenario.Database);
        if (mode is "rollback" or "rollback-unknown" or "cancel") Assert.Equal(before, after);
        else { Assert.NotEqual(before, after); await AssertPublicDenied(scenario.Database); }
        Assert.Equal(unrelated, await UnrelatedPolicy());
        Assert.Equal(outcome, (await Execute(scenario, true, fault)).Outcome); Assert.Equal(1, fault.Dispatches);
        await using (var recoveryServices = Services(scenario.Connection))
        {
            var recovery = recoveryServices.GetRequiredService<IPostgreSqlDatabaseRecovery>();
            var preview = (await recovery.InspectAsync(scenario.Restrict, default))!;
            Assert.Null(preview.DatabaseName); Assert.Null(preview.UncertainAttemptId);
            var command = new AssignExistingPostgreSqlDatabase(scenario.Restrict, preview.Environment,
                preview.Snapshot.AttemptId!.Value, true, PostgreSqlRecoveryReason.OperatorAcceptedExistingDatabase, "Not a create", "tester");
            Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.Rejected, (await recovery.AssignExistingDatabaseAsync(command, default)).Outcome);
        }
        await using var verify = Context(scenario.Connection);
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        var attempt = await verify.DurableOperationAttempts.AsNoTracking().SingleAsync(a => a.OperationId == "db-restrict");
        Assert.Equal(mode == "rollback" ? "ServerRejected" : "Uncertain", attempt.Outcome);
    }

    [Fact]
    public async Task Same_name_replacement_with_different_OID_refuses_without_ACL_change()
    {
        var scenario = await Prepare(); Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, false)).Outcome);
        var original = await Policy(scenario.Database);
        var retained = scenario.Database + "_old";
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand($"ALTER DATABASE \"{scenario.Database}\" RENAME TO \"{retained}\"", connection).ExecuteNonQueryAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{scenario.Database}\"", connection).ExecuteNonQueryAsync();
        }
        var before = await Policy(scenario.Database); var fault = new Fault("trap");
        Assert.Equal(DurableExecutionOutcome.Failed, (await Execute(scenario, true, fault)).Outcome);
        Assert.Equal(0, fault.Dispatches); Assert.Equal(before, await Policy(scenario.Database));
        Assert.Equal(original, await Policy(retained));
    }

    [Fact]
    public async Task Manual_assignment_has_no_output_and_cannot_authorize_restrict_after_restart()
    {
        var scenario = await Prepare(); Guid attempt;
        await using (var arrange = Services(scenario.Connection))
        {
            var journal = arrange.GetRequiredService<IDurableOperationJournal>();
            var started = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(scenario.Create, DateTimeOffset.UtcNow, default));
            attempt = started.AttemptId;
            Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(scenario.Create, attempt, DateTimeOffset.UtcNow, default));
            Assert.True(await journal.TryRecoverDispatchingAsync(scenario.Create, DateTimeOffset.UtcNow, default));
        }
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        { await admin.OpenAsync(); await new NpgsqlCommand($"CREATE DATABASE \"{scenario.Database}\"", admin).ExecuteNonQueryAsync(); }
        var before = await Policy(scenario.Database);
        for (var iteration = 0; iteration < 2; iteration++)
        {
            await using var act = Services(scenario.Connection);
            var journal = act.GetRequiredService<IDurableOperationJournal>();
            var operation = (await journal.FindAsync(scenario.Create, default))!;
            var command = new AssignExistingPostgreSqlDatabase(scenario.Create, operation.Environment, attempt, true,
                PostgreSqlRecoveryReason.OperatorAcceptedExistingDatabase, "Explicit fixture assignment", "tester");
            var assigned = await act.GetRequiredService<IPostgreSqlDatabaseRecovery>().AssignExistingDatabaseAsync(command, default);
            Assert.Equal(iteration == 0 ? PostgreSqlDatabaseAssignmentOutcome.Assigned : PostgreSqlDatabaseAssignmentOutcome.AlreadyAssigned, assigned.Outcome);
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(scenario.Create, default)).Outcome);
            Assert.Equal(DurableExecutionOutcome.InputUnavailable, (await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(scenario.Restrict, default)).Outcome);
        }
        Assert.Equal(before, await Policy(scenario.Database));
        await using var verify = Context(scenario.Connection);
        Assert.Empty(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Equal("Uncertain", (await verify.DurableOperationAttempts.AsNoTracking().SingleAsync()).Outcome);
        Assert.Single(await verify.PostgreSqlDatabaseRecoveryDecisions.AsNoTracking().ToArrayAsync());
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }
}
