using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Fact]
    public async Task Pg_dump_restore_preserves_active_pilot_history_without_provider_access_or_replay()
    {
        var scenario = await ExecutionScenarioAsync();
        var directory = "/tmp/haby-active-restore-" + Guid.NewGuid().ToString("N");
        var dump = directory + "/catalog.dump";
        Exception? failure = null;
        WebApplication? source = null;
        WebApplication? fresh = null;
        try
        {
            var restored = await NewDatabaseAsync();
            var histories = new List<RestoreHistory>();
            AssignExistingOperationRequest? assignment = null;
            RecoveryReceipt? receipt = null;
            var loseConfirmation = false;
            source = await StartExecutionFaultHost(scenario, new ExecutionHooks
            {
                BeforeSuccess = _ => loseConfirmation ? throw new IOException("Test-only lost confirmation.") : ValueTask.CompletedTask
            });
            {
                using var channel = Channel(source, scenario.Certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                foreach (var state in new[] { "pending", "dispatching", "attention", "confirmed", "assigned" })
                {
                    var database = "restore_" + Guid.NewGuid().ToString("N");
                    var imported = await client.ImportAsync(new EnvelopeRequest
                    {
                        OriginalEnvelope = ByteString.CopyFromUtf8(Encoding.UTF8.GetString(Envelope("restore-" + state))
                            .Replace("sample_db", database, StringComparison.Ordinal))
                    });
                    Assert.Equal(ImportReply.OutcomeOneofCase.Created, imported.OutcomeCase);
                    Assert.Equal("manifest.resource.unused", Assert.Single(imported.Created.Revision.HistoricalWarnings.Diagnostics).Code);
                    var change = await client.CreateChangeSetAsync(new CreateChangeSetRequest
                    {
                        EnvironmentId = "pilot",
                        Reference = imported.Created.Revision.Reference,
                        Precondition = PlanningPrecondition.ExpectedAbsence
                    });
                    Assert.Equal(PlanningStatus.Created, change.Status);
                    var plan = await client.CreatePlanAsync(new ChangeSetRequest { ChangeSetId = change.ChangeSet.ChangeSetId });
                    Assert.Equal(PlanningStatus.Planned, plan.Status);
                    var commit = await client.CommitDesiredStateAsync(new CommitDesiredStateRequest
                    { ChangeSetId = change.ChangeSet.ChangeSetId, PlanId = plan.Plan.PlanId });
                    Assert.Equal(CommitStatus.Committed, commit.Status);
                    var key = new OperationRequest
                    {
                        DeploymentId = commit.Commit.DeploymentId,
                        Generation = 1,
                        ResourceId = "db",
                        Action = OperationAction.Create
                    };
                    if (state is "dispatching" or "attention")
                    {
                        // Simulate process death after the durable barrier, before sending SQL.
                        var journal = source.Services.GetRequiredService<IDurableCreateOperationJournal>();
                        var started = Assert.IsType<DurableCreateBeginAttemptResult.Started>(
                            await journal.TryBeginAttemptAsync(ExecutionKey(key), DateTimeOffset.UtcNow, default));
                        Assert.IsType<DurableCreateMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(
                            ExecutionKey(key), started.AttemptId, DateTimeOffset.UtcNow, default));
                        if (state == "attention")
                            Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(key)).Status);
                    }
                    if (state == "confirmed")
                        Assert.Equal(OperationStatus.Succeeded, (await client.ExecuteOperationAsync(key)).Status);
                    if (state == "assigned")
                    {
                        loseConfirmation = true;
                        await Assert.ThrowsAsync<RpcException>(async () => await client.ExecuteOperationAsync(key));
                        Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(key)).Status);
                        assignment = RecoveryAssignment(await client.InspectOperationRecoveryAsync(key));
                        var assigned = await client.AssignExistingOperationAsync(assignment);
                        Assert.Equal(RecoveryStatus.Assigned, assigned.Status);
                        receipt = assigned.Receipt;
                        Assert.Equal(OperatorHostApplication.TechnicalActorId, receipt.TechnicalActor);
                        Assert.NotEmpty(receipt.DecisionId);
                        Assert.NotEmpty(receipt.DecidedAtUtc);
                    }
                    var history = await ReadRestoreHistory(client, key);
                    AssertSafeRestoreOutput(history, scenario.Password);
                    AssertRestoreState(history, state);
                    histories.Add(history);
                }
            }
            await source.DisposeAsync(); // Drain and stop all source writers before snapshot.
            source = null;
            foreach (var history in histories)
                Assert.Equal(history.Operation.Operation.State == OperationState.Succeeded ? 1L : (long?)null,
                    await ExecutionScalar(scenario.Connection, "SELECT applied_generation FROM application_deployments WHERE id = @id",
                        command => command.Parameters.AddWithValue("id", Guid.Parse(history.Operation.Operation.Key.DeploymentId))));
            await AlterRecoveryLogin(scenario, "NOLOGIN NOCREATEDB");
            var original = await RestoreCatalogDigests(scenario.Connection);
            var role = "restore_host_" + Guid.NewGuid().ToString("N");
            // Restore as the actual catalog principal, making restored objects migration-capable.
            await ExecutionSql(restored, $"CREATE ROLE {role}; GRANT ALL ON SCHEMA public TO {role}");
            await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
            // Directory is private before pg_dump opens its secret-bearing output file.
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump,
                "--dbname=" + new NpgsqlConnectionStringBuilder(scenario.Connection).Database);
            await fixture.RunClientUtilityAsync("chmod", "600", dump);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner",
                "--no-privileges", "--role=" + role, "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
            var hostConnection = new NpgsqlConnectionStringBuilder(restored) { Options = "-c role=" + role }.ConnectionString;
            await VerifyRestoreRights(hostConnection);
            Assert.Equal(original, await RestoreCatalogDigests(restored));

            var trap = new RestoreProviderTrap();
            using var logs = new ExecutionLogs();
            fresh = OperatorHostApplication.Build(Arguments(hostConnection, scenario.CertificatePath), services =>
            {
                services.AddSingleton<IDurableCreateTarget>(trap);
                services.AddLogging(builder => builder.AddProvider(logs));
            });
            {
                await fresh.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
                await fresh.StartAsync();
                Assert.Equal(0, trap.Calls);
                Assert.Equal(original, await RestoreCatalogDigests(restored));
                using var channel = Channel(fresh, scenario.Certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                for (var repeat = 0; repeat < 2; repeat++)
                    foreach (var history in histories)
                    {
                        var actual = await ReadRestoreHistory(client, history.Operation.Operation.Key);
                        AssertSafeRestoreOutput(actual, scenario.Password);
                        Assert.Equal(history, actual);
                    }
                var retry = assignment!.Clone();
                retry.Note = "Retry must retain the original protected explanation";
                var result = await client.AssignExistingOperationAsync(retry);
                AssertSafeRestoreText(result.ToString(), scenario.Password);
                Assert.Equal(RecoveryStatus.AlreadyAssigned, result.Status);
                Assert.Equal(receipt, result.Receipt);
                await AssertRestoreProtectedInputs(fresh.Services, scenario, assignment);
                Assert.Equal(0, trap.Calls); // Counts even calls whose exceptions are caught by production code.
                Assert.Equal(original, await RestoreCatalogDigests(restored));
            }
            await fresh.DisposeAsync();
            fresh = null;
            AssertSafeRestoreText(logs.Text, scenario.Password);
            Assert.Equal(original, await RestoreCatalogDigests(scenario.Connection));
            // Catalog restore cannot undo or reproduce external effects.
            foreach (var history in histories)
            {
                var shouldExist = history.Operation.Operation.State == OperationState.Succeeded;
                Assert.Equal(shouldExist ? 1L : 0L, await ExecutionScalar(fixture.ConnectionString,
                    "SELECT count(*) FROM pg_database WHERE datname = @name",
                    command => command.Parameters.AddWithValue("name", history.Recovery.Preview.TargetName)));
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            // Only this invocation's exact files; never let cleanup replace the original failure.
            await CleanupRestoreResources(failure, output.WriteLine,
                async () => { if (source is not null) await source.DisposeAsync(); },
                async () => { if (fresh is not null) await fresh.DisposeAsync(); },
                () => fixture.RunClientUtilityAsync("rm", "-f", dump),
                () => fixture.RunClientUtilityAsync("rmdir", directory),
                async () => await scenario.DisposeAsync());
        }
    }

    [Fact]
    public async Task Restore_cleanup_preserves_primary_failure_drains_all_actions_and_withholds_cleanup_details()
    {
        var primary = new InvalidOperationException("Primary restore failure.");
        var messages = new List<string>();
        var drained = false;
        async Task Fail()
        {
            try { throw primary; }
            finally
            {
                await CleanupRestoreResources(primary, messages.Add,
                    () => throw new IOException("Protected cleanup detail"),
                    () => { drained = true; return Task.CompletedTask; });
            }
        }
        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(Fail));
        Assert.True(drained);
        Assert.False(Assert.Single(messages).Contains("Protected cleanup detail", StringComparison.Ordinal));
        var cleanupOnly = await Assert.ThrowsAsync<InvalidOperationException>(() => CleanupRestoreResources(null,
            messages.Add, () => throw new IOException("Protected cleanup detail")));
        Assert.False(cleanupOnly.ToString().Contains("Protected cleanup detail", StringComparison.Ordinal));
    }

    private static async Task CleanupRestoreResources(Exception? primary, Action<string> report, params Func<Task>[] actions)
    {
        var failed = false;
        foreach (var action in actions)
            try { await action(); }
            catch (Exception) { failed = true; }
        if (!failed) return;
        if (primary is null) throw new InvalidOperationException("Active restore cleanup failed; disposable container teardown is required.");
        report("Active restore cleanup also failed; the original failure is preserved. Disposable container teardown is required.");
    }

    private sealed record RestoreHistory(RevisionSummary Revision, DurableChangeSetSummary ChangeSet,
        DurablePlanSummary Plan, CommitReply Commit, CommitReply Generation, OperationReply Operation, RecoveryReply Recovery);

    private static async Task<RestoreHistory> ReadRestoreHistory(ManifestOperator.ManifestOperatorClient client, OperationRequest key)
    {
        var operation = await client.GetOperationAsync(key);
        Assert.Equal(OperationStatus.Found, operation.Status);
        var generation = await client.GetGenerationAsync(new GenerationRequest { DeploymentId = key.DeploymentId, Generation = key.Generation });
        Assert.Equal(CommitStatus.Found, generation.Status);
        var change = new ChangeSetRequest { ChangeSetId = generation.DesiredGeneration.ChangeSetId };
        return new RestoreHistory((await client.GetRevisionAsync(new GetRevisionRequest { Reference = operation.Operation.Reference })).Found,
            (await client.GetChangeSetAsync(change)).ChangeSet,
            (await client.GetPlanAsync(new PlanRequest { PlanId = generation.DesiredGeneration.PlanId })).Plan,
            await client.GetCommitAsync(change), generation, operation, await client.InspectOperationRecoveryAsync(key));
    }

    private static void AssertRestoreState(RestoreHistory history, string state)
    {
        Assert.Equal(state switch
        {
            "pending" => OperationState.Pending,
            "dispatching" => OperationState.Dispatching,
            "attention" => OperationState.NeedsAttention,
            _ => OperationState.Succeeded
        }, history.Operation.Operation.State);
        Assert.Equal(1, history.Generation.DesiredGeneration.Generation);
        Assert.Equal(history.Plan.ProviderInstanceId, history.Operation.Operation.ProviderInstanceId);
        Assert.Equal(history.Plan.ConfigurationRevision, history.Operation.Operation.ConfigurationRevision);
        Assert.Equal(history.Plan.ImplementationId, history.Operation.Operation.ImplementationId);
        if (state == "pending") Assert.Empty(history.Operation.Attempts);
        else
        {
            var attempt = Assert.Single(history.Operation.Attempts);
            Assert.Equal(state switch
            {
                "dispatching" => OperationAttemptOutcome.Unspecified,
                "confirmed" => OperationAttemptOutcome.Succeeded,
                _ => OperationAttemptOutcome.Uncertain
            }, attempt.Outcome);
            Assert.True(attempt.HasDispatchedAtUtc);
        }
        Assert.Equal(state == "attention" ? OperationReason.UncertainAfterDispatch : OperationReason.Unspecified,
            history.Operation.Operation.Reason);
        Assert.Equal(state == "assigned", history.Recovery.Receipt is not null);
    }

    private static void AssertSafeRestoreOutput(RestoreHistory history, string password)
    {
        foreach (var message in new IMessage[] { history.Revision, history.ChangeSet, history.Plan, history.Commit,
            history.Generation, history.Operation, history.Recovery }) AssertSafeRestoreText(message.ToString()!, password);
    }

    private static void AssertSafeRestoreText(string text, string password)
    {
        // Boolean assertions intentionally prevent xUnit from rendering protected operands on failure.
        Assert.False(text.Contains(password, StringComparison.Ordinal), "Secret material escaped the restore boundary.");
        Assert.False(text.Contains(RecoveryNote, StringComparison.Ordinal), "Protected audit note escaped the restore boundary.");
    }

    private static async Task AssertRestoreProtectedInputs(IServiceProvider services, ExecutionScenario scenario,
        AssignExistingOperationRequest assignment)
    {
        var revision = await services.GetRequiredService<IProviderConfigurationStore>()
            .FindRevisionAsync<PostgreSqlProviderConfiguration>(scenario.Environment, scenario.Instance,
                ProviderConfigurationRevisionNumber.Create(1), default);
        Assert.NotNull(revision);
        using var material = await services.GetRequiredService<ISecretStore>()
            .ResolveAsync(scenario.Environment, revision.Configuration.AdministratorPassword, default);
        Assert.NotNull(material);
        var bytes = material.CopyBytes();
        try { Assert.True(bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(scenario.Password)), "Pinned secret did not survive restore."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        var audit = await services.GetRequiredService<IPostgreSqlDatabaseRecoveryJournal>()
            .FindDecisionAsync(ExecutionKey(assignment.Key), Guid.Parse(assignment.ExpectedAttemptId), default);
        Assert.NotNull(audit);
        Assert.True(audit.Acknowledged);
        Assert.True(string.Equals(RecoveryNote, audit.Note, StringComparison.Ordinal), "Protected audit did not survive unchanged.");
    }

    private static async Task VerifyRestoreRights(string connection)
    {
        Assert.Equal(true, await ExecutionScalar(connection, "SELECT has_schema_privilege(current_user, 'public', 'USAGE,CREATE')"));
        // Roll back a DDL and DML probe using the real Host role, rather than merely testing as admin.
        await ExecutionSql(connection, "BEGIN; CREATE TABLE public.restore_rights_probe (id integer); INSERT INTO public.restore_rights_probe VALUES (1); UPDATE public.restore_rights_probe SET id = 2; DELETE FROM public.restore_rights_probe; DROP TABLE public.restore_rights_probe; ROLLBACK");
        Assert.Equal(0L, await ExecutionScalar(connection, "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tableowner <> current_user"));
    }

    private static async Task<SortedDictionary<string, string>> RestoreCatalogDigests(string connection)
    {
        // Independent verification context on every call. Catalog inspection is intentionally SQL:
        // it includes every persisted column/table (also protected payloads), with no tracked entities.
        await using var context = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
        await context.Database.OpenConnectionAsync();
        var db = (NpgsqlConnection)context.Database.GetDbConnection();
        var tables = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", db))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(table);
            await using var command = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(content ORDER BY content::text), '[]'::jsonb)::text FROM (SELECT to_jsonb(row_value) AS content FROM public.{quoted} row_value) rows", db);
            var content = (string)(await command.ExecuteScalarAsync())!;
            // Only digests can reach assertion diagnostics. Never emit snapshots containing secrets/notes.
            result.Add(table, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))));
            if (table == "application_deployments")
            {
                await using var applied = new NpgsqlCommand("SELECT count(*) FROM application_deployments WHERE desired_generation = 1 AND applied_generation = 1", db);
                Assert.Equal(2L, await applied.ExecuteScalarAsync());
                await using var uncertain = new NpgsqlCommand("SELECT count(*) FROM application_deployments WHERE desired_generation = 1 AND applied_generation IS NULL", db);
                Assert.Equal(3L, await uncertain.ExecuteScalarAsync());
            }
        }
        return result;
    }

    private sealed class RestoreProviderTrap : IDurableCreateTarget
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public ValueTask<DurableCreateAbsenceOutcome> CheckDatabaseAbsentAsync(DurableCreateTargetInput input, CancellationToken cancellationToken)
        { Interlocked.Increment(ref calls); throw new InvalidOperationException("Restored Host contacted a Provider read boundary."); }
        public ValueTask<DurableCreateDispatchOutcome> DispatchCreateAsync(DurableCreateTargetInput input, CancellationToken cancellationToken)
        { Interlocked.Increment(ref calls); throw new InvalidOperationException("Restored Host contacted a Provider dispatch boundary."); }
    }
}
