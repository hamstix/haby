using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Haby.PostgreSql.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    private static async Task Sql(string connection, string sql)
    {
        await using var session = new NpgsqlConnection(connection); await session.OpenAsync();
        await new NpgsqlCommand(sql, session).ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("output")]
    [InlineData("attempt")]
    [InlineData("aggregate")]
    public async Task Role_publication_failure_rolls_back_output_success_exact_attempt_and_aggregate(string mode)
    {
        var scenario = await Prepare(); await Ready(scenario);
        var table = mode == "output" ? "operation_outputs" : mode == "attempt" ? "durable_create_operation_attempts" : "application_deployments";
        var predicate = mode == "output" ? "operation_id <> 'app-login-create'" : mode == "attempt" ? "operation_id <> 'app-login-create' OR outcome IS NULL" : "applied_generation IS NULL";
        await Sql(scenario.Connection, $"ALTER TABLE {table} ADD CONSTRAINT login_failure CHECK ({predicate})");
        var fault = new Fault("none");
        var result = await Execute(scenario, "app-login-create", fault);
        Assert.Contains(result.Outcome, new[] { DurableExecutionOutcome.NeedsAttention, DurableExecutionOutcome.DispatchUncertain });
        Assert.True(await Exists(scenario.Role));
        await using (var verify = Context(scenario.Connection))
        {
            Assert.False(await verify.OperationOutputs.AnyAsync(r => r.OperationId == "app-login-create"));
            Assert.NotEqual("Succeeded", (await verify.DurableOperations.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-create")).State);
            Assert.NotEqual("Succeeded", (await verify.DurableOperationAttempts.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-create")).Outcome);
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        await Sql(scenario.Connection, $"ALTER TABLE {table} DROP CONSTRAINT login_failure");
        Assert.Equal(DurableExecutionOutcome.NeedsAttention, (await Execute(scenario, "app-login-create", fault)).Outcome);
        Assert.Equal(1, fault.Markers);
    }

    [Fact]
    public async Task Role_output_is_mandatory_write_once_and_exact_attempt_scoped()
    {
        var scenario = await Prepare(); await Ready(scenario);
        await using var services = Services(scenario.Connection);
        var journal = services.GetRequiredService<IDurableOperationJournal>();
        var begun = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(scenario.Key("app-login-create"), DateTimeOffset.UtcNow, default));
        await journal.TryCommitDispatchMarkerAsync(scenario.Key("app-login-create"), begun.AttemptId, DateTimeOffset.UtcNow, default);
        Assert.False(await journal.TryRecordSuccessAsync(scenario.Key("app-login-create"), begun.AttemptId, DateTimeOffset.UtcNow, default));
        // No evidence is inferred from role existence. This fixture stops before any password DDL.
        var outputs = services.GetRequiredService<IExternalOperationOutputs>();
        Assert.False(await outputs.CompleteAsync(scenario.Key("app-login-create"), Guid.NewGuid(),
            PostgreSqlRoleEvidenceContract.Encode(new(scenario.Role, 1)), DateTimeOffset.UtcNow, default));
        Assert.False(await Exists(scenario.Role));
    }

    [Fact]
    public async Task Confirmed_role_output_refuses_conflicting_evidence_and_wrong_attempt_after_restart()
    {
        var scenario = await Prepare(); await Ready(scenario);
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, "app-login-create")).Outcome);
        await using var services = Services(scenario.Connection);
        var outputs = services.GetRequiredService<IExternalOperationOutputs>();
        var journal = services.GetRequiredService<IDurableOperationJournal>();
        var attempt = Assert.Single(await journal.FindAttemptsAsync(scenario.Key("app-login-create"), default)).AttemptId;
        await using var verify = Context(scenario.Connection);
        var row = await verify.OperationOutputs.AsNoTracking().SingleAsync(r => r.OperationId == "app-login-create");
        var saved = new ExternalOperationOutput(row.Contract, row.Key, row.Payload);
        Assert.True(await outputs.CompleteAsync(scenario.Key("app-login-create"), attempt, saved, DateTimeOffset.UtcNow, default));
        Assert.False(await outputs.CompleteAsync(scenario.Key("app-login-create"), Guid.NewGuid(), saved, DateTimeOffset.UtcNow, default));
        Assert.False(await outputs.CompleteAsync(scenario.Key("app-login-create"), attempt,
            PostgreSqlRoleEvidenceContract.Encode(new(scenario.Role, 1)), DateTimeOffset.UtcNow, default));
    }

    [Theory]
    [InlineData("database-missing")]
    [InlineData("database-uncertain")]
    [InlineData("database-name")]
    [InlineData("database-oid")]
    [InlineData("restrict")]
    [InlineData("profile")]
    [InlineData("audit")]
    [InlineData("payload")]
    [InlineData("owner")]
    [InlineData("version")]
    [InlineData("provider-secret")]
    public async Task Invalid_exact_evidence_or_material_refuses_before_login_dispatch(string mode)
    {
        var scenario = await Prepare(); await Ready(scenario);
        var sql = mode switch
        {
            "database-missing" => "DELETE FROM operation_outputs WHERE operation_id = 'db-create'",
            "database-uncertain" => "UPDATE durable_create_operation_attempts SET outcome = 'Uncertain' WHERE operation_id = 'db-create'",
            "database-name" => "UPDATE operation_outputs SET payload = '{\"databaseName\":\"wrong\",\"databaseOid\":1}' WHERE operation_id = 'db-create'",
            "database-oid" => "UPDATE operation_outputs SET payload = jsonb_set(payload::jsonb, '{databaseOid}', '1')::text WHERE operation_id = 'db-create'",
            "restrict" => "UPDATE durable_create_operations SET state = 'NeedsAttention' WHERE operation_id = 'db-restrict'",
            "profile" => "ALTER TABLE generated_credential_slots DROP CONSTRAINT ck_generated_slot_shape; UPDATE generated_credential_slots SET generation_profile = 'unsupported/v1'",
            "audit" => "DELETE FROM generated_credential_creation_audits",
            "payload" => "DELETE FROM secret_payloads WHERE secret_version_id IN (SELECT version_id FROM generated_credential_slots)",
            "owner" => "UPDATE generated_credential_slots SET resource_local_id = 'different-login'",
            "version" => "UPDATE operation_outputs SET payload = replace(payload, (SELECT version_id::text FROM generated_credential_slots), '00000000-0000-7000-8000-000000000001') WHERE operation_id = 'app-login-material'",
            _ => "DELETE FROM secret_payloads WHERE secret_version_id NOT IN (SELECT version_id FROM generated_credential_slots)"
        };
        // Deliberate corruption only in this test-owned disposable catalog session.
        await Sql(scenario.Connection, "SET session_replication_role = replica; " + sql);
        var fault = new Fault("trap"); var result = await Execute(scenario, "app-login-create", fault);
        Assert.NotEqual(DurableExecutionOutcome.Succeeded, result.Outcome); Assert.Equal(0, fault.Markers);
        Assert.False(await Exists(scenario.Role));
        await using var verify = Context(scenario.Connection);
        Assert.False(await verify.OperationOutputs.AnyAsync(r => r.OperationId == "app-login-create"));
        Assert.False(await verify.DurableOperationAttempts.AnyAsync(r => r.OperationId == "app-login-create" && r.DispatchedAt != null));
    }

    [Fact]
    public async Task Provider_scoped_reservations_converge_retain_names_and_refuse_a_competing_owner()
    {
        var scenario = await Prepare();
        async Task<bool> Reserve(ProviderInstanceId provider, ManagedResourceKey owner)
        {
            await using var services = Services(scenario.Connection);
            return await services.GetRequiredService<IPostgreSqlRoleNames>().ReserveAsync(provider, owner, default);
        }
        var provider = scenario.Plan.Inputs.ProviderInstance;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Reserve(provider, scenario.Owner)));
        Assert.All(results, Assert.True);
        Assert.True(await Reserve(ProviderInstanceId.New(), scenario.Owner));
        // Simulate a hash-name collision using privileged fixture corruption, without adding a
        // production name override/random generator. The retained association cannot be reassigned.
        await Sql(scenario.Connection, "UPDATE postgresql_role_names SET \"Resource\" = 'other-login'");
        Assert.False(await Reserve(provider, scenario.Owner));
        Assert.False(await Exists(scenario.Role));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_dump_restore_preserves_reservation_exact_password_output_and_nonreplayable_state(bool uncertain)
    {
        var scenario = await Prepare(); await Ready(scenario);
        var reference = await Password(scenario); var bytes = await Material(scenario, reference);
        try
        {
            var expected = uncertain ? DurableExecutionOutcome.NeedsAttention : DurableExecutionOutcome.Succeeded;
            Assert.Equal(expected, (await Execute(scenario, "app-login-create", uncertain ? new Fault("commit") : null)).Outcome);
            var restoredConnection = await Database("login_restore_"); var dump = "/tmp/login-" + Guid.NewGuid().ToString("N") + ".dump";
            try
            {
                await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + new NpgsqlConnectionStringBuilder(scenario.Connection).Database);
                await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + new NpgsqlConnectionStringBuilder(restoredConnection).Database, dump);
                var restored = scenario with { Connection = restoredConnection };
                await using (var services = Services(restored.Connection))
                {
                    await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
                    await services.GetRequiredService<PostgreSqlLoginCatalogInitializer>().MigrateAsync();
                    Assert.True(await services.GetRequiredService<IPostgreSqlRoleNames>().ReserveAsync(restored.Plan.Inputs.ProviderInstance, restored.Owner, default));
                }
                Assert.Equal(expected, (await Execute(restored, "app-login-create", new Fault("trap"))).Outcome);
                Assert.Equal(reference, await Password(restored));
                var material = await Material(restored, reference); try { Assert.True(bytes.SequenceEqual(material)); } finally { Array.Clear(material); }
                await AssertLogin(restored, bytes);
                await using var source = Context(scenario.Connection); await using var target = Context(restored.Connection);
                Assert.Equal(await source.OperationOutputs.AsNoTracking().OrderBy(r => r.OperationId).Select(r => r.Payload).ToArrayAsync(),
                    await target.OperationOutputs.AsNoTracking().OrderBy(r => r.OperationId).Select(r => r.Payload).ToArrayAsync());
                Assert.Equal(4, await target.DurableOperationAttempts.CountAsync());
            }
            finally { await fixture.RunClientUtilityAsync("rm", "-f", dump); }
        }
        finally { Array.Clear(bytes); }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(Messages);
        public void Dispose() { }
        private sealed class Sink(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class SqlEvents : EventListener
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Npgsql.Sql") EnableEvents(eventSource, EventLevel.Verbose);
        }
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.Payload is not null)
                Messages.Enqueue(string.Join(" ", eventData.Payload));
        }
    }

    private sealed class NameRace(string connection, string role) : IPostgreSqlLoginFaultHooks
    {
        public async ValueTask BeforeRoleAsync(NpgsqlConnection _, NpgsqlTransaction transaction, CancellationToken token)
        {
            // A different acknowledged administrator creates the competing role after the
            // absence check. PostgreSQL's unique role name must arbitrate without overwrite.
            using var quote = new NpgsqlCommandBuilder();
            await Sql(connection, "CREATE ROLE " + quote.QuoteIdentifier(role) + " NOLOGIN");
        }
    }

    private sealed class ExternalDrift(string connection, string role, bool authentication) : IPostgreSqlLoginFaultHooks
    {
        private async ValueTask Change()
        {
            using var quote = new NpgsqlCommandBuilder();
            await Sql(connection, "ALTER ROLE " + quote.QuoteIdentifier(role) + (authentication ? " NOLOGIN" : " CREATEDB"));
        }
        public ValueTask BeforeVerificationAsync(CancellationToken token) => authentication ? ValueTask.CompletedTask : Change();
        public ValueTask BeforeAuthenticationAsync(CancellationToken token) => authentication ? Change() : ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_failed_verification_or_authentication_retains_uncertainty_without_repair(bool authentication)
    {
        var scenario = await Prepare(); await Ready(scenario);
        Assert.Equal(DurableExecutionOutcome.NeedsAttention, (await Execute(scenario, "app-login-create",
            new ExternalDrift(fixture.ConnectionString, scenario.Role, authentication))).Outcome);
        Assert.Equal(DurableExecutionOutcome.NeedsAttention, (await Execute(scenario, "app-login-create", new Fault("trap"))).Outcome);
        await using var verify = Context(scenario.Connection);
        Assert.False(await verify.OperationOutputs.AnyAsync(r => r.OperationId == "app-login-create"));
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Single(await verify.DurableOperationAttempts.AsNoTracking().Where(r => r.OperationId == "app-login-create").ToArrayAsync());
    }

    [Fact]
    public async Task Current_alias_revision_cannot_replace_the_saved_provider_or_administrator_secret()
    {
        var scenario = await Prepare(); await Ready(scenario);
        await using (var services = Services(scenario.Connection))
        {
            var providers = services.GetRequiredService<IProviderConfigurationStore>();
            var inputs = scenario.Plan.Inputs;
            var saved = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(inputs.Environment,
                inputs.ProviderInstance, inputs.ConfigurationRevision, default))!.Configuration;
            var next = ProviderConfigurationRevisionNumber.Create(2);
            var alternateName = "alternate_admin_" + Guid.NewGuid().ToString("N");
            var alternateSecret = new SecretRef(SecretId.New(), SecretVersionId.New());
            var bytes = await Material(scenario, await Password(scenario));
            try
            {
                await services.GetRequiredService<ISecretStore>().CreateAsync(new(new SecretOwner.Environment(inputs.Environment),
                    SecretPurpose.ProviderAdministrator), alternateSecret, bytes, default);
                using var quote = new NpgsqlCommandBuilder();
                await Sql(fixture.ConnectionString, "CREATE ROLE " + quote.QuoteIdentifier(alternateName) +
                    " LOGIN SUPERUSER PASSWORD '" + PostgreSqlScramVerifier.Create(bytes) + "'");
            }
            finally { Array.Clear(bytes); }
            var alternate = new PostgreSqlProviderConfiguration(1, saved.Host, saved.Port, saved.MaintenanceDatabase,
                alternateName, alternateSecret, saved.ImplementationId);
            await providers.AddRevisionAsync(inputs.Environment, inputs.ProviderInstance, next, alternate, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(
                inputs.Environment, inputs.Alias, new(inputs.ProviderInstance, inputs.ConfigurationRevision),
                new(inputs.ProviderInstance, next), "tester", default)).Outcome);
        }
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, "app-login-create")).Outcome);
    }

    [Fact]
    public async Task Unsupported_server_extension_refuses_password_DDL_before_the_dispatch_marker()
    {
        var scenario = await Prepare(); await Ready(scenario);
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = scenario.Database }.ConnectionString;
        await Sql(connection, "CREATE EXTENSION hstore");
        var fault = new Fault("trap");
        Assert.Equal(DurableExecutionOutcome.RetryableFailure, (await Execute(scenario, "app-login-create", fault)).Outcome);
        Assert.Equal(0, fault.Markers); Assert.False(await Exists(scenario.Role));
    }

    [Fact]
    public async Task Existing_unmanaged_role_keeps_its_password_flags_memberships_and_unrelated_ACLs()
    {
        var scenario = await Prepare(); await Ready(scenario);
        var bytes = await Material(scenario, await Password(scenario));
        try
        {
            var verifier = PostgreSqlScramVerifier.Create(bytes);
            using var quote = new NpgsqlCommandBuilder();
            await Sql(fixture.ConnectionString, "CREATE ROLE " + quote.QuoteIdentifier(scenario.Role) +
                " LOGIN CREATEDB PASSWORD '" + verifier + "'; GRANT pg_monitor TO " + quote.QuoteIdentifier(scenario.Role));
            var unrelated = await UnrelatedPolicy(); var database = await Policy(scenario.Database);
            async Task<string> Evidence()
            {
                await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT json_build_array(r.oid, r.rolcanlogin, r.rolcreatedb, md5(r.rolpassword),
                      (SELECT json_agg(roleid ORDER BY roleid) FROM pg_auth_members WHERE member = r.oid))::text
                    FROM pg_authid r WHERE rolname = $1
                    """, connection);
                command.Parameters.AddWithValue(scenario.Role);
                return (string)(await command.ExecuteScalarAsync())!;
            }
            var before = await Evidence();
            Assert.Equal(DurableExecutionOutcome.Failed, (await Execute(scenario, "app-login-create")).Outcome);
            // Value-free assertions: even an existing verifier digest stays out of diagnostics.
            Assert.True(before == await Evidence());
            Assert.Equal(database, await Policy(scenario.Database)); Assert.Equal(unrelated, await UnrelatedPolicy());
            await using var verify = Context(scenario.Connection);
            Assert.False(await verify.DurableOperationAttempts.AnyAsync(a => a.OperationId == "app-login-create" && a.DispatchedAt != null));
            Assert.False(await verify.OperationOutputs.AnyAsync(o => o.OperationId == "app-login-create"));
        }
        finally { Array.Clear(bytes); }
    }

    [Fact]
    public async Task Concurrent_execution_creates_one_role_and_reuses_one_saved_password_reservation_and_attempt()
    {
        var scenario = await Prepare(); await Ready(scenario);
        var reference = await Password(scenario);
        await using var services = Services(scenario.Connection);
        var executor = services.GetRequiredService<IDurableOperationExecutor>();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => executor.ExecuteAsync(scenario.Key("app-login-create"), default).AsTask()));
        Assert.All(results, r => Assert.Equal(DurableExecutionOutcome.Succeeded, r.Outcome));
        Assert.Equal(reference, await Password(scenario));
        await using var verify = Context(scenario.Connection);
        Assert.Single(await verify.DurableOperationAttempts.AsNoTracking().Where(a => a.OperationId == "app-login-create").ToArrayAsync());
        Assert.Single(await verify.OperationOutputs.AsNoTracking().Where(a => a.OperationId == "app-login-create").ToArrayAsync());
    }

    [Theory]
    [InlineData("success")]
    [InlineData("rollback")]
    [InlineData("collision")]
    public async Task Captured_client_trace_and_configured_server_logs_never_contain_password_or_verifier(string mode)
    {
        var scenario = await Prepare(); await Ready(scenario); var bytes = await Material(scenario, await Password(scenario));
        using var provider = new CapturedLogs();
        using var sqlEvents = new SqlEvents();
        using var logger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        var traces = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => traces.Enqueue(string.Join(" ", a.TagObjects.Select(t => t.Key + "=" + t.Value)))
        };
        ActivitySource.AddActivityListener(listener);
        NpgsqlLoggingConfiguration.InitializeLogging(logger, parameterLoggingEnabled: true);
        try
        {
            // Strengthen ordinary SQL logging for this disposable server; dedicated password
            // sessions must override it locally. Restore it before other test classes run.
            await fixture.RunClientUtilityAsync("psql", "-U", "postgres", "-c", "ALTER SYSTEM SET log_statement = 'all'");
            await fixture.RunClientUtilityAsync("psql", "-U", "postgres", "-c", "SELECT pg_reload_conf()");
            await Sql(fixture.ConnectionString, "SELECT 'login_logging_control'");
            Assert.Equal(mode == "success" ? DurableExecutionOutcome.Succeeded : DurableExecutionOutcome.Failed,
                (await Execute(scenario, "app-login-create", mode == "success" ? null : mode == "collision"
                    ? new NameRace(fixture.ConnectionString, scenario.Role) : new Fault("rollback"))).Outcome);
            if (mode == "collision")
            {
                await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
                await using var read = new NpgsqlCommand("SELECT NOT rolcanlogin FROM pg_roles WHERE rolname = $1", connection);
                read.Parameters.AddWithValue(scenario.Role); Assert.True(await read.ExecuteScalarAsync() is true);
            }
            var server = await fixture.ReadServerLogsAsync();
            Assert.True(server.Contains("login_logging_control", StringComparison.Ordinal));
            var controlCaptured = sqlEvents.Messages.Any(m => m.Contains("login_logging_control", StringComparison.Ordinal));
            Assert.True(controlCaptured); // Value-free: never dump captured SQL on failure.
            var captured = server + string.Join("\n", provider.Messages) + string.Join("\n", traces) + string.Join("\n", sqlEvents.Messages);
            Assert.False(captured.Contains(Encoding.ASCII.GetString(bytes), StringComparison.Ordinal));
            Assert.False(captured.Contains("SCRAM-SHA-256$", StringComparison.Ordinal));
        }
        finally
        {
            NpgsqlLoggingConfiguration.InitializeLogging(NullLoggerFactory.Instance, parameterLoggingEnabled: false);
            Array.Clear(bytes);
            await fixture.RunClientUtilityAsync("psql", "-U", "postgres", "-c", "ALTER SYSTEM RESET log_statement");
            await fixture.RunClientUtilityAsync("psql", "-U", "postgres", "-c", "SELECT pg_reload_conf()");
        }
    }
}
