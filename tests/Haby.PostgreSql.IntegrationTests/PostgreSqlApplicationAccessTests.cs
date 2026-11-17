using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public abstract partial class ApplicationLoginScenario
{
    private protected static ServiceProvider OwnerServices(Scenario scenario, IPostgreSqlAccessFaultHooks? fault = null) =>
            Services(scenario.Connection, full: true, access: true, accessFault: fault);
    private protected static async Task<DurableExecutionResult> AccessExecute(Scenario scenario, string id = "app-login-access",
            IPostgreSqlAccessFaultHooks? fault = null, CancellationToken token = default)
    {
        await using var services = OwnerServices(scenario, fault);
        return await services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(scenario.Key(id), token);
    }
    private protected static async Task AccessReady(Scenario scenario)
    {
        foreach (var id in new[] { "db-create", "db-restrict", "app-login-material", "app-login-create" })
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await AccessExecute(scenario, id)).Outcome);
        await using var verify = Context(scenario.Connection);
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Equal(4, await verify.DurableOperationAttempts.CountAsync());
        Assert.Equal(3, await verify.OperationOutputs.CountAsync());
    }
    private protected string AdministrativeTarget(Scenario scenario) => new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
    { Database = scenario.Database, Pooling = false }.ConnectionString;
    private protected static async Task<PostgreSqlApplicationConnection> ConnectionOutput(Scenario scenario)
    {
        await using var context = Context(scenario.Connection);
        var row = await context.OperationOutputs.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-access");
        return PostgreSqlApplicationConnectionContract.Decode(new(row.Contract, row.Key, row.Payload))!;
    }
    private protected static async Task AssertComplete(Scenario scenario)
    {
        await using var verify = Context(scenario.Connection);
        Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Equal(5, await verify.DurableOperations.CountAsync(r => r.State == "Succeeded"));
        Assert.Equal(5, await verify.DurableOperationAttempts.CountAsync(r => r.Outcome == "Succeeded" && r.FinishedAt != null));
        Assert.Equal(4, await verify.DurableOperationAttempts.CountAsync(r => r.DispatchedAt != null));
        Assert.Equal(4, await verify.OperationOutputs.CountAsync());
    }
    private protected sealed class AccessFault(string mode, CancellationTokenSource? cancel = null) : IPostgreSqlAccessFaultHooks
    {
        public int Markers;
        public ValueTask AfterMarkerAsync(CancellationToken token)
        {
            Markers++;
            if (mode == "cancel") { cancel!.Cancel(); token.ThrowIfCancellationRequested(); }
            if (mode == "trap") throw new InvalidOperationException("Unexpected redispatch.");
            return ValueTask.CompletedTask;
        }
        public ValueTask AfterOwnershipAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) =>
            mode == "owner-rollback" ? Reject(connection, transaction, token) : ValueTask.CompletedTask;
        public async ValueTask AfterSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        {
            if (mode is "schema-rollback" or "rollback-unknown")
            {
                try { await Reject(connection, transaction, token); }
                catch (PostgresException) { if (mode == "rollback-unknown") await connection.CloseAsync(); throw; }
            }
        }
        public ValueTask BeforeCommitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) =>
            mode == "defaults-rollback" ? Reject(connection, transaction, token) : ValueTask.CompletedTask;
        private static async ValueTask Reject(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) =>
            await new NpgsqlCommand("SELECT 1/0", connection, transaction).ExecuteScalarAsync(token);
        public ValueTask AfterCommitAsync(CancellationToken token) => Fail("commit");
        public ValueTask BeforeVerificationAsync(CancellationToken token) => Fail("verification");
        public ValueTask BeforeAuthenticationAsync(CancellationToken token) => Fail("authentication");
        public ValueTask BeforeFinalizationAsync(CancellationToken token) => Fail("finalization");
        private ValueTask Fail(string boundary) => mode == boundary ? ValueTask.FromException(new IOException("Injected interruption.")) : ValueTask.CompletedTask;
    }

}

public sealed partial class PostgreSqlApplicationLoginTests
{

    [Fact]
    public Task Five_real_actions_publish_exact_connection_and_run_two_real_EF_migrations_as_generated_login() => FiveActions("app");

    [Fact]
    public Task Explicit_public_five_actions_preserve_standard_schema_and_run_real_EF_migrations_as_generated_login() => FiveActions("public");

