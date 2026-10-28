using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

/// <summary>
/// The dispatch-barrier crash and cancellation windows against a disposable real PostgreSQL.
/// Every fault is deterministic (hooks fire at named durable boundaries), arrange, act and assert
/// use separate connections, and no test deletes a database.
/// </summary>
public sealed class DurableCreateExecutionTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Implementation = PostgreSqlDatabaseCheckpointCapability.ImplementationId;
    private const string AnyObject = """{"type":"object"}""";
    private static readonly ManifestContractCatalog Catalog = new(
        [new(ResourceTypeId.Create("postgresql.database/v1alpha1"), AnyObject, AnyObject)],
        [], [], []);

    [Fact]
    public async Task Explicit_assignment_of_independently_created_database_is_atomic_and_idempotent()
    {
        var prepared = await PrepareExecutionAsync("haby_recover_ok");
        var attemptId = await MakeUncertainAsync(prepared);
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{prepared.Database}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        // The pinned login can read pg_database but cannot create anything during assignment.
        await AlterRoleAsync(prepared.User, "NOCREATEDB");
        PostgreSqlDatabaseRecoveryReceipt receipt;
        await using (var services = Services(prepared.Connection, prepared.Time))
        {
            var recovery = services.GetRequiredService<IPostgreSqlDatabaseRecovery>();
            var preview = await recovery.InspectAsync(prepared.Key, default);
            Assert.NotNull(preview);
            Assert.Equal(prepared.Database, preview.DatabaseName);
            Assert.Equal(prepared.Binding.InstanceId, preview.ProviderInstance);
            Assert.Equal(attemptId, preview.UncertainAttemptId);
            Assert.Equal(DurableCreateOperationState.NeedsAttention, preview.State);
            Assert.Null(await ScalarAsync(prepared.Connection,
                "SELECT applied_generation FROM application_deployments"));

            var command = Assignment(prepared, attemptId);
            // PostgreSQL stores microseconds. The first receipt must equal a later persisted read
            // even when the server clock has sub-microsecond ticks.
            prepared.Time.Advance(TimeSpan.FromTicks(1));
            var result = await recovery.AssignExistingDatabaseAsync(command, default);
            Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.Assigned, result.Outcome);
            receipt = Assert.IsType<PostgreSqlDatabaseRecoveryReceipt>(result.Decision);
            Assert.Equal(attemptId, receipt.AttemptId);
            Assert.DoesNotContain("operator note", result.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal("Succeeded", await ScalarAsync(prepared.Connection,
            "SELECT state FROM durable_create_operations"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(["Uncertain"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM postgresql_database_recovery_decisions"));
        await using (var restarted = Services(prepared.Connection, prepared.Time))
        {
            var recovery = restarted.GetRequiredService<IPostgreSqlDatabaseRecovery>();
            await AlterRoleAsync(prepared.User, "NOLOGIN");
            var retry = await recovery.AssignExistingDatabaseAsync(Assignment(prepared, attemptId), default);
            Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.AlreadyAssigned, retry.Outcome);
            Assert.Equal(receipt, retry.Decision);
            var audit = await restarted.GetRequiredService<IPostgreSqlDatabaseRecoveryJournal>()
                .FindDecisionAsync(prepared.Key, attemptId, default);
            Assert.Equal("operator note", audit!.Note);
            Assert.True(audit.Acknowledged);
            Assert.Equal(nameof(PostgreSqlDatabaseRecoveryAudit), audit.ToString());
        }
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM postgresql_database_recovery_decisions"));
        await AssertRejectedAsync(prepared.Connection,
            "UPDATE postgresql_database_recovery_decisions SET note = 'rewritten'");
        await AssertRejectedAsync(prepared.Connection,
            "DELETE FROM postgresql_database_recovery_decisions");
        await AssertRejectedAsync(prepared.Connection,
            "UPDATE durable_create_operations SET state = 'Pending'");
    }

    [Fact]
    public async Task Existing_uncertain_attempt_survives_upgrade_from_merged_migration_history()
    {
        var prepared = await PrepareExecutionAsync("haby_recover_upgrade", migrateToPrevious: true);
        var attemptId = await MakeUncertainAsync(prepared);
        var before = await RecoveryRowsBeforeMigrationAsync(prepared.Connection);
        await using (var upgrade = Services(prepared.Connection, prepared.Time))
            await upgrade.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        Assert.Equal(before, await RecoveryRowsBeforeMigrationAsync(prepared.Connection));
        await CreateIndependentDatabaseAsync(prepared.Database);
        await using var restarted = Services(prepared.Connection, prepared.Time);
        var assigned = await restarted.GetRequiredService<IPostgreSqlDatabaseRecovery>()
            .AssignExistingDatabaseAsync(Assignment(prepared, attemptId), default);
        Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.Assigned, assigned.Outcome);
        Assert.Equal("Succeeded", await ScalarAsync(prepared.Connection,
            "SELECT state FROM durable_create_operations"));
        Assert.Equal(["Uncertain"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM postgresql_database_recovery_decisions"));
    }

    [Fact]
    public async Task Recovery_audit_survives_later_applied_generation()
    {
        var prepared = await PrepareExecutionAsync("haby_recover_next");
        var attemptId = await MakeUncertainAsync(prepared);
        await CreateIndependentDatabaseAsync(prepared.Database);
        await using (var services = Services(prepared.Connection, prepared.Time))
            Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.Assigned,
                (await services.GetRequiredService<IPostgreSqlDatabaseRecovery>()
                    .AssignExistingDatabaseAsync(Assignment(prepared, attemptId), default)).Outcome);

        // The pilot currently admits only generation 1. Relax that pilot-only CHECK in this
        // disposable catalog to exercise ADR-016's later-generation pointer semantics.
        await ExecuteSqlAsync(prepared.Connection, """
            ALTER TABLE application_deployments
                DROP CONSTRAINT "CK_application_deployments_desired_generation";
            ALTER TABLE application_deployment_generations
                DROP CONSTRAINT "CK_deployment_generations_first";
            INSERT INTO application_deployment_generations
                (deployment_id, generation, presence, plan_id, change_set_id,
                 application_record_id, revision_number, environment_record_id,
                 environment_key, provider_alias, provider_instance_id,
                 configuration_revision_number, implementation_id, fingerprint,
                 accepted_at, actor_id)
            SELECT deployment_id, 2, presence, plan_id, change_set_id,
                   application_record_id, revision_number, environment_record_id,
                   environment_key, provider_alias, provider_instance_id,
                   configuration_revision_number, implementation_id, fingerprint,
                   now(), actor_id
            FROM application_deployment_generations WHERE generation = 1;
            UPDATE application_deployments
                SET desired_generation = 2, applied_generation = 2,
                    concurrency_token = concurrency_token + 1;
            """);
        Assert.Equal(2L, await ScalarLongAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT generation FROM postgresql_database_recovery_decisions"));
    }

    [Fact]
    public async Task Rejected_assignments_leave_operation_attempt_audit_and_applied_generation_unchanged()
    {
        var prepared = await PrepareExecutionAsync("haby_recover_no");
        var attemptId = await MakeUncertainAsync(prepared);
        var before = await RecoveryRowsAsync(prepared.Connection);
        await using var services = Services(prepared.Connection, prepared.Time);
        var recovery = services.GetRequiredService<IPostgreSqlDatabaseRecovery>();
        var command = Assignment(prepared, attemptId);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await recovery.AssignExistingDatabaseAsync(command with { Acknowledged = false }, default));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await recovery.AssignExistingDatabaseAsync(command with { Note = " " }, default));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await recovery.AssignExistingDatabaseAsync(command with { Note = new string('x', 513) }, default));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await recovery.AssignExistingDatabaseAsync(command with { Note = "password=hidden" }, default));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await recovery.AssignExistingDatabaseAsync(command with { Note = "bad\u202evalue" }, default));
        Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.Rejected,
            (await recovery.AssignExistingDatabaseAsync(command with { ExpectedAttemptId = Guid.NewGuid() },
                default)).Outcome);
        var wrongKey = prepared.Key with { ResourceId = ManifestLocalId.Create("other") };
        Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.Rejected,
            (await recovery.AssignExistingDatabaseAsync(command with { Key = wrongKey }, default)).Outcome);
        Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.DatabaseAbsent,
            (await recovery.AssignExistingDatabaseAsync(command, default)).Outcome);
        Assert.Equal(before, await RecoveryRowsAsync(prepared.Connection));

        await AlterRoleAsync(prepared.User, "NOLOGIN");
        Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.TargetUnavailable,
            (await recovery.AssignExistingDatabaseAsync(command, default)).Outcome);
        Assert.Equal(before, await RecoveryRowsAsync(prepared.Connection));
    }

    [Fact]
    public async Task Recovery_database_guard_rejects_unaudited_or_arbitrary_terminal_rewrites()
    {
        var prepared = await PrepareExecutionAsync("haby_recover_guard");
        await MakeUncertainAsync(prepared);
        var before = await RecoveryRowsAsync(prepared.Connection);
        await AssertRejectedAsync(prepared.Connection,
            "UPDATE durable_create_operations SET state = 'Succeeded', reason_code = NULL");
        await AssertRejectedAsync(prepared.Connection,
            "UPDATE durable_create_operations SET state = 'Pending', reason_code = NULL, finalized_at = NULL");
        await AssertRejectedAsync(prepared.Connection,
            "DELETE FROM durable_create_operations");
        await AssertRejectedAsync(prepared.Connection, """
            INSERT INTO postgresql_database_recovery_decisions
                (id, deployment_id, generation, resource_id, action, attempt_id, attempt_number,
                 reason, note, technical_actor, acknowledged, decided_at)
            SELECT gen_random_uuid(), o.deployment_id, o.generation, o.resource_id, o.action,
                   o.current_attempt_id, o.attempt_number, 'OperatorAcceptedExistingDatabase', 'checked',
                   'tester', true, now()
            FROM durable_create_operations o
            """, "23503");
        await AssertRejectedAsync(prepared.Connection, """
            BEGIN;
            INSERT INTO postgresql_database_recovery_decisions
                (id, deployment_id, generation, resource_id, action, attempt_id, attempt_number,
                 reason, note, technical_actor, acknowledged, decided_at)
            SELECT gen_random_uuid(), o.deployment_id, o.generation, o.resource_id, o.action,
                   o.current_attempt_id, o.attempt_number, 'OperatorAcceptedExistingDatabase',
                   'checked', 'tester', true, now()
            FROM durable_create_operations o;
            UPDATE durable_create_operations
                SET state = 'Succeeded', reason_code = NULL;
            COMMIT;
            """);
        Assert.Equal("NeedsAttention", await ScalarAsync(prepared.Connection,
            "SELECT state FROM durable_create_operations"));
        Assert.Null(await ScalarAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(0L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM postgresql_database_recovery_decisions"));
        Assert.Equal(before, await RecoveryRowsAsync(prepared.Connection));
    }

    [Fact]
    public async Task Concurrent_assignment_commits_one_decision_and_returns_the_same_receipt()
    {
        var prepared = await PrepareExecutionAsync("haby_recover_race");
        var attemptId = await MakeUncertainAsync(prepared);
        await CreateIndependentDatabaseAsync(prepared.Database);
        await using var first = Services(prepared.Connection, prepared.Time);
        await using var second = Services(prepared.Connection, prepared.Time);
        var command = Assignment(prepared, attemptId);
        var results = await Task.WhenAll(
            first.GetRequiredService<IPostgreSqlDatabaseRecovery>()
                .AssignExistingDatabaseAsync(command, default).AsTask(),
            second.GetRequiredService<IPostgreSqlDatabaseRecovery>()
                .AssignExistingDatabaseAsync(command, default).AsTask());
        Assert.Contains(results, result => result.Outcome == PostgreSqlDatabaseAssignmentOutcome.Assigned);
        Assert.All(results, result => Assert.NotNull(result.Decision));
        Assert.Equal(results[0].Decision, results[1].Decision);
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM postgresql_database_recovery_decisions"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(["Uncertain"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts"));
    }

    [Fact]
    public async Task Failed_final_write_rolls_back_audit_and_operation_transition()
    {
        var prepared = await PrepareExecutionAsync("haby_recover_rollback");
        var attemptId = await MakeUncertainAsync(prepared);
        await CreateIndependentDatabaseAsync(prepared.Database);
        var before = await RecoveryRowsAsync(prepared.Connection);
        await ExecuteSqlAsync(prepared.Connection, """
            CREATE FUNCTION fail_recovery_advance() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.applied_generation IS NOT NULL THEN
                    RAISE EXCEPTION 'injected advance failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER fail_recovery_advance_before_update BEFORE UPDATE ON application_deployments
                FOR EACH ROW EXECUTE FUNCTION fail_recovery_advance();
            """);
        try
        {
            await using var services = Services(prepared.Connection, prepared.Time);
            var recovery = services.GetRequiredService<IPostgreSqlDatabaseRecovery>();
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await recovery.AssignExistingDatabaseAsync(Assignment(prepared, attemptId), default));
            Assert.Equal(before, await RecoveryRowsAsync(prepared.Connection));
        }
        finally
        {
            await ExecuteSqlAsync(prepared.Connection, """
                DROP TRIGGER fail_recovery_advance_before_update ON application_deployments;
                DROP FUNCTION fail_recovery_advance();
                """);
        }
    }

    [Fact]
    public async Task Preexisting_database_conflict_and_wrong_environment_cannot_be_assigned()
    {
        var conflict = await PrepareExecutionAsync("haby_recover_conflict");
        await CreateIndependentDatabaseAsync(conflict.Database);
        await using (var services = Services(conflict.Connection, conflict.Time))
        {
            Assert.Equal(DurableCreateExecutionOutcome.Failed,
                (await ExecuteAsync(services, conflict.Key, default)).Outcome);
            var attemptId = (await services.GetRequiredService<IDurableCreateOperationJournal>()
                .FindAttemptsAsync(conflict.Key, default)).Single().AttemptId;
            var before = await RecoveryRowsAsync(conflict.Connection);
            var recovery = services.GetRequiredService<IPostgreSqlDatabaseRecovery>();
            Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.Rejected,
                (await recovery.AssignExistingDatabaseAsync(Assignment(conflict, attemptId), default)).Outcome);
            Assert.Equal(before, await RecoveryRowsAsync(conflict.Connection));
        }
        var uncertain = await PrepareExecutionAsync("haby_recover_env");
        var uncertainAttempt = await MakeUncertainAsync(uncertain);
        var initial = await RecoveryRowsAsync(uncertain.Connection);
        await using var restarted = Services(uncertain.Connection, uncertain.Time);
        var wrongEnvironment = Assignment(uncertain, uncertainAttempt) with
        {
            Environment = EnvironmentRecordId.Parse(Guid.CreateVersion7().ToString())
        };
        Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.Rejected,
            (await restarted.GetRequiredService<IPostgreSqlDatabaseRecovery>()
                .AssignExistingDatabaseAsync(wrongEnvironment, default)).Outcome);
        Assert.Equal(initial, await RecoveryRowsAsync(uncertain.Connection));
    }

    [Fact]
    public async Task Assignment_uses_pinned_provider_after_alias_switch()
    {
        var prepared = await PrepareExecutionAsync("haby_recover_pin");
        var attemptId = await MakeUncertainAsync(prepared);
        await CreateIndependentDatabaseAsync(prepared.Database);
        await using var services = Services(prepared.Connection, prepared.Time);
        var otherPassword = "haby-test-" + Guid.NewGuid().ToString("N");
        var otherUser = "haby_recover_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var role = new NpgsqlCommand(
                $"""CREATE ROLE "{otherUser}" LOGIN CREATEDB PASSWORD '{otherPassword}'""", admin);
            await role.ExecuteNonQueryAsync();
        }
        var secrets = services.GetRequiredService<ISecretStore>();
        var otherSecret = new SecretRef(SecretId.New(), SecretVersionId.New());
        await secrets.CreateAsync(prepared.Environment, otherSecret, Encoding.UTF8.GetBytes(otherPassword), default);
        var provider = services.GetRequiredService<IProviderConfigurationStore>();
        var adminTarget = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var other = new PostgreSqlProviderConfiguration(1, adminTarget.Host!, adminTarget.Port,
            adminTarget.Database!, otherUser, otherSecret, Implementation);
        var otherInstance = await provider.CreateInstanceAsync(prepared.Environment, other, default);
        await provider.AddRevisionAsync(prepared.Environment, otherInstance,
            ProviderConfigurationRevisionNumber.Create(1), other, default);
        Assert.Equal(ProviderAliasChangeOutcome.Changed,
            (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(prepared.Environment,
                ProviderAlias.Create("postgresql.primary"), prepared.Binding,
                new ProviderAliasBinding(otherInstance, ProviderConfigurationRevisionNumber.Create(1)),
                "tester", default)).Outcome);
        await AlterRoleAsync(otherUser, "NOLOGIN");
        await AlterRoleAsync(prepared.User, "NOCREATEDB");

        var result = await services.GetRequiredService<IPostgreSqlDatabaseRecovery>()
            .AssignExistingDatabaseAsync(Assignment(prepared, attemptId), default);
        Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.Assigned, result.Outcome);
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(["Uncertain"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts"));
    }

    private async Task CreateIndependentDatabaseAsync(string name)
    {
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
        await create.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteSqlAsync(string connection, string sql)
    {
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        await using var command = new NpgsqlCommand(sql, database);
        await command.ExecuteNonQueryAsync();
    }

    private static AssignExistingPostgreSqlDatabase Assignment(Prepared prepared, Guid attemptId) =>
        new(prepared.Key, prepared.Environment, attemptId, true,
            PostgreSqlRecoveryReason.OperatorAcceptedExistingDatabase, "operator note", "tester");

    private async Task<Guid> MakeUncertainAsync(Prepared prepared)
    {
        await using var services = Services(prepared.Connection, prepared.Time);
        var journal = services.GetRequiredService<IDurableCreateOperationJournal>();
        var started = Assert.IsType<DurableCreateBeginAttemptResult.Started>(
            await journal.TryBeginAttemptAsync(prepared.Key, prepared.Time.GetUtcNow(), default));
        Assert.IsType<DurableCreateMarkerResult.Committed>(
            await journal.TryCommitDispatchMarkerAsync(prepared.Key, started.AttemptId,
                prepared.Time.GetUtcNow(), default));
        Assert.True(await journal.TryRecoverDispatchingAsync(prepared.Key,
            prepared.Time.GetUtcNow(), default));
        return started.AttemptId;
    }

    private static async Task<string> RecoveryRowsAsync(string connection)
    {
        var operation = await ScalarAsync(connection,
            "SELECT row_to_json(o)::text FROM durable_create_operations o");
        var attempt = await ScalarAsync(connection,
            "SELECT row_to_json(a)::text FROM durable_create_operation_attempts a");
        var deployment = await ScalarAsync(connection,
            "SELECT row_to_json(d)::text FROM application_deployments d");
        var decisions = await ScalarLongAsync(connection,
            "SELECT count(*) FROM postgresql_database_recovery_decisions");
        return $"{operation}|{attempt}|{deployment}|{decisions}";
    }

    private static async Task<string> RecoveryRowsBeforeMigrationAsync(string connection)
    {
        var operation = await ScalarAsync(connection,
            "SELECT row_to_json(o)::text FROM durable_create_operations o");
        var attempt = await ScalarAsync(connection,
            "SELECT row_to_json(a)::text FROM durable_create_operation_attempts a");
        var deployment = await ScalarAsync(connection,
            "SELECT row_to_json(d)::text FROM application_deployments d");
        return $"{operation}|{attempt}|{deployment}";
    }

    [Fact]
    public async Task Success_advances_applied_generation_once_and_retry_sends_no_second_create()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_ok");
        await using (var services = Services(prepared.Connection, prepared.Time))
        {
            var result = await ExecuteAsync(services, prepared.Key, default);
            Assert.Equal(DurableCreateExecutionOutcome.Succeeded, result.Outcome);
            Assert.Equal(DurableCreateOperationState.Succeeded, result.Operation!.State);
            Assert.Null(result.Operation.Reason);
        }
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));
        Assert.Equal("Succeeded", await ScalarAsync(prepared.Connection,
            "SELECT state FROM durable_create_operations"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection, """
            SELECT count(*) FROM durable_create_operation_attempts
            WHERE outcome = 'Succeeded' AND dispatched_at IS NOT NULL AND finished_at IS NOT NULL
              AND attempt_number = 1
            """));
        Assert.Equal(0L, await ScalarLongAsync(prepared.Connection, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_name IN ('durable_create_operations', 'durable_create_operation_attempts')
              AND column_name ~ '(secret|password|payload|credential)'
            """));
        await using (var redaction = await OpenAsync(prepared.Connection))
        {
            await using var scan = new NpgsqlCommand("""
                SELECT (SELECT count(*) FROM durable_create_operations AS t
                        WHERE to_jsonb(t)::text LIKE '%' || @secret || '%')
                     + (SELECT count(*) FROM durable_create_operation_attempts AS t
                        WHERE to_jsonb(t)::text LIKE '%' || @secret || '%')
                """, redaction);
            scan.Parameters.AddWithValue("secret", prepared.Password);
            Assert.Equal(0L, (long)(await scan.ExecuteScalarAsync())!);
        }
        // A terminal operation is answered from the journal only: the login is revoked so any
        // second attempt at Provider SQL cannot succeed.
        await AlterRoleAsync(prepared.User, "NOLOGIN");
        await using (var restarted = Services(prepared.Connection, prepared.Time))
        {
            var retry = await ExecuteAsync(restarted, prepared.Key, default);
            Assert.Equal(DurableCreateExecutionOutcome.Succeeded, retry.Outcome);
            Assert.Equal(DurableCreateOperationState.Succeeded, retry.Operation!.State);
        }
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM durable_create_operation_attempts"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));
        await AssertRejectedAsync(prepared.Connection, """
            INSERT INTO postgresql_database_recovery_decisions
                (id, deployment_id, generation, resource_id, action, attempt_id, attempt_number,
                 reason, note, technical_actor, acknowledged, decided_at)
            SELECT gen_random_uuid(), o.deployment_id, o.generation, o.resource_id, o.action,
                   o.current_attempt_id, o.attempt_number, 'OperatorAcceptedExistingDatabase', 'false recovery',
                   'tester', true, now()
            FROM durable_create_operations o
            """, "23503");
        Assert.Equal(0L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM postgresql_database_recovery_decisions"));
        // Terminal history is immutable at the database boundary too.
        await AssertRejectedAsync(prepared.Connection,
            "UPDATE durable_create_operations SET state = 'Pending' WHERE state = 'Succeeded'");
        await AssertRejectedAsync(prepared.Connection,
            "UPDATE durable_create_operation_attempts SET outcome = 'Conflict'");
        await AssertRejectedAsync(prepared.Connection,
            "DELETE FROM durable_create_operation_attempts");
    }

    [Fact]
    public async Task Crash_before_the_marker_resumes_preparing_and_completes_with_exactly_one_dispatch()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_b4");
        var hooks = new FaultHooks { BeforeMarker = _ => throw new CrashException() };
        await using (var services = Services(prepared.Connection, prepared.Time, hooks: hooks))
            await Assert.ThrowsAsync<CrashException>(async () => await ExecuteAsync(services, prepared.Key, default));
        Assert.Equal("Preparing", await ScalarAsync(prepared.Connection, "SELECT state FROM durable_create_operations"));
        Assert.Null(await ScalarAsync(prepared.Connection, "SELECT dispatched_at FROM durable_create_operations"));
        Assert.Equal(0L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));
        Assert.Null(await ScalarAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts"));

        await using var restarted = Services(prepared.Connection, prepared.Time);
        Assert.Equal(DurableCreateExecutionOutcome.Succeeded, (await ExecuteAsync(restarted, prepared.Key, default)).Outcome);
        Assert.Equal(["AbandonedBeforeDispatch", "Succeeded"], await ScalarsAsync(prepared.Connection, """
            SELECT outcome FROM durable_create_operation_attempts ORDER BY attempt_number
            """));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection, """
            SELECT count(*) FROM durable_create_operation_attempts WHERE dispatched_at IS NOT NULL
            """));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));
    }

    [Fact]
    public async Task Restart_immediately_after_a_committed_marker_requires_attention_without_create()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_am");
        Guid attemptId;
        await using (var services = Services(prepared.Connection, prepared.Time))
        {
            var journal = services.GetRequiredService<IDurableCreateOperationJournal>();
            var started = Assert.IsType<DurableCreateBeginAttemptResult.Started>(
                await journal.TryBeginAttemptAsync(prepared.Key, prepared.Time.GetUtcNow(), default));
            attemptId = started.AttemptId;
            Assert.IsType<DurableCreateMarkerResult.Committed>(
                await journal.TryCommitDispatchMarkerAsync(prepared.Key, attemptId,
                    prepared.Time.GetUtcNow(), default));
        }

        Assert.Equal("Dispatching", await ScalarAsync(prepared.Connection,
            "SELECT state FROM durable_create_operations"));
        Assert.Equal(attemptId, await ScalarAsync(prepared.Connection,
            "SELECT current_attempt_id FROM durable_create_operations"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM durable_create_operation_attempts WHERE dispatched_at IS NOT NULL"));
        Assert.Equal(0L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));

        await AlterRoleAsync(prepared.User, "NOLOGIN");
        await using var restarted = Services(prepared.Connection, prepared.Time);
        var result = await ExecuteAsync(restarted, prepared.Key, default);

        Assert.Equal(DurableCreateExecutionOutcome.NeedsAttention, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.UncertainAfterDispatch, result.Operation!.Reason);
        Assert.Equal(["Uncertain"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts"));
        Assert.Null(await ScalarAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(0L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));
    }

    [Fact]
    public async Task Cancellation_at_the_marker_boundary_stops_before_SQL_and_resumes_with_a_new_attempt()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_c4");
        using var cancellation = new CancellationTokenSource();
        var hooks = new FaultHooks { BeforeMarker = cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            } };
        await using (var services = Services(prepared.Connection, prepared.Time, hooks: hooks))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await ExecuteAsync(services, prepared.Key, cancellation.Token));
        Assert.Equal("Preparing", await ScalarAsync(prepared.Connection, "SELECT state FROM durable_create_operations"));
        Assert.Null(await ScalarAsync(prepared.Connection, "SELECT dispatched_at FROM durable_create_operations"));
        Assert.Equal(0L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));

        await using var resumed = Services(prepared.Connection, prepared.Time);
        Assert.Equal(DurableCreateExecutionOutcome.Succeeded, (await ExecuteAsync(resumed, prepared.Key, default)).Outcome);
        Assert.Equal(["AbandonedBeforeDispatch", "Succeeded"], await ScalarsAsync(prepared.Connection, """
            SELECT outcome FROM durable_create_operation_attempts ORDER BY attempt_number
            """));
    }

    [Fact]
    public async Task Crash_after_external_success_recovers_to_needs_attention_even_with_the_database_present()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_a4");
        var hooks = new FaultHooks { BeforeSuccess = _ => throw new CrashException() };
        await using (var services = Services(prepared.Connection, prepared.Time, hooks: hooks))
            await Assert.ThrowsAsync<CrashException>(async () => await ExecuteAsync(services, prepared.Key, default));
        // The Create response and the confirmation read completed; the terminal row did not.
        Assert.Equal("Dispatching", await ScalarAsync(prepared.Connection, "SELECT state FROM durable_create_operations"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));
        Assert.Null(await ScalarAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));

        // Recovery must not touch the Provider again; the revoked login proves it.
        await AlterRoleAsync(prepared.User, "NOLOGIN");
        await using var restarted = Services(prepared.Connection, prepared.Time);
        var result = await ExecuteAsync(restarted, prepared.Key, default);
        Assert.Equal(DurableCreateExecutionOutcome.NeedsAttention, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.UncertainAfterDispatch, result.Operation!.Reason);
        Assert.Equal("NeedsAttention", await ScalarAsync(prepared.Connection, "SELECT state FROM durable_create_operations"));
        Assert.Null(await ScalarAsync(prepared.Connection, "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));
        Assert.Equal(["Uncertain"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts"));

        // NeedsAttention is terminal for the executor: no automatic retry, no further SQL.
        var again = await ExecuteAsync(restarted, prepared.Key, default);
        Assert.Equal(DurableCreateExecutionOutcome.NeedsAttention, again.Outcome);
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM durable_create_operation_attempts"));
    }

    [Fact]
    public async Task Cancellation_during_the_create_command_requires_attention_without_retry()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_dc");
        using var cancellation = new CancellationTokenSource();
        var hooks = new FaultHooks { DuringCreate = _ =>
            {
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            } };
        await using var services = Services(prepared.Connection, prepared.Time, hooks: hooks);
        var result = await ExecuteAsync(services, prepared.Key, cancellation.Token);

        Assert.Equal(DurableCreateExecutionOutcome.NeedsAttention, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.UncertainAfterDispatch, result.Operation!.Reason);
        Assert.Null(await ScalarAsync(prepared.Connection, "SELECT applied_generation FROM application_deployments"));
        Assert.NotNull(await ScalarAsync(prepared.Connection, "SELECT dispatched_at FROM durable_create_operations"));
        Assert.Equal(["Uncertain"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts"));

        var again = await ExecuteAsync(services, prepared.Key, default);
        Assert.Equal(DurableCreateExecutionOutcome.NeedsAttention, again.Outcome);
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM durable_create_operation_attempts"));
    }

    [Fact]
    public async Task Connection_loss_after_the_marker_requires_attention_and_never_retries_or_fakes_success()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_cl");
        var hooks = new FaultHooks { DuringCreate = _ =>
            {
                // The Provider backend is terminated after the durable marker and before the
                // CREATE invocation. Even this window must not authorize an automatic retry.
                return new ValueTask(TerminateBackendsAsync(prepared.User));
            } };
        await using var services = Services(prepared.Connection, prepared.Time, hooks: hooks);
        var result = await ExecuteAsync(services, prepared.Key, default);

        Assert.Equal(DurableCreateExecutionOutcome.NeedsAttention, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.UncertainAfterDispatch, result.Operation!.Reason);
        Assert.Null(await ScalarAsync(prepared.Connection, "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(["Uncertain"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts"));
    }

    [Fact]
    public async Task A_preexisting_database_is_a_conflict_that_never_adopts_or_dispatches()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_pr");
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{prepared.Database}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        await using var services = Services(prepared.Connection, prepared.Time);
        var result = await ExecuteAsync(services, prepared.Key, default);

        Assert.Equal(DurableCreateExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.Conflict, result.Operation!.Reason);
        Assert.Null(await ScalarAsync(prepared.Connection, "SELECT dispatched_at FROM durable_create_operations"));
        Assert.Null(await ScalarAsync(prepared.Connection, "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(["Conflict"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts"));
        Assert.Null(await ScalarAsync(prepared.Connection, """
            SELECT dispatched_at FROM durable_create_operation_attempts WHERE dispatched_at IS NOT NULL LIMIT 1
            """));

        var again = await ExecuteAsync(services, prepared.Key, default);
        Assert.Equal(DurableCreateExecutionOutcome.Failed, again.Outcome);
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM durable_create_operation_attempts"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));
    }

    [Fact]
    public async Task Concurrent_execution_on_one_key_never_dispatches_twice_or_advances_applied_early()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_cc");
        await using var first = Services(prepared.Connection, prepared.Time);
        await using var second = Services(prepared.Connection, prepared.Time);
        var results = await Task.WhenAll(
            ExecuteAsync(first, prepared.Key, default).AsTask(),
            ExecuteAsync(second, prepared.Key, default).AsTask());

        var dispatched = await ScalarLongAsync(prepared.Connection, """
            SELECT count(*) FROM durable_create_operation_attempts WHERE dispatched_at IS NOT NULL
            """);
        var databases = await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database));
        var applied = await ScalarAsync(prepared.Connection, "SELECT applied_generation FROM application_deployments");
        var state = await ScalarAsync(prepared.Connection, "SELECT state FROM durable_create_operations");

        Assert.True(dispatched <= 1, $"dispatched attempts: {dispatched}");
        Assert.True(databases <= 1, $"created databases: {databases}");
        Assert.All(results, result => Assert.NotNull(result.Operation));
        if (applied is not null)
        {
            Assert.Equal(1L, applied);
            Assert.Equal("Succeeded", state);
            Assert.Equal(1L, databases);
        }
        else
        {
            // Without a durably recorded and verified success the operation stays recoverable or
            // needs attention; no window may claim success.
            Assert.NotEqual("Succeeded", state);
        }
    }

    [Fact]
    public async Task An_alias_switch_after_acceptance_still_executes_the_pinned_revision()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_as");
        await using (var services = Services(prepared.Connection, prepared.Time))
        {
            // The alias must first pass preflight. Revoke that target's login after the switch:
            // execution can succeed only through the generation's pinned Provider revision.
            var secrets = services.GetRequiredService<ISecretStore>();
            var otherUser = "haby_alias_" + Guid.NewGuid().ToString("N");
            var otherPassword = "haby-test-" + Guid.NewGuid().ToString("N");
            await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
            {
                await connection.OpenAsync();
                await using var role = new NpgsqlCommand(
                    $"""CREATE ROLE "{otherUser}" LOGIN CREATEDB PASSWORD '{otherPassword}'""", connection);
                await role.ExecuteNonQueryAsync();
            }
            var otherSecret = new SecretRef(SecretId.New(), SecretVersionId.New());
            await secrets.CreateAsync(prepared.Environment, otherSecret,
                Encoding.UTF8.GetBytes(otherPassword), default);
            var provider = services.GetRequiredService<IProviderConfigurationStore>();
            var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
            var other = new PostgreSqlProviderConfiguration(1, admin.Host!, admin.Port, admin.Database!,
                otherUser, otherSecret, Implementation);
            var otherInstance = await provider.CreateInstanceAsync(prepared.Environment, other, default);
            await provider.AddRevisionAsync(prepared.Environment, otherInstance,
                ProviderConfigurationRevisionNumber.Create(1), other, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed,
                (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(prepared.Environment,
                    ProviderAlias.Create("postgresql.primary"), prepared.Binding,
                    new ProviderAliasBinding(otherInstance, ProviderConfigurationRevisionNumber.Create(1)),
                    "tester", default)).Outcome);
            await AlterRoleAsync(otherUser, "NOLOGIN");

            var result = await ExecuteAsync(services, prepared.Key, default);
            Assert.Equal(DurableCreateExecutionOutcome.Succeeded, result.Outcome);
            Assert.Equal(prepared.Binding.InstanceId, result.Operation!.ProviderInstance);
            Assert.Equal(prepared.Binding.RevisionNumber, result.Operation.ConfigurationRevision);
        }
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));
    }

    [Fact]
    public async Task An_unavailable_codec_closes_the_attempt_and_can_resume_with_the_same_pinned_revision()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_nr");
        await using (var noCodec = Services(prepared.Connection, prepared.Time, registerCodec: false))
        {
            var result = await ExecuteAsync(noCodec, prepared.Key, default);

            Assert.Equal(DurableCreateExecutionOutcome.RetryableFailure, result.Outcome);
            Assert.Equal(DurableCreateOperationState.Preparing, result.Operation!.State);
            Assert.Equal(DurableCreateOperationReason.ProviderInputUnavailable, result.Operation.Reason);
        }
        Assert.Null(await ScalarAsync(prepared.Connection, "SELECT dispatched_at FROM durable_create_operations"));
        Assert.Null(await ScalarAsync(prepared.Connection, "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(0L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));
        Assert.Equal(["FailedBeforeDispatch"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts"));

        await using var restored = Services(prepared.Connection, prepared.Time);
        var retry = await ExecuteAsync(restored, prepared.Key, default);
        Assert.Equal(DurableCreateExecutionOutcome.Succeeded, retry.Outcome);
        Assert.Equal(["FailedBeforeDispatch", "Succeeded"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts ORDER BY attempt_number"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM durable_create_operation_attempts WHERE dispatched_at IS NOT NULL"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));
    }

    [Fact]
    public async Task Transient_manifest_read_failure_closes_only_attempt_and_resumes()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_manifest_retry");
        await using var services = Services(prepared.Connection, prepared.Time, failManifestReadOnce: true);
        var first = await ExecuteAsync(services, prepared.Key, default);
        Assert.Equal(DurableCreateExecutionOutcome.RetryableFailure, first.Outcome);
        Assert.Equal(DurableCreateOperationState.Preparing, first.Operation!.State);
        Assert.Equal(DurableCreateOperationReason.ProviderInputUnavailable, first.Operation.Reason);
        Assert.Equal(0L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM durable_create_operation_attempts WHERE dispatched_at IS NOT NULL"));
        Assert.Equal(0L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));

        var resumed = await ExecuteAsync(services, prepared.Key, default);
        Assert.Equal(DurableCreateExecutionOutcome.Succeeded, resumed.Outcome);
        Assert.Equal(["FailedBeforeDispatch", "Succeeded"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts ORDER BY attempt_number"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));
    }

    [Fact]
    public async Task Corrupt_stored_manifest_is_definitive_before_dispatch()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_manifest_corrupt");
        await ExecuteSqlAsync(prepared.Connection,
            "UPDATE manifest_revisions SET snapshot_format_version = 1, snapshot = '{bad'");
        await using var services = Services(prepared.Connection, prepared.Time);
        var result = await ExecuteAsync(services, prepared.Key, default);
        Assert.Equal(DurableCreateExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.ProviderInputUnavailable, result.Operation!.Reason);
        Assert.Equal(["FailedBeforeDispatch"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts"));
        Assert.Equal(0L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));
    }

    [Fact]
    public async Task Lost_provider_connectivity_before_dispatch_can_be_restored_and_retried()
    {
        var prepared = await PrepareExecutionAsync("haby_exec_rc");
        await AlterRoleAsync(prepared.User, "NOLOGIN");
        await using (var unavailable = Services(prepared.Connection, prepared.Time))
        {
            var first = await ExecuteAsync(unavailable, prepared.Key, default);
            Assert.Equal(DurableCreateExecutionOutcome.RetryableFailure, first.Outcome);
            Assert.Equal(DurableCreateOperationState.Preparing, first.Operation!.State);
            Assert.Equal(DurableCreateOperationReason.Connectivity, first.Operation.Reason);
        }
        Assert.Null(await ScalarAsync(prepared.Connection, "SELECT dispatched_at FROM durable_create_operations"));
        Assert.Equal(0L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));

        await AlterRoleAsync(prepared.User, "LOGIN");
        await using var restored = Services(prepared.Connection, prepared.Time);
        var retry = await ExecuteAsync(restored, prepared.Key, default);
        Assert.Equal(DurableCreateExecutionOutcome.Succeeded, retry.Outcome);
        Assert.Null(retry.Operation!.Reason);
        Assert.Equal(["FailedBeforeDispatch", "Succeeded"], await ScalarsAsync(prepared.Connection,
            "SELECT outcome FROM durable_create_operation_attempts ORDER BY attempt_number"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM durable_create_operation_attempts WHERE dispatched_at IS NOT NULL"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(1L, await ScalarLongAsync(prepared.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name",
            command => command.Parameters.AddWithValue("name", prepared.Database)));
    }

    private sealed record Prepared(string Connection, AdjustableTimeProvider Time, EnvironmentRecordId Environment,
        DurableCreateOperationKey Key, string Database, string User, string Password,
        ProviderAliasBinding Binding);

    private async Task<Prepared> PrepareExecutionAsync(string prefix, bool migrateToPrevious = false)
    {
        var database = prefix + "_" + Guid.NewGuid().ToString("N");
        var connection = await NewDatabaseAsync(prefix);
        var time = new AdjustableTimeProvider();
        await using (var services = Services(connection, time))
        {
            if (migrateToPrevious)
            {
                await using var context = await services
                    .GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>()
                    .CreateDbContextAsync();
                await context.Database.MigrateAsync("20261027101647_AddDurableCreateDispatchBarrier");
            }
            else
                await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var secrets = services.GetRequiredService<ISecretStore>();
            var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            var password = "haby-test-" + Guid.NewGuid().ToString("N");
            var user = "haby_exec_" + Guid.NewGuid().ToString("N");
            await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
            {
                await admin.OpenAsync();
                await using var role = new NpgsqlCommand(
                    $"""CREATE ROLE "{user}" LOGIN CREATEDB PASSWORD '{password}'""", admin);
                await role.ExecuteNonQueryAsync();
            }
            var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
            await secrets.CreateAsync(environment, secret, Encoding.UTF8.GetBytes(password), default);
            var target = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            { Username = user, Password = password };
            var configuration = new PostgreSqlProviderConfiguration(1, target.Host!, target.Port,
                target.Database!, target.Username!, secret, Implementation);
            var provider = services.GetRequiredService<IProviderConfigurationStore>();
            var instance = await provider.CreateInstanceAsync(environment, configuration, default);
            await provider.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1),
                configuration, default);
            var binding = new ProviderAliasBinding(instance, ProviderConfigurationRevisionNumber.Create(1));
            Assert.Equal(ProviderAliasChangeOutcome.Changed,
                (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment,
                    ProviderAlias.Create("postgresql.primary"), null, binding, "tester", default)).Outcome);
            var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                services.GetRequiredService<IManifestImportStore>(), Catalog, time)
                .ExecuteAsync(ManifestFor(database), "tester"));
            var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
            var changeSet = (await planner.CreateChangeSetAsync(environment,
                imported.ManifestRevisionRef.ApplicationRecordId, imported.ManifestRevisionRef,
                new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
            var plan = (await planner.CreatePlanAsync(changeSet.Id, "tester", default)).Plan!;
            var committed = Assert.IsType<DurableCommitResult.Committed>(await services
                .GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(changeSet.Id, plan.Id, "tester", default));
            return new Prepared(connection, time, environment,
                new DurableCreateOperationKey(committed.Commit.DeploymentId, committed.Commit.Generation,
                    ManifestLocalId.Create("db"), DurableOperationAction.Create),
                database, user, password, binding);
        }
    }

    private static ValueTask<DurableCreateExecutionResult> ExecuteAsync(ServiceProvider services,
        DurableCreateOperationKey key, CancellationToken cancellationToken) =>
        services.GetRequiredService<IDurableCreateOperationExecutor>().ExecuteAsync(key, cancellationToken);

    private static byte[] ManifestFor(string databaseName) => Encoding.UTF8.GetBytes(
        """{"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"operator-example"},"spec":{"resources":{"db":{"type":"postgresql.database/v1alpha1","providerRef":"postgresql.primary","properties":{"name":"__NAME__"}}}}}}"""
            .Replace("__NAME__", databaseName, StringComparison.Ordinal));

    private async Task<string> NewDatabaseAsync(string prefix)
    {
        var name = prefix + "_cat_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await create.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    private async Task AlterRoleAsync(string user, string option)
    {
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"""ALTER ROLE "{user}" {option}""", admin);
        await command.ExecuteNonQueryAsync();
    }

    private async Task TerminateBackendsAsync(string user)
    {
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity
            WHERE usename = @user AND pid <> pg_backend_pid()
            """, admin);
        command.Parameters.AddWithValue("user", user);
        await command.ExecuteNonQueryAsync();
    }

    private static ServiceProvider Services(string connection, TimeProvider clock, bool registerCodec = true,
        IDurableCreateDispatchFaultHooks? hooks = null, bool failManifestReadOnce = false)
    {
        var services = new ServiceCollection()
            .AddSingleton(clock)
            .AddPostgreSqlManifestCatalog(connection)
            .AddPostgreSqlPlaintextSecretStore()
            .AddPostgreSqlProviderConfigurationStore()
            .AddPostgreSqlProviderAliasStore()
            .AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>();
        if (registerCodec) services.AddPostgreSqlProviderConfigurationCodec();
        if (failManifestReadOnce)
            services.AddSingleton<IManifestRevisionQuery>(provider => new FailOnceManifestRevisionQuery(
                provider.GetRequiredService<PostgreSqlManifestImportStore>()));
        services.AddPostgreSqlProviderRevisionPreflight()
            .AddPostgreSqlDurableDesiredStateCommitter()
            .AddPostgreSqlDurableCreateOperationJournal()
            .AddPostgreSqlDurableCreateOperationExecution()
            .AddPostgreSqlDatabaseRecoveryJournal()
            .AddPostgreSqlDatabaseRecovery();
        if (hooks is not null) services.AddSingleton(hooks);
        return services.BuildServiceProvider();
    }

    private sealed class FailOnceManifestRevisionQuery(IManifestRevisionQuery inner) : IManifestRevisionQuery
    {
        private bool failed;

        public ValueTask<ManifestRevision?> FindAsync(ManifestRevisionRef reference,
            CancellationToken cancellationToken = default)
        {
            if (!failed)
            {
                failed = true;
                throw new IOException("Temporary manifest store outage");
            }
            return inner.FindAsync(reference, cancellationToken);
        }
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connection)
    {
        var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        return database;
    }

    private static async Task<object?> ScalarAsync(string connection, string sql,
        Action<NpgsqlCommand>? bind = null)
    {
        await using var database = await OpenAsync(connection);
        await using var command = new NpgsqlCommand(sql, database);
        bind?.Invoke(command);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task<long> ScalarLongAsync(string connection, string sql,
        Action<NpgsqlCommand>? bind = null) => (long)(await ScalarAsync(connection, sql, bind))!;

    private static async Task<string[]> ScalarsAsync(string connection, string sql)
    {
        await using var database = await OpenAsync(connection);
        await using var command = new NpgsqlCommand(sql, database);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.IsDBNull(0) ? "NULL" : reader.GetString(0));
        return [.. values];
    }

    private static async Task AssertRejectedAsync(string connection, string sql,
        string expectedSqlState = "P0001")
    {
        await using var database = await OpenAsync(connection);
        await using var command = new NpgsqlCommand(sql, database);
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync());
        Assert.Equal(expectedSqlState, error.SqlState);
    }

    private sealed class CrashException : Exception
    {
        public CrashException() : base("injected process crash") { }
    }

    private sealed class FaultHooks : IDurableCreateDispatchFaultHooks
    {
        public Func<CancellationToken, ValueTask>? BeforeMarker { get; init; }
        public Func<CancellationToken, ValueTask>? AfterMarker { get; init; }
        public Func<CancellationToken, ValueTask>? DuringCreate { get; init; }
        public Func<CancellationToken, ValueTask>? BeforeSuccess { get; init; }

        public ValueTask BeforeDispatchMarkerAsync(CancellationToken cancellationToken) =>
            BeforeMarker?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
        public ValueTask AfterDispatchMarkerCommittedAsync(CancellationToken cancellationToken) =>
            AfterMarker?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
        public ValueTask DuringCreateCommandAsync(CancellationToken cancellationToken) =>
            DuringCreate?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
        public ValueTask BeforeSuccessPersistedAsync(CancellationToken cancellationToken) =>
            BeforeSuccess?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
