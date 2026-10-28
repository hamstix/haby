using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    private const string RecoveryNote = "Provenance independently verified by the test operator";

    [Fact]
    public async Task Recovery_rpc_is_read_only_then_atomically_assigns_once_and_returns_original_receipt_after_restart()
    {
        await using var scenario = await ExecutionScenarioAsync();
        var request = await UncertainRecoveryOperation(scenario, created: true);
        RecoveryReceipt original;
        using var logs = new ExecutionLogs();
        await using (var host = await StartExecutionLoggedHost(scenario.Connection, scenario.CertificatePath, logs))
        {
            using var channel = Channel(host, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            var before = await RecoveryCatalog(scenario);
            var dispatching = await client.InspectOperationRecoveryAsync(request);
            Assert.Equal(RecoveryStatus.Found, dispatching.Status);
            Assert.Equal(OperationState.Dispatching, dispatching.Preview.Operation.State);
            Assert.False(dispatching.Preview.HasUncertainAttemptId);
            Assert.Equal(before, await RecoveryCatalog(scenario));
            Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(request)).Status);
            var preview = await client.InspectOperationRecoveryAsync(request);
            Assert.Equal(scenario.Database, preview.Preview.TargetName);
            Assert.Equal(scenario.Instance.ToString(), preview.Preview.Operation.ProviderInstanceId);
            Assert.True(preview.Preview.HasUncertainAttemptId);
            before = await RecoveryCatalog(scenario);
            Assert.Equal(preview, await client.InspectOperationRecoveryAsync(request));
            Assert.Equal(before, await RecoveryCatalog(scenario));
            // A read-only target login makes a hidden CREATE fail. Current alias selects an unavailable alternative.
            await AlterRecoveryLogin(scenario, "NOCREATEDB");
            await scenario.RedirectAliasAsync();
            var assignment = RecoveryAssignment(preview);
            var concurrent = await Task.WhenAll(client.AssignExistingOperationAsync(assignment).ResponseAsync,
                client.AssignExistingOperationAsync(assignment).ResponseAsync);
            Assert.Contains(concurrent, r => r.Status == RecoveryStatus.Assigned);
            Assert.All(concurrent, r => Assert.Contains(r.Status, new[] { RecoveryStatus.Assigned, RecoveryStatus.AlreadyAssigned }));
            original = concurrent[0].Receipt;
            Assert.Equal(original, concurrent[1].Receipt);
            Assert.Equal(OperatorHostApplication.TechnicalActorId, original.TechnicalActor);
            Assert.DoesNotContain(RecoveryNote, string.Join("", concurrent.Select(r => r.ToString())));
        }
        await AssertRecoveredCatalog(scenario);
        await AlterRecoveryLogin(scenario, "NOLOGIN");
        await using (var restarted = await StartRecoveryWithoutManifest(scenario))
        {
            using var channel = Channel(restarted, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            var preview = await client.InspectOperationRecoveryAsync(request);
            Assert.Equal(OperationState.Succeeded, preview.Preview.Operation.State);
            Assert.Equal(original, preview.Receipt);
            var noConsent = RecoveryAssignment(preview); noConsent.Acknowledged = false;
            Assert.Equal(RecoveryStatus.InvalidRequest, (await client.AssignExistingOperationAsync(noConsent)).Status);
            var retry = await client.AssignExistingOperationAsync(RecoveryAssignment(preview));
            Assert.Equal(RecoveryStatus.AlreadyAssigned, retry.Status);
            Assert.Equal(original, retry.Receipt); // No new existence check is possible with NOLOGIN.
        }
        await AssertRecoveredCatalog(scenario);
        Assert.DoesNotContain(RecoveryNote, logs.Text);
        Assert.DoesNotContain(scenario.Password, logs.Text);
    }

    [Fact]
    public async Task Raw_recovery_rpc_rejects_missing_consent_stale_identity_reason_and_note_without_partial_updates()
    {
        await using var scenario = await ExecutionScenarioAsync();
        var key = await UncertainRecoveryOperation(scenario, created: true);
        using var logs = new ExecutionLogs();
        await using var host = await StartExecutionLoggedHost(scenario.Connection, scenario.CertificatePath, logs);
        using var channel = Channel(host, scenario.Certificate);
        var client = new ManifestOperator.ManifestOperatorClient(channel);
        Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(key)).Status);
        var assignment = RecoveryAssignment(await client.InspectOperationRecoveryAsync(key));
        var before = await RecoveryCatalog(scenario);
        var cases = new List<(AssignExistingOperationRequest Request, RecoveryStatus Status)>();
        void Add(Action<AssignExistingOperationRequest> mutate, RecoveryStatus status)
        { var copy = assignment.Clone(); mutate(copy); cases.Add((copy, status)); }
        Add(r => r.Acknowledged = false, RecoveryStatus.InvalidRequest);
        Add(r => r.ExpectedAttemptId = Guid.NewGuid().ToString("D"), RecoveryStatus.Rejected);
        Add(r => r.ExpectedAttemptId = "", RecoveryStatus.InvalidRequest);
        Add(r => r.EnvironmentRecordId = EnvironmentRecordId.New().ToString(), RecoveryStatus.Rejected);
        Add(r => r.EnvironmentRecordId = "", RecoveryStatus.InvalidRequest);
        Add(r => r.EnvironmentRecordId = r.EnvironmentRecordId.ToUpperInvariant(), RecoveryStatus.InvalidRequest);
        Add(r => r.Key.DeploymentId = r.Key.DeploymentId.ToUpperInvariant(), RecoveryStatus.InvalidRequest);
        Add(r => r.Key.ResourceId = "other", RecoveryStatus.OperationNotFound);
        Add(r => r.Key.DeploymentId = ApplicationDeploymentId.New().ToString(), RecoveryStatus.OperationNotFound);
        Add(r => r.Key.Generation = 2, RecoveryStatus.UnsupportedRecoveryAction);
        Add(r => r.Key.Action = (OperationAction)999, RecoveryStatus.UnsupportedRecoveryAction);
        Add(r => r.Key.Action = OperationAction.Unspecified, RecoveryStatus.InvalidRequest);
        Add(r => r.Key = null, RecoveryStatus.InvalidRequest);
        Add(r => r.Reason = RecoveryReason.Unspecified, RecoveryStatus.InvalidRequest);
        Add(r => r.Reason = (RecoveryReason)999, RecoveryStatus.InvalidRequest);
        foreach (var note in new[] { "", " ", new string('x', 513), "password=secret-sentinel", "bad\ntext", "bad\u202etext" })
            Add(r => r.Note = note, RecoveryStatus.InvalidRequest);
        foreach (var (request, status) in cases)
        {
            var reply = await client.AssignExistingOperationAsync(request);
            Assert.Equal(status, reply.Status); Assert.Null(reply.Receipt);
            Assert.DoesNotContain("secret-sentinel", reply.ToString());
            Assert.Equal(before, await RecoveryCatalog(scenario));
        }
        Assert.DoesNotContain(RecoveryNote, logs.Text); Assert.DoesNotContain("secret-sentinel", logs.Text);
        await AssertExecutionCatalog(scenario, exists: true, applied: false, dispatched: 1);
    }

    [Fact]
    public async Task Recovery_absent_database_and_unavailable_pinned_target_leave_uncertainty_unresolved()
    {
        await using var scenario = await ExecutionScenarioAsync();
        var key = await UncertainRecoveryOperation(scenario, created: false);
        await using var host = await StartAsync(scenario.Connection, scenario.CertificatePath);
        using var channel = Channel(host, scenario.Certificate);
        var client = new ManifestOperator.ManifestOperatorClient(channel);
        Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(key)).Status);
        var assignment = RecoveryAssignment(await client.InspectOperationRecoveryAsync(key));
        var before = await RecoveryCatalog(scenario);
        Assert.Equal(RecoveryStatus.TargetAbsent, (await client.AssignExistingOperationAsync(assignment)).Status);
        Assert.Equal(before, await RecoveryCatalog(scenario));
        await AlterRecoveryLogin(scenario, "NOLOGIN");
        Assert.Equal(RecoveryStatus.TargetUnavailable, (await client.AssignExistingOperationAsync(assignment)).Status);
        Assert.Equal(before, await RecoveryCatalog(scenario));
        await AssertExecutionCatalog(scenario, exists: false, applied: false, dispatched: 1);
    }

    [Fact]
    public async Task Recovery_definitive_conflict_is_ineligible_for_assignment()
    {
        await using var scenario = await ExecutionScenarioAsync();
        await using var host = await StartAsync(scenario.Connection, scenario.CertificatePath);
        using var channel = Channel(host, scenario.Certificate);
        var client = new ManifestOperator.ManifestOperatorClient(channel);
        var key = await CommitExecutionAsync(client, scenario.Database);
        await ExecutionSql(scenario.Connection, $"CREATE DATABASE \"{scenario.Database}\"");
        Assert.Equal(OperationStatus.Failed, (await client.ExecuteOperationAsync(key)).Status);
        var preview = await client.InspectOperationRecoveryAsync(key);
        Assert.False(preview.Preview.HasUncertainAttemptId);
        var assignment = RecoveryAssignment(preview);
        assignment.ExpectedAttemptId = preview.Preview.Operation.AttemptId;
        var before = await RecoveryCatalog(scenario);
        Assert.Equal(RecoveryStatus.Rejected, (await client.AssignExistingOperationAsync(assignment)).Status);
        Assert.Equal(before, await RecoveryCatalog(scenario));
        await AssertExecutionCatalog(scenario, exists: true, applied: false, dispatched: 0);
    }

    [Fact]
    public async Task Unsupported_pinned_implementation_never_enters_recovery_or_mutates_state()
    {
        await using var scenario = await ExecutionScenarioAsync();
        var key = await UncertainRecoveryOperation(scenario, created: false);
        var host = OperatorHostApplication.Build(Arguments(scenario.Connection, scenario.CertificatePath), services =>
            services.AddSingleton<IDurableCreateOperationQuery>(sp => new UnsupportedRecoveryQuery(
                sp.GetRequiredService<IDurableCreateOperationJournal>())));
        await using (host)
        {
            await host.StartAsync();
            using var channel = Channel(host, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(key)).Status);
            var before = await RecoveryCatalog(scenario);
            var preview = await client.InspectOperationRecoveryAsync(key);
            Assert.Equal(RecoveryStatus.UnsupportedRecoveryAction, preview.Status);
            var raw = new AssignExistingOperationRequest { Key = key, Acknowledged = true,
                EnvironmentRecordId = scenario.Environment.ToString(), ExpectedAttemptId = Guid.NewGuid().ToString("D"),
                Reason = RecoveryReason.OperatorAcceptedExistingDatabase, Note = RecoveryNote };
            Assert.Equal(RecoveryStatus.UnsupportedRecoveryAction, (await client.AssignExistingOperationAsync(raw)).Status);
            Assert.Equal(before, await RecoveryCatalog(scenario));
        }
    }

    private sealed class UnsupportedRecoveryQuery(IDurableCreateOperationJournal journal) : IDurableCreateOperationQuery
    {
        public async ValueTask<DurableCreateOperation?> FindAsync(DurableCreateOperationKey key, CancellationToken cancellationToken) =>
            await journal.FindAsync(key, cancellationToken) is { } operation ? operation with { ImplementationId = "unsupported/v1" } : null;
    }

    [Fact]
    public async Task Recovery_preview_projects_one_application_snapshot_even_when_dispatcher_read_is_older()
    {
        await using var scenario = await ExecutionScenarioAsync();
        var key = await UncertainRecoveryOperation(scenario, created: false);
        var host = OperatorHostApplication.Build(Arguments(scenario.Connection, scenario.CertificatePath), services =>
            services.AddSingleton<IDurableCreateOperationQuery>(sp => new OlderRecoveryQuery(
                sp.GetRequiredService<IDurableCreateOperationJournal>())));
        await using (host)
        {
            await host.StartAsync();
            using var channel = Channel(host, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(key)).Status);
            var actual = (await client.GetOperationAsync(key)).Operation;
            var preview = (await client.InspectOperationRecoveryAsync(key)).Preview;
            Assert.Equal(actual, preview.Operation);
            Assert.Equal(actual.AttemptId, preview.UncertainAttemptId);
        }
    }

    private sealed class OlderRecoveryQuery(IDurableCreateOperationJournal journal) : IDurableCreateOperationQuery
    {
        public async ValueTask<DurableCreateOperation?> FindAsync(DurableCreateOperationKey key, CancellationToken cancellationToken) =>
            await journal.FindAsync(key, cancellationToken) is { } operation ? operation with
            { State = DurableCreateOperationState.Pending, AttemptId = null, AttemptNumber = 0,
                Reason = null, UpdatedAt = null, DispatchedAt = null, FinalizedAt = null } : null;
    }

    private sealed class UnavailableRecoveryManifest : IManifestRevisionQuery
    {
        public ValueTask<ManifestRevision?> FindAsync(ManifestRevisionRef reference, CancellationToken cancellationToken = default) =>
            throw new InvalidDataException("unavailable manifest sentinel");
    }

    private static async Task<Microsoft.AspNetCore.Builder.WebApplication> StartRecoveryWithoutManifest(ExecutionScenario scenario)
    {
        var host = OperatorHostApplication.Build(Arguments(scenario.Connection, scenario.CertificatePath), services =>
            services.AddSingleton<IManifestRevisionQuery, UnavailableRecoveryManifest>());
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task Recovery_rejects_a_competing_generation_change_after_preview()
    {
        await using var scenario = await ExecutionScenarioAsync();
        var key = await UncertainRecoveryOperation(scenario, created: true);
        await using var host = await StartAsync(scenario.Connection, scenario.CertificatePath);
        using var channel = Channel(host, scenario.Certificate);
        var client = new ManifestOperator.ManifestOperatorClient(channel);
        Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(key)).Status);
        var assignment = RecoveryAssignment(await client.InspectOperationRecoveryAsync(key));
        // Like the retained-history test, relax only the pilot generation CHECKs in this disposable
        // catalog to model a later accepted generation. The recovery transaction must still reject it.
        await ExecutionSql(scenario.Connection, """
            ALTER TABLE application_deployments DROP CONSTRAINT "CK_application_deployments_desired_generation";
            ALTER TABLE application_deployment_generations DROP CONSTRAINT "CK_deployment_generations_first";
            INSERT INTO application_deployment_generations
                (deployment_id, generation, presence, plan_id, change_set_id,
                 application_record_id, revision_number, environment_record_id,
                 environment_key, provider_alias, provider_instance_id,
                 configuration_revision_number, implementation_id, fingerprint, accepted_at, actor_id)
            SELECT deployment_id, 2, presence, plan_id, change_set_id,
                   application_record_id, revision_number, environment_record_id,
                   environment_key, provider_alias, provider_instance_id,
                   configuration_revision_number, implementation_id, fingerprint, now(), actor_id
            FROM application_deployment_generations WHERE generation = 1;
            UPDATE application_deployments SET desired_generation = 2, concurrency_token = concurrency_token + 1;
            """);
        var before = await RecoveryCatalog(scenario);
        Assert.Equal(RecoveryStatus.Rejected, (await client.AssignExistingOperationAsync(assignment)).Status);
        Assert.Equal(before, await RecoveryCatalog(scenario));
    }

    [Fact]
    public async Task Recovery_persistence_failure_returns_value_free_error_without_audit_or_state_update()
    {
        await using var scenario = await ExecutionScenarioAsync();
        var key = await UncertainRecoveryOperation(scenario, created: true);
        var role = "recovery_reader_" + Guid.NewGuid().ToString("N");
        await ExecutionSql(scenario.Connection, $"CREATE ROLE {role}; GRANT ALL ON SCHEMA public TO {role}; GRANT ALL ON ALL TABLES IN SCHEMA public TO {role}; REVOKE INSERT ON postgresql_database_recovery_decisions FROM {role}");
        var restricted = new Npgsql.NpgsqlConnectionStringBuilder(scenario.Connection) { Options = "-c role=" + role }.ConnectionString;
        using var logs = new ExecutionLogs();
        await using var host = await StartExecutionLoggedHost(restricted, scenario.CertificatePath, logs);
        using var channel = Channel(host, scenario.Certificate);
        var client = new ManifestOperator.ManifestOperatorClient(channel);
        Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(key)).Status);
        var assignment = RecoveryAssignment(await client.InspectOperationRecoveryAsync(key));
        var before = await RecoveryCatalog(scenario);
        var error = await Assert.ThrowsAsync<RpcException>(async () => await client.AssignExistingOperationAsync(assignment));
        Assert.Equal(StatusCode.Internal, error.StatusCode);
        Assert.Equal(before, await RecoveryCatalog(scenario));
        Assert.DoesNotContain(RecoveryNote, error.ToString() + logs.Text);
        Assert.DoesNotContain(scenario.Password, error.ToString() + logs.Text);
        Assert.DoesNotContain("Npgsql.PostgresException", logs.Text);
    }

    [Fact]
    public async Task Recovery_native_cli_assigns_real_uncertain_Create_and_retries_original_receipt_after_restart()
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrEmpty(binary));
        if (binary is null) return;
        Assert.True(File.Exists(binary));
        await using var scenario = await ExecutionScenarioAsync();
        var key = await UncertainRecoveryOperation(scenario, created: true);
        var note = Path.GetTempFileName();
        var config = Path.GetTempFileName();
        await File.WriteAllTextAsync(config, "formatVersion: 1\ninstances: {}\ncontexts: {}");
        await File.WriteAllTextAsync(note, RecoveryNote, new UTF8Encoding(false));
        string? receipt = null;
        try
        {
            for (var restart = 0; restart < 2; restart++)
            {
                await using var host = restart == 0 ? await StartAsync(scenario.Connection, scenario.CertificatePath)
                    : await StartRecoveryWithoutManifest(scenario);
                using var channel = Channel(host, scenario.Certificate);
                var client = new ManifestOperator.ManifestOperatorClient(channel);
                if (restart == 0)
                {
                    Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(key)).Status);
                    await AlterRecoveryLogin(scenario, "NOCREATEDB");
                }
                else await AlterRecoveryLogin(scenario, "NOLOGIN");
                var preview = await client.InspectOperationRecoveryAsync(key);
                var request = RecoveryAssignment(preview);
                await Run(["inspect", key.DeploymentId, "1", "db", "--action", "create"], "json", 0, false);
                var args = new[] { "assign-existing", key.DeploymentId, "1", "db", "--action", "create",
                    "--environment-record-id", request.EnvironmentRecordId, "--expected-attempt-id", request.ExpectedAttemptId.ToUpperInvariant(),
                    "--reason", "operatorAcceptedExistingDatabase", "--note-file", note, "--acknowledge-assignment" };
                if (restart == 0) await Run(args[..^1], "json", 2, false);
                var result = await Run(args, "json", 0, true);
                using var json = JsonDocument.Parse(result);
                var decision = json.RootElement.GetProperty("data"u8).GetProperty("receipt"u8).GetRawText();
                if (restart == 0) receipt = decision; else Assert.Equal(receipt, decision);
                await Run(args, "text", 0, true);
                Assert.Equal(OperationState.Succeeded, (await client.GetOperationAsync(key)).Operation.State);

                async Task<string> Run(string[] args, string format, int exit, bool summary)
                {
                    var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var arg in new[] { "operation", "recovery" }.Concat(args).Concat(["-o", format, "--config", config])) start.ArgumentList.Add(arg);
                    start.Environment["HABY_OPERATOR_URL"] = host.Urls.Single();
                    start.Environment["HABY_OPERATOR_SKIP_TLS_VALIDATION"] = "true";
                    using var process = Process.Start(start)!;
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree: true); throw; }
                    Assert.Equal(exit, process.ExitCode);
                    var text = await stdout; var error = await stderr;
                    Assert.DoesNotContain(RecoveryNote, text + error); Assert.DoesNotContain(scenario.Password, text + error);
                    if (summary) { Assert.Contains(restart == 0 ? scenario.Database : "(unavailable)", error); Assert.Contains(request.ExpectedAttemptId, error); }
                    if (exit == 2) Assert.Equal("", text);
                    return text;
                }
            }
            await AssertRecoveredCatalog(scenario);
        }
        finally { File.Delete(note); File.Delete(config); }
    }

    private async Task<OperationRequest> UncertainRecoveryOperation(ExecutionScenario scenario, bool created)
    {
        await using var host = created
            ? await StartExecutionFaultHost(scenario, new ExecutionHooks { BeforeSuccess = _ => throw new IOException("test-only crash") })
            : await StartAsync(scenario.Connection, scenario.CertificatePath);
        using var channel = Channel(host, scenario.Certificate);
        var client = new ManifestOperator.ManifestOperatorClient(channel);
        var request = await CommitExecutionAsync(client, scenario.Database);
        if (created)
            await Assert.ThrowsAsync<RpcException>(async () => await client.ExecuteOperationAsync(request));
        else
        {
            var key = ExecutionKey(request); var journal = host.Services.GetRequiredService<IDurableCreateOperationJournal>();
            var started = Assert.IsType<DurableCreateBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(key, DateTimeOffset.UtcNow, default));
            Assert.IsType<DurableCreateMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(key, started.AttemptId, DateTimeOffset.UtcNow, default));
        }
        return request;
    }

    private static AssignExistingOperationRequest RecoveryAssignment(RecoveryReply preview) => new()
    {
        Key = preview.Preview.Operation.Key.Clone(), EnvironmentRecordId = preview.Preview.Operation.EnvironmentRecordId,
        ExpectedAttemptId = preview.Preview.HasUncertainAttemptId ? preview.Preview.UncertainAttemptId : preview.Receipt?.AttemptId ?? Guid.NewGuid().ToString("D"),
        Acknowledged = true, Reason = RecoveryReason.OperatorAcceptedExistingDatabase, Note = RecoveryNote
    };

    private static async Task<string> RecoveryCatalog(ExecutionScenario scenario) =>
        (string)(await ExecutionScalar(scenario.Connection, """
            SELECT jsonb_build_object(
                'operations', (SELECT jsonb_agg(to_jsonb(o)) FROM durable_create_operations o),
                'attempts', (SELECT jsonb_agg(to_jsonb(a)) FROM durable_create_operation_attempts a),
                'decisions', (SELECT jsonb_agg(to_jsonb(d)) FROM postgresql_database_recovery_decisions d),
                'deployments', (SELECT jsonb_agg(to_jsonb(p)) FROM application_deployments p))::text
            """))!;

    private static async Task AssertRecoveredCatalog(ExecutionScenario scenario)
    {
        await AssertExecutionCatalog(scenario, exists: true, applied: true, dispatched: 1);
        Assert.Equal(1L, await ExecutionScalar(scenario.Connection, "SELECT count(*) FROM postgresql_database_recovery_decisions"));
        Assert.Equal("Succeeded", await ExecutionScalar(scenario.Connection, "SELECT state FROM durable_create_operations"));
        Assert.Equal("Uncertain", await ExecutionScalar(scenario.Connection, "SELECT outcome FROM durable_create_operation_attempts"));
        Assert.Equal(RecoveryNote, await ExecutionScalar(scenario.Connection, "SELECT note FROM postgresql_database_recovery_decisions"));
        Assert.Equal(true, await ExecutionScalar(scenario.Connection, "SELECT acknowledged FROM postgresql_database_recovery_decisions"));
    }

    private static async Task AlterRecoveryLogin(ExecutionScenario scenario, string alteration)
    {
        var revision = await scenario.Setup.GetRequiredService<IProviderConfigurationStore>()
            .FindRevisionAsync<PostgreSqlProviderConfiguration>(scenario.Environment, scenario.Instance, ProviderConfigurationRevisionNumber.Create(1), default);
        await ExecutionSql(scenario.Connection, $"ALTER ROLE \"{revision!.Configuration.AdministratorUserName}\" {alteration}");
    }
}