    private async Task FiveActions(string schema)
    {
        var scenario = await Prepare(full: true, render: true, configureManifest: envelope =>
            envelope["manifest"]!["spec"]!["resources"]!["app-login"]!["properties"]!["schema"] = schema);
        Assert.Contains("\"schema\":\"" + schema + "\"", scenario.Plan.Graph.Nodes.Single(n => n.Id.Value == "app-login-access").Input.Json);
        await using (var before = Context(scenario.Connection))
        {
            Assert.Empty(await before.DurableOperationAttempts.ToArrayAsync());
            Assert.Empty(await before.OperationOutputs.ToArrayAsync());
            Assert.Empty(await before.GeneratedCredentialSlots.ToArrayAsync());
        }
        Assert.False(await Exists(scenario.Role));
        var unrelated = await UnrelatedPolicy();
        await AccessReady(scenario);
        PostgreSqlDatabaseOutput created;
        await using (var receipt = Context(scenario.Connection))
        {
            var row = await receipt.OperationOutputs.AsNoTracking().SingleAsync(r => r.OperationId == "db-create");
            created = PostgreSqlDatabaseOutputContract.Decode(new(row.Contract, row.Key, row.Payload))!;
        }
        // A template/administrator-owned contained object is never reassigned by database ownership.
        await Sql(AdministrativeTarget(scenario), "CREATE TABLE public.administrator_object(id integer); CREATE SCHEMA administrator_schema");
        var reference = await Password(scenario);
        var bytes = await Material(scenario, reference);
        try
        {
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await AccessExecute(scenario)).Outcome);
            var output = await ConnectionOutput(scenario);
            var endpoint = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
            Assert.Equal(new(endpoint.Host!, endpoint.Port, scenario.Database, schema, scenario.Role, reference), output);
            await AssertLogin(scenario, bytes);
            await using (var rendering = OwnerServices(scenario))
            {
                var saved = (await rendering.GetRequiredService<IDurableDeploymentPlanner>().FindPlanAsync(scenario.Plan.Id, default))!;
                Assert.Equal(scenario.Plan, saved);
                Assert.Equal(DurablePlanCurrentStatus.ChangeSetStale, await rendering.GetRequiredService<IDurableDeploymentPlanner>().CheckPlanAsync(saved.Id, default));
                var catalog = PostgreSqlConnectionRendering.CreateCatalog();
                var renderer = new ConfigurationOutputRenderer(catalog, rendering.GetRequiredService<IConfigurationOutputEvidence>());
                var material = Encoding.ASCII.GetChars(bytes);
                try
                {
                    foreach (var pin in saved.Inputs.Configuration!.RenderPins)
                    {
                        var fragment = (await renderer.RenderAsync(pin, scenario.Commit.DeploymentId, scenario.Commit.Generation, scenario.Owner, default))!;
                        var deferred = Assert.IsType<ConfigurationDeferredValue>(fragment.Value);
                        Assert.Equal(new ConfigurationString(schema), deferred.Arguments.Members["schema"]);
                        var formatted = catalog.Format(pin, deferred, material)!;
                        await using var consumer = new NpgsqlConnection(formatted.Reveal());
                        await consumer.OpenAsync();
                        await using var identity = new NpgsqlCommand("SELECT current_user = $1 AND current_schema() = $2 AND current_setting('search_path') = $2", consumer);
                        identity.Parameters.AddWithValue(scenario.Role); identity.Parameters.AddWithValue(schema);
                        Assert.True(await identity.ExecuteScalarAsync() is true);
                    }
                }
                finally { Array.Clear(material); }
            }
            var application = new NpgsqlConnectionStringBuilder
            {
                Host = output.Host,
                Port = output.Port,
                Database = output.DatabaseName,
                Username = output.UserName,
                Password = Encoding.ASCII.GetString(bytes),
                Pooling = false
            };
            try
            {
                await OwnerMigrationEvidence.RunAsync(application.ConnectionString);
                if (schema == "public") await PublicMigrationEvidence.RunAsync(application.ConnectionString);
                await Sql(application.ConnectionString, $"CREATE TABLE {schema}.qualified_item(id integer); CREATE TABLE unqualified_item(id integer); INSERT INTO unqualified_item VALUES (247)");
                await Sql(application.ConnectionString, $"""
                    CREATE SCHEMA additional_owned;
                    CREATE FUNCTION {schema}.future_routine() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                    CREATE TYPE {schema}.future_type AS ENUM ('one');
                    CREATE FUNCTION additional_owned.future_routine() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                    CREATE TYPE additional_owned.future_type AS ENUM ('one');
                    """);
                foreach (var sql in new[] { "CREATE DATABASE forbidden_cluster_database", "CREATE ROLE forbidden_cluster_role", "ALTER TABLE public.administrator_object ADD COLUMN forbidden integer" })
                {
                    var error = await Assert.ThrowsAsync<PostgresException>(() => Sql(application.ConnectionString, sql));
                    Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
                }
                await using var login = new NpgsqlConnection(application.ConnectionString);
                await login.OpenAsync();
                Assert.True(await new NpgsqlCommand($"SELECT current_schema() = '{schema}' AND current_setting('search_path') = '{schema}'", login).ExecuteScalarAsync() is true);
                var hardened = await Database("owner_hardened_");
                var hardenedName = new NpgsqlConnectionStringBuilder(hardened).Database!;
                await Sql(hardened, $"REVOKE ALL ON DATABASE \"{hardenedName}\" FROM PUBLIC");
                var cross = new NpgsqlConnectionStringBuilder(application.ConnectionString) { Database = hardenedName };
                var denied = await Assert.ThrowsAsync<PostgresException>(() => Sql(cross.ConnectionString, "SELECT 1"));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
                // Unrelated PostgreSQL defaults may still grant PUBLIC CONNECT; configured isolation is local.
                cross.Database = "postgres";
                await Sql(cross.ConnectionString, "SELECT 1");
            }
            finally { application.Password = null; }
            await using (var admin = new NpgsqlConnection(AdministrativeTarget(scenario)))
            {
                await admin.OpenAsync();
                await using var evidence = new NpgsqlCommand("""
                    SELECT d.datdba = r.oid AND (($2 = 'public' AND n.nspowner = 'pg_database_owner'::regrole) OR ($2 <> 'public' AND n.nspowner = r.oid)) AND p.nspowner = 'pg_database_owner'::regrole AND p.oid = $3 AND d.oid = $4
                      AND r.rolcanlogin AND NOT (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls)
                      AND NOT EXISTS (SELECT 1 FROM pg_auth_members WHERE member = r.oid)
                      AND (SELECT count(*) FROM pg_class c JOIN pg_namespace s ON s.oid = c.relnamespace
                        WHERE s.nspname = 'migration_owned' AND c.relkind IN ('r','i','S') AND c.relowner = r.oid) >= 5
                      AND NOT EXISTS (SELECT 1 FROM pg_proc f JOIN pg_namespace s ON s.oid = f.pronamespace,
                        aclexplode(COALESCE(f.proacl, acldefault('f',f.proowner))) a
                        WHERE s.nspname IN ($2,'additional_owned') AND a.grantee = 0)
                      AND NOT EXISTS (SELECT 1 FROM pg_type t JOIN pg_namespace s ON s.oid = t.typnamespace,
                        aclexplode(COALESCE(t.typacl, acldefault('T',t.typowner))) a
                        WHERE s.nspname IN ($2,'additional_owned') AND t.typname = 'future_type' AND a.grantee = 0)
                      AND NOT EXISTS (SELECT 1 FROM aclexplode(COALESCE(d.datacl, acldefault('d',d.datdba))) a WHERE a.grantee = 0)
                      AND NOT EXISTS (SELECT 1 FROM aclexplode(COALESCE(p.nspacl, acldefault('n',p.nspowner))) a WHERE a.grantee = 0)
                      AND (SELECT relowner FROM pg_class WHERE oid = 'public.administrator_object'::regclass) <> r.oid
                      AND (SELECT relowner FROM pg_class WHERE oid = ($2 || '.qualified_item')::regclass) = r.oid
                      AND (SELECT relowner FROM pg_class WHERE oid = ($2 || '.unqualified_item')::regclass) = r.oid
                      AND (SELECT nspowner FROM pg_namespace WHERE nspname = 'administrator_schema') <> r.oid
                    FROM pg_database d CROSS JOIN pg_roles r CROSS JOIN pg_namespace n CROSS JOIN pg_namespace p
                    WHERE d.datname = current_database() AND r.rolname = $1 AND n.nspname = $2 AND p.nspname = 'public'
                    """, admin);
                evidence.Parameters.AddWithValue(scenario.Role); evidence.Parameters.AddWithValue(schema);
                evidence.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, created.PublicSchemaOid);
                evidence.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, created.DatabaseOid);
                Assert.True(await evidence.ExecuteScalarAsync() is true);
            }
            Assert.Equal(unrelated, await UnrelatedPolicy());
            await AssertComplete(scenario);
            // New composition and generation retry skip every successful node, including the final DDL.
            var trap = new AccessFault("trap");
            await using (var restart = OwnerServices(scenario, trap))
                Assert.Null((await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default)).StopOutcome);
            Assert.Equal(0, trap.Markers);
            Assert.Equal(output, await ConnectionOutput(scenario));
            Assert.Equal(reference, await Password(scenario));
            await AssertComplete(scenario);
        }
        finally { Array.Clear(bytes); }
    }

    [Theory]
    [InlineData("owner-rollback")]
    [InlineData("schema-rollback")]
    [InlineData("defaults-rollback")]
    [InlineData("rollback-unknown")]
    [InlineData("commit")]
    [InlineData("verification")]
    [InlineData("authentication")]
    [InlineData("finalization")]
    [InlineData("cancel")]
    public async Task Final_access_rollback_or_uncertainty_never_repairs_or_replays(string mode)
    {
        var scenario = await Prepare(full: true); await AccessReady(scenario);
        var before = await OwnershipAndObjects(scenario.Database); var policy = await Policy(scenario.Database);
        var reference = await Password(scenario);
        using var cancel = new CancellationTokenSource(); var fault = new AccessFault(mode, cancel);
        var definitive = mode is "owner-rollback" or "schema-rollback" or "defaults-rollback";
        var expected = definitive ? DurableExecutionOutcome.Failed : DurableExecutionOutcome.NeedsAttention;
        Assert.Equal(expected, (await AccessExecute(scenario, fault: fault, token: cancel.Token)).Outcome);
        if (definitive || mode is "rollback-unknown" or "cancel")
        {
            Assert.Equal(before, await OwnershipAndObjects(scenario.Database));
            Assert.Equal(policy, await Policy(scenario.Database));
        }
        else Assert.NotEqual(before, await OwnershipAndObjects(scenario.Database));
        Assert.Equal(expected, (await AccessExecute(scenario, fault: fault)).Outcome);
        Assert.Equal(1, fault.Markers);
        Assert.Equal(reference, await Password(scenario));
        await using var verify = Context(scenario.Connection);
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Equal(4, await verify.DurableOperations.CountAsync(r => r.State == "Succeeded"));
        Assert.False(await verify.OperationOutputs.AnyAsync(r => r.OperationId == "app-login-access"));
        Assert.NotEqual("Succeeded", (await verify.DurableOperationAttempts.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-access")).Outcome);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("attempt")]
    [InlineData("aggregate")]
    public async Task Final_catalog_failure_preserves_only_independently_confirmed_results(string mode)
    {
        var scenario = await Prepare(full: true); await AccessReady(scenario);
        var table = mode == "output" ? "operation_outputs" : mode == "attempt" ? "durable_create_operation_attempts" : "application_deployments";
        var predicate = mode == "output" ? "operation_id <> 'app-login-access'" : mode == "attempt" ? "operation_id <> 'app-login-access' OR outcome IS DISTINCT FROM 'Succeeded'" : "applied_generation IS NULL";
        await Sql(scenario.Connection, $"ALTER TABLE {table} ADD CONSTRAINT access_failure CHECK ({predicate})");
        var before = await OwnershipAndObjects(scenario.Database); var fault = new AccessFault("none");
        var result = await AccessExecute(scenario, fault: fault);
        Assert.NotEqual(before, await OwnershipAndObjects(scenario.Database));
        if (mode == "aggregate")
        {
            Assert.Equal(DurableExecutionOutcome.Succeeded, result.Outcome);
            string payload; Guid attemptId; int attempts;
            await using (var verify = Context(scenario.Connection))
            {
                payload = (await verify.OperationOutputs.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-access")).Payload;
                var operation = await verify.DurableOperations.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-access");
                Assert.Equal("Succeeded", operation.State);
                attemptId = operation.CurrentAttemptId!.Value;
                Assert.Equal("Succeeded", (await verify.DurableOperationAttempts.AsNoTracking().SingleAsync(r => r.AttemptId == attemptId)).Outcome);
                attempts = await verify.DurableOperationAttempts.CountAsync();
                Assert.Empty(await verify.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
                Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            }
            await Sql(scenario.Connection, "ALTER TABLE application_deployments DROP CONSTRAINT access_failure");
            await using (var restart = OwnerServices(scenario, fault))
                Assert.Null((await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default)).StopOutcome);
            Assert.Equal(1, fault.Markers);
            await using var final = Context(scenario.Connection);
            Assert.Equal(payload, (await final.OperationOutputs.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-access")).Payload);
            Assert.Equal(attemptId, (await final.DurableOperations.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-access")).CurrentAttemptId);
            Assert.Equal(attempts, await final.DurableOperationAttempts.CountAsync());
            Assert.Single(await final.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
            Assert.Equal(1, (await final.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            return;
        }
        Assert.Contains(result.Outcome, new[] { DurableExecutionOutcome.NeedsAttention, DurableExecutionOutcome.DispatchUncertain });
        await using (var verify = Context(scenario.Connection))
        {
            Assert.False(await verify.OperationOutputs.AnyAsync(r => r.OperationId == "app-login-access"));
            Assert.NotEqual("Succeeded", (await verify.DurableOperations.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-access")).State);
            Assert.NotEqual("Succeeded", (await verify.DurableOperationAttempts.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-access")).Outcome);
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        await Sql(scenario.Connection, $"ALTER TABLE {table} DROP CONSTRAINT access_failure");
        Assert.Equal(DurableExecutionOutcome.NeedsAttention, (await AccessExecute(scenario, fault: fault)).Outcome);
        Assert.Equal(1, fault.Markers);
    }

}
