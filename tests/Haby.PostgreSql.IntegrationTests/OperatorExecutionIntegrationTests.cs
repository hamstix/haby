using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Fact]
    public async Task Explicit_execution_uses_production_singleton_and_pinned_inputs_and_survives_restart()
    {
        await using var scenario = await ExecutionScenarioAsync();
        OperationReply original;
        await using (var host = await StartAsync(scenario.Connection, scenario.CertificatePath))
        {
            var executor = host.Services.GetRequiredService<IDurableCreateOperationExecutor>();
            using var scope = host.Services.CreateScope();
            Assert.Same(executor, scope.ServiceProvider.GetRequiredService<IDurableCreateOperationExecutor>());
            Assert.IsType<PostgreSqlDurableCreateOperationExecutor>(executor);
            Assert.IsType<PostgreSqlDurableCreateTarget>(host.Services.GetRequiredService<IDurableCreateTarget>());
            using var channel = Channel(host, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            var request = await CommitExecutionAsync(client, scenario.Database);
            var pending = await client.GetOperationAsync(request);
            Assert.Equal(OperationState.Pending, pending.Operation.State);
            Assert.Empty(pending.Attempts);
            await AssertExecutionCatalog(scenario, exists: false, applied: false, dispatched: 0);
            // The current alias now points to another instance with an unavailable exact secret.
            // Execution must still use the revision/SecretVersion accepted by the commit.
            await scenario.RedirectAliasAsync();
            var concurrent = await Task.WhenAll(client.ExecuteOperationAsync(request).ResponseAsync,
                client.ExecuteOperationAsync(request).ResponseAsync);
            Assert.All(concurrent, r => Assert.Equal(OperationStatus.Succeeded, r.Status));
            Assert.Equal(concurrent[0], concurrent[1]);
            original = await client.GetOperationAsync(request);
            Assert.Equal(OperationStatus.Found, original.Status);
            Assert.Equal(OperationState.Succeeded, original.Operation.State);
            Assert.Equal(scenario.Instance.ToString(), original.Operation.ProviderInstanceId);
            Assert.Equal(1, original.Operation.ConfigurationRevision);
            Assert.Equal(OperationAttemptOutcome.Succeeded, Assert.Single(original.Attempts).Outcome);
            Assert.DoesNotContain(scenario.Password, original.ToString());
        }
        await AssertExecutionCatalog(scenario, exists: true, applied: true, dispatched: 1);
        await using (var restarted = await StartAsync(scenario.Connection, scenario.CertificatePath))
        {
            using var channel = Channel(restarted, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            Assert.Equal(original, await client.GetOperationAsync(original.Operation.Key));
            Assert.Equal(OperationStatus.Succeeded, (await client.ExecuteOperationAsync(original.Operation.Key)).Status);
            Assert.Equal(original, await client.GetOperationAsync(original.Operation.Key));
        }
        await AssertExecutionCatalog(scenario, exists: true, applied: true, dispatched: 1);
    }

    [Fact]
    public async Task Preexisting_database_is_a_definitive_conflict_without_adoption()
    {
        await using var scenario = await ExecutionScenarioAsync();
        await using var host = await StartAsync(scenario.Connection, scenario.CertificatePath);
        using var channel = Channel(host, scenario.Certificate);
        var client = new ManifestOperator.ManifestOperatorClient(channel);
        var request = await CommitExecutionAsync(client, scenario.Database);
        await ExecutionSql(scenario.Connection, $"CREATE DATABASE \"{scenario.Database}\"");
        var result = await client.ExecuteOperationAsync(request);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(OperationReason.Conflict, result.Operation.Reason);
        Assert.Equal(OperationState.Failed, result.Operation.State);
        Assert.Equal(result, await client.ExecuteOperationAsync(request));
        var history = await client.GetOperationAsync(request);
        Assert.Equal(OperationAttemptOutcome.Conflict, Assert.Single(history.Attempts).Outcome);
        await AssertExecutionCatalog(scenario, exists: true, applied: false, dispatched: 0);
    }

    [Fact]
    public async Task Unavailable_pinned_secret_access_is_retryable_with_retained_attempt_history()
    {
        await using var scenario = await ExecutionScenarioAsync();
        var role = "exec_reader_" + Guid.NewGuid().ToString("N");
        await ExecutionSql(scenario.Connection, $"CREATE ROLE {role}; GRANT ALL ON SCHEMA public TO {role}; GRANT ALL ON ALL TABLES IN SCHEMA public TO {role}; REVOKE SELECT ON secret_payloads FROM {role}");
        var restricted = new NpgsqlConnectionStringBuilder(scenario.Connection) { Options = "-c role=" + role }.ConnectionString;
        using var logs = new ExecutionLogs();
        await using var host = await StartExecutionLoggedHost(restricted, scenario.CertificatePath, logs);
        using var channel = Channel(host, scenario.Certificate);
        var client = new ManifestOperator.ManifestOperatorClient(channel);
        var request = await CommitExecutionAsync(client, scenario.Database);
        var result = await client.ExecuteOperationAsync(request);
        Assert.Equal(OperationStatus.RetryableFailure, result.Status);
        Assert.Equal(OperationReason.SecretUnavailable, result.Operation.Reason);
        Assert.Equal(OperationState.Preparing, result.Operation.State);
        await AssertExecutionCatalog(scenario, exists: false, applied: false, dispatched: 0);
        // Restore access to the exact pinned SecretVersion; no payload or reference is replaced.
        await ExecutionSql(scenario.Connection, $"GRANT SELECT ON secret_payloads TO {role}");
        Assert.Equal(OperationStatus.Succeeded, (await client.ExecuteOperationAsync(request)).Status);
        var history = await client.GetOperationAsync(request);
        Assert.Equal(new[] { OperationAttemptOutcome.FailedBeforeDispatch, OperationAttemptOutcome.Succeeded },
            history.Attempts.Select(a => a.Outcome));
        await AssertExecutionCatalog(scenario, exists: true, applied: true, dispatched: 1);
        Assert.DoesNotContain(scenario.Password, logs.Text);
        Assert.DoesNotContain("Npgsql.PostgresException", logs.Text);
        Assert.DoesNotContain("unusable-alternate-secret", logs.Text);
        Assert.DoesNotContain("AdministratorPassword", logs.Text);
    }

    [Fact]
    public async Task Invalid_and_unsupported_operation_requests_are_typed_and_never_create_attempts()
    {
        await using var scenario = await ExecutionScenarioAsync();
        await using var host = await StartAsync(scenario.Connection, scenario.CertificatePath);
        using var channel = Channel(host, scenario.Certificate);
        var client = new ManifestOperator.ManifestOperatorClient(channel);
        var request = await CommitExecutionAsync(client, scenario.Database);
        var invalid = new List<OperationRequest> { new() };
        foreach (var id in new[] { "", "bad", Guid.NewGuid().ToString(), Guid.CreateVersion7().ToString().ToUpperInvariant() })
        { var bad = request.Clone(); bad.DeploymentId = id; invalid.Add(bad); }
        // Validate exact identity; neither omitted generation nor an implicit Resource is allowed.
        var zero = request.Clone(); zero.Generation = 0; invalid.Add(zero);
        var negative = request.Clone(); negative.Generation = -1; invalid.Add(negative);
        var empty = request.Clone(); empty.ResourceId = ""; invalid.Add(empty);
        var latest = request.Clone(); latest.ResourceId = "latest.*"; invalid.Add(latest);
        var missingAction = request.Clone(); missingAction.Action = OperationAction.Unspecified; invalid.Add(missingAction);
        foreach (var bad in invalid)
        {
            Assert.Equal(OperationStatus.InvalidRequest, (await client.ExecuteOperationAsync(bad)).Status);
            Assert.Equal(OperationStatus.InvalidRequest, (await client.GetOperationAsync(bad)).Status);
        }
        var unsupported = request.Clone(); unsupported.Action = (OperationAction)999;
        Assert.Equal(OperationStatus.UnsupportedAction, (await client.ExecuteOperationAsync(unsupported)).Status);
        Assert.Equal(OperationStatus.UnsupportedAction, (await client.GetOperationAsync(unsupported)).Status);
        unsupported = request.Clone(); unsupported.Generation = 2;
        Assert.Equal(OperationStatus.UnsupportedShape, (await client.ExecuteOperationAsync(unsupported)).Status);
        Assert.Equal(OperationStatus.UnsupportedShape, (await client.GetOperationAsync(unsupported)).Status);
        var missing = request.Clone(); missing.ResourceId = "other";
        Assert.Equal(OperationStatus.OperationNotFound, (await client.ExecuteOperationAsync(missing)).Status);
        Assert.Equal(OperationStatus.OperationNotFound, (await client.GetOperationAsync(missing)).Status);
        missing = request.Clone(); missing.DeploymentId = ApplicationDeploymentId.New().ToString();
        Assert.Equal(OperationStatus.OperationNotFound, (await client.ExecuteOperationAsync(missing)).Status);
        Assert.Equal(OperationStatus.OperationNotFound, (await client.GetOperationAsync(missing)).Status);
        Assert.Empty((await client.GetOperationAsync(request)).Attempts);
        await AssertExecutionCatalog(scenario, exists: false, applied: false, dispatched: 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_dispatch_survives_restart_and_get_never_recovers_or_adopts(bool created)
    {
        await using var scenario = await ExecutionScenarioAsync();
        OperationRequest request;
        if (created)
        {
            // Test-only crash after real CREATE and its independent existence check, before success persistence.
            await using var host = await StartExecutionFaultHost(scenario, new ExecutionHooks
            {
                BeforeSuccess = _ => throw new IOException("injected process crash")
            });
            using var channel = Channel(host, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            request = await CommitExecutionAsync(client, scenario.Database);
            var error = await Assert.ThrowsAsync<RpcException>(async () => await client.ExecuteOperationAsync(request));
            Assert.Equal(StatusCode.Internal, error.StatusCode);
            Assert.DoesNotContain(scenario.Password, error.ToString());
        }
        else
        {
            await using var host = await StartAsync(scenario.Connection, scenario.CertificatePath);
            using var channel = Channel(host, scenario.Certificate);
            request = await CommitExecutionAsync(new ManifestOperator.ManifestOperatorClient(channel), scenario.Database);
            // Trusted journal API simulates death after the committed marker before SQL.
            var key = ExecutionKey(request);
            var journal = host.Services.GetRequiredService<IDurableCreateOperationJournal>();
            var started = Assert.IsType<DurableCreateBeginAttemptResult.Started>(
                await journal.TryBeginAttemptAsync(key, DateTimeOffset.UtcNow, default));
            Assert.IsType<DurableCreateMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(
                key, started.AttemptId, DateTimeOffset.UtcNow, default));
        }
        await AssertExecutionCatalog(scenario, exists: created, applied: false, dispatched: 1);
        await using (var restarted = await StartAsync(scenario.Connection, scenario.CertificatePath))
        {
            using var channel = Channel(restarted, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            var dispatching = await client.GetOperationAsync(request);
            Assert.Equal(OperationState.Dispatching, dispatching.Operation.State);
            Assert.Equal(OperationAttemptOutcome.Unspecified, Assert.Single(dispatching.Attempts).Outcome);
            Assert.Equal(dispatching, await client.GetOperationAsync(request));
            Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(request)).Status);
            var attention = await client.GetOperationAsync(request);
            Assert.Equal(OperationState.NeedsAttention, attention.Operation.State);
            Assert.Equal(OperationReason.UncertainAfterDispatch, attention.Operation.Reason);
            Assert.Equal(OperationAttemptOutcome.Uncertain, Assert.Single(attention.Attempts).Outcome);
            Assert.Equal(OperationStatus.NeedsAttention, (await client.ExecuteOperationAsync(request)).Status);
            Assert.Equal(attention, await client.GetOperationAsync(request));
        }
        await AssertExecutionCatalog(scenario, exists: created, applied: false, dispatched: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_cancellation_preserves_barrier_and_durable_history(bool afterMarker)
    {
        await using var scenario = await ExecutionScenarioAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask Wait(CancellationToken token)
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { completed.SetResult(); }
        }
        var hooks = afterMarker ? new ExecutionHooks { AfterMarker = Wait } : new ExecutionHooks { BeforeMarker = Wait };
        OperationRequest request;
        await using (var host = await StartExecutionFaultHost(scenario, hooks))
        {
            using var channel = Channel(host, scenario.Certificate);
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            request = await CommitExecutionAsync(client, scenario.Database);
            using var cancellation = new CancellationTokenSource();
            using var call = client.ExecuteOperationAsync(request, cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // A read concurrent with the paused execution is read-only and does not wait on its semaphore.
            var observed = await client.GetOperationAsync(request);
            Assert.Equal(afterMarker ? OperationState.Dispatching : OperationState.Preparing, observed.Operation.State);
            cancellation.Cancel();
            var error = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
            Assert.Equal(StatusCode.Cancelled, error.StatusCode);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // Graceful stop drains the canceled request before restarting the same catalog.
            await host.StopAsync();
        }
        await using var restarted = await StartAsync(scenario.Connection, scenario.CertificatePath);
        using var nextChannel = Channel(restarted, scenario.Certificate);
        var next = new ManifestOperator.ManifestOperatorClient(nextChannel);
        Assert.Equal(afterMarker ? OperationStatus.NeedsAttention : OperationStatus.Succeeded,
            (await next.ExecuteOperationAsync(request)).Status);
        var history = await next.GetOperationAsync(request);
        Assert.Equal(afterMarker ? OperationAttemptOutcome.Uncertain : OperationAttemptOutcome.Succeeded, history.Attempts.Last().Outcome);
        await AssertExecutionCatalog(scenario, exists: !afterMarker, applied: !afterMarker, dispatched: 1);
    }

    [Fact]
    public async Task Published_native_CLI_imports_plans_commits_executes_and_reads_the_real_PostgreSQL_effect()
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrEmpty(binary));
        if (binary is null) return;
        await using var scenario = await ExecutionScenarioAsync();
        var envelope = Path.GetTempFileName();
        await File.WriteAllBytesAsync(envelope, ExecutionEnvelope(scenario.Database));
        string deployment;
        try
        {
            await using (var host = await StartAsync(scenario.Connection, scenario.CertificatePath))
            {
                async Task<JsonDocument> Run(params string[] args) => JsonDocument.Parse(await RunNativeCommitCli(binary, host.Urls.Single(), args));
                using var imported = await Run("manifest", "import", envelope);
                var reference = imported.RootElement.GetProperty("data"u8).GetProperty("revision"u8).GetProperty("reference"u8);
                using var change = await Run("changeset", "create", reference.GetProperty("applicationRecordId"u8).GetString()!,
                    reference.GetProperty("revisionNumber"u8).GetInt64().ToString(), "--expected-absence");
                var changeId = change.RootElement.GetProperty("data"u8).GetProperty("changeSet"u8).GetProperty("changeSetId"u8).GetString()!;
                using var planned = await Run("plan", "create", changeId);
                var plan = planned.RootElement.GetProperty("data"u8).GetProperty("plan"u8).GetProperty("planId"u8).GetString()!;
                using var accepted = await Run("changeset", "commit", changeId, "--plan", plan);
                deployment = accepted.RootElement.GetProperty("data"u8).GetProperty("commit"u8).GetProperty("deploymentId"u8).GetString()!;
                await AssertExecutionCatalog(scenario, exists: false, applied: false, dispatched: 0);
                using var executed = await Run("operation", "execute", deployment, "1", "db", "--action", "create");
                Assert.Equal("succeeded", executed.RootElement.GetProperty("outcome"u8).GetString());
                Assert.DoesNotContain(scenario.Password, executed.RootElement.GetRawText());
            }
            await using (var restarted = await StartAsync(scenario.Connection, scenario.CertificatePath))
            {
                var args = new[] { "operation", "get", deployment, "1", "db", "--action", "create" };
                using var before = JsonDocument.Parse(await RunNativeCommitCli(binary, restarted.Urls.Single(), args));
                using var retry = JsonDocument.Parse(await RunNativeCommitCli(binary, restarted.Urls.Single(),
                    ["operation", "execute", deployment, "1", "db", "--action", "create"]));
                using var after = JsonDocument.Parse(await RunNativeCommitCli(binary, restarted.Urls.Single(), args));
                Assert.Equal(before.RootElement.GetRawText(), after.RootElement.GetRawText());
                var operation = after.RootElement.GetProperty("data"u8);
                Assert.Equal("succeeded", operation.GetProperty("operation"u8).GetProperty("state"u8).GetString());
                Assert.Single(operation.GetProperty("attempts"u8).EnumerateArray());
                Assert.DoesNotContain(scenario.Password, after.RootElement.GetRawText());
            }
            await AssertExecutionCatalog(scenario, exists: true, applied: true, dispatched: 1);
        }
        finally { File.Delete(envelope); }
    }

    private static byte[] ExecutionEnvelope(string database) => Encoding.UTF8.GetBytes(
        Encoding.UTF8.GetString(Envelope("operator-execution")).Replace("sample_db", database, StringComparison.Ordinal));

    private static async Task<OperationRequest> CommitExecutionAsync(ManifestOperator.ManifestOperatorClient client, string database)
    {
        var imported = await client.ImportAsync(new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(ExecutionEnvelope(database)) });
        var change = await client.CreateChangeSetAsync(new CreateChangeSetRequest
        {
            EnvironmentId = "pilot",
            Reference = imported.Created.Revision.Reference,
            Precondition = PlanningPrecondition.ExpectedAbsence
        });
        var plan = await client.CreatePlanAsync(new ChangeSetRequest { ChangeSetId = change.ChangeSet.ChangeSetId });
        var committed = await client.CommitDesiredStateAsync(new CommitDesiredStateRequest
        { ChangeSetId = change.ChangeSet.ChangeSetId, PlanId = plan.Plan.PlanId });
        Assert.Equal(CommitStatus.Committed, committed.Status);
        return new OperationRequest { DeploymentId = committed.Commit.DeploymentId, Generation = 1, ResourceId = "db", Action = OperationAction.Create };
    }

    private static DurableCreateOperationKey ExecutionKey(OperationRequest request) => new(
        ApplicationDeploymentId.Parse(request.DeploymentId), DeploymentGeneration.Create(request.Generation),
        ManifestLocalId.Create(request.ResourceId), DurableOperationAction.Create);

    private async Task<ExecutionScenario> ExecutionScenarioAsync()
    {
        var connection = await NewDatabaseAsync();
        var services = new ServiceCollection().AddPostgreSqlManifestCatalog(connection)
            .AddPostgreSqlPlaintextSecretStore().AddPostgreSqlProviderConfigurationCodec()
            .AddPostgreSqlProviderConfigurationStore().AddPostgreSqlProviderAliasStore()
            .AddSingleton<IProviderRevisionPreflight, SeedPreflight>()
            .AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>().BuildServiceProvider();
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var store = services.GetRequiredService<ISecretStore>();
        var environment = await store.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        // Testcontainers may use a short default password also present in safe implementation IDs.
        // Use a dedicated ephemeral Provider login and unique secret sentinel on every runtime.
        var user = "exec_" + Guid.NewGuid().ToString("N");
        var password = "haby-execution-secret-" + Guid.NewGuid().ToString("N");
        await ExecutionSql(fixture.ConnectionString, $"CREATE ROLE \"{user}\" LOGIN CREATEDB PASSWORD '{password}'");
        var target = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Username = user, Password = password };
        await store.CreateAsync(environment, secret, Encoding.UTF8.GetBytes(password), default);
        var configuration = new PostgreSqlProviderConfiguration(1, target.Host!, target.Port, target.Database!, target.Username!,
            secret, PostgreSqlDatabaseCheckpointCapability.ImplementationId);
        var providers = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await providers.CreateInstanceAsync(environment, configuration, default);
        await providers.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
        Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>()
            .ChangeAsync(environment, ProviderAlias.Create("postgresql.primary"), null,
                new ProviderAliasBinding(instance, ProviderConfigurationRevisionNumber.Create(1)), "fixture", default)).Outcome);
        var certificate = Certificate();
        var path = Path.Combine(Path.GetTempPath(), "haby-execution-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "test-only"));
        return new ExecutionScenario(connection, "exec_" + Guid.NewGuid().ToString("N"), services, environment, instance,
            target.Password!, certificate, path);
    }

    private sealed record ExecutionScenario(string Connection, string Database, ServiceProvider Setup,
        EnvironmentRecordId Environment, ProviderInstanceId Instance, string Password,
        System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate, string CertificatePath) : IAsyncDisposable
    {
        public async Task RedirectAliasAsync()
        {
            var providers = Setup.GetRequiredService<IProviderConfigurationStore>();
            var alternateSecret = new SecretRef(SecretId.New(), SecretVersionId.New());
            await Setup.GetRequiredService<ISecretStore>().CreateAsync(Environment, alternateSecret,
                Encoding.UTF8.GetBytes("unusable-alternate-secret"), default);
            var configuration = new PostgreSqlProviderConfiguration(1, "127.0.0.1", 1, "postgres", "alternate",
                alternateSecret, PostgreSqlDatabaseCheckpointCapability.ImplementationId);
            var alternate = await providers.CreateInstanceAsync(Environment, configuration, default);
            await providers.AddRevisionAsync(Environment, alternate, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await Setup.GetRequiredService<IProviderAliasStore>()
                .ChangeAsync(Environment, ProviderAlias.Create("postgresql.primary"),
                    new ProviderAliasBinding(Instance, ProviderConfigurationRevisionNumber.Create(1)),
                    new ProviderAliasBinding(alternate, ProviderConfigurationRevisionNumber.Create(1)), "fixture", default)).Outcome);
        }

        public async ValueTask DisposeAsync()
        {
            await Setup.DisposeAsync();
            Certificate.Dispose();
            File.Delete(CertificatePath);
        }
    }

    private static async Task<WebApplication> StartExecutionFaultHost(ExecutionScenario scenario, ExecutionHooks hooks)
    {
        var host = OperatorHostApplication.Build(Arguments(scenario.Connection, scenario.CertificatePath), services =>
            services.AddSingleton<IDurableCreateDispatchFaultHooks>(hooks));
        await host.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await host.StartAsync();
        return host;
    }

    private static async Task<WebApplication> StartExecutionLoggedHost(string connection, string path, ExecutionLogs logs)
    {
        var host = OperatorHostApplication.Build(Arguments(connection, path), services => services.AddLogging(b => b.AddProvider(logs)));
        await host.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await host.StartAsync();
        return host;
    }

    private sealed class ExecutionLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> entries = new();
        public string Text => string.Join("\n", entries);
        public ILogger CreateLogger(string categoryName) => new Capture(entries);
        public void Dispose() { }
        private sealed class Capture(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(formatter(state, exception) + exception);
        }
    }

    private sealed class ExecutionHooks : IDurableCreateDispatchFaultHooks
    {
        public Func<CancellationToken, ValueTask>? BeforeMarker { get; init; }
        public Func<CancellationToken, ValueTask>? AfterMarker { get; init; }
        public Func<CancellationToken, ValueTask>? BeforeSuccess { get; init; }
        public ValueTask BeforeDispatchMarkerAsync(CancellationToken token) => BeforeMarker?.Invoke(token) ?? ValueTask.CompletedTask;
        public ValueTask AfterDispatchMarkerCommittedAsync(CancellationToken token) => AfterMarker?.Invoke(token) ?? ValueTask.CompletedTask;
        public ValueTask DuringCreateCommandAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask BeforeSuccessPersistedAsync(CancellationToken token) => BeforeSuccess?.Invoke(token) ?? ValueTask.CompletedTask;
    }

    private static async Task AssertExecutionCatalog(ExecutionScenario scenario, bool exists, bool applied, long dispatched)
    {
        Assert.Equal(exists ? 1L : 0L, await ExecutionScalar(scenario.Connection,
            "SELECT count(*) FROM pg_database WHERE datname = @name", c => c.Parameters.AddWithValue("name", scenario.Database)));
        Assert.Equal(applied ? 1L : null, await ExecutionScalar(scenario.Connection, "SELECT applied_generation FROM application_deployments"));
        Assert.Equal(dispatched, await ExecutionScalar(scenario.Connection,
            "SELECT count(*) FROM durable_create_operation_attempts WHERE dispatched_at IS NOT NULL"));
        Assert.Equal(1L, await ExecutionScalar(scenario.Connection, "SELECT count(*) FROM durable_create_operations"));
        Assert.Equal(1L, await ExecutionScalar(scenario.Connection, "SELECT desired_generation FROM application_deployments"));
    }

    private static async Task<object?> ExecutionScalar(string connection, string sql, Action<NpgsqlCommand>? bind = null)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db); bind?.Invoke(command);
        var value = await command.ExecuteScalarAsync(); return value is DBNull ? null : value;
    }

    private static async Task ExecutionSql(string connection, string sql, Action<NpgsqlCommand>? bind = null)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db); bind?.Invoke(command); await command.ExecuteNonQueryAsync();
    }
}
