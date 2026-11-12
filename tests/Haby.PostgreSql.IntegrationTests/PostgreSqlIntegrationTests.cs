using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.PostgreSql;
using Haby.PostgreSql.IntegrationTests.Wire;
using Google.Protobuf;
using Grpc.Net.Client;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private WslcPostgreSqlServer? _externalServer;
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        using var timing = TestTiming.ClusterReady();
        var external = WslcPostgreSqlRun.ReadEnvironment();
        if (external is not null)
        {
            _externalServer = new(external);
            await _externalServer.StartAsync();
            ConnectionString = _externalServer.ConnectionString;
            return;
        }
        // A unique confidential sentinel cannot collide with ordinary metadata such as
        // "postgresql" in captured CLI output, unlike the fixture's default password.
        _container = new PostgreSqlBuilder("postgres:17.6-alpine")
            .WithPassword(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)))
            .Build();
        await _container.StartAsync();
        ConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        { Pooling = false }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        using var timing = TestTiming.ClusterCleanup();
        if (_container is not null) await _container.DisposeAsync();
        if (_externalServer is not null) await _externalServer.DisposeAsync();
    }

    public async Task RunClientUtilityAsync(params string[] command)
    {
        if (_container is not null)
        {
            var result = await _container.ExecAsync(command);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"PostgreSQL client utility {command[0]} failed (exit {result.ExitCode}). Output withheld because catalog utilities may expose protected data.");
            return;
        }

        if (_externalServer is null) throw new InvalidOperationException("Owned fixture is unavailable.");
        await _externalServer.UtilityAsync(command);
    }

    internal async Task<string> ReadServerLogsAsync()
    {
        if (_container is not null)
        {
            var logs = await _container.GetLogsAsync();
            return logs.Stdout + logs.Stderr;
        }
        if (_externalServer is null) throw new InvalidOperationException("Owned fixture is unavailable.");
        return await _externalServer.UtilityAsync([], logs: true);
    }
}

public sealed class PostgreSqlIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly ManifestContractCatalog Catalog = new([PostgreSqlDatabaseContract.Definition], [], [], []);

    [Fact]
    public async Task Generated_grpc_client_proves_checkpoint_boundaries()
    {
        await using var host = await CheckpointTestHost.StartAsync(fixture.ConnectionString);
        using var channel = GrpcChannel.ForAddress(host.Address);
        var client = new Checkpoint.CheckpointClient(channel);
        var name = Name();
        var originalEnvelope = Payload(name);
        Assert.False(await Exists(name));

        var imported = await client.ImportAsync(new ImportRequest { OriginalEnvelope = ByteString.CopyFrom(originalEnvelope) });
        Assert.Equal("Created", imported.Outcome);
        Assert.Equal(1, imported.RevisionNumber);
        Assert.Equal(1, (await client.ReadStateAsync(new StateRequest { Application = imported.Application })).Revisions);
        Assert.False(await Exists(name));

        var invalid = await client.ImportAsync(new ImportRequest { OriginalEnvelope = ByteString.CopyFromUtf8("{bad") });
        Assert.Equal("InvalidRequest", invalid.Outcome);
        var invalidName = Name();
        var invalidManifest = Encoding.UTF8.GetString(Payload(invalidName, "invalid-checkpoint"))
            .Replace("postgresql.database/v1alpha1", "unknown.database/v1alpha1", StringComparison.Ordinal);
        var invalidDeclaration = await client.ImportAsync(new ImportRequest
        {
            OriginalEnvelope = ByteString.CopyFromUtf8(invalidManifest)
        });
        Assert.Equal("InvalidManifest", invalidDeclaration.Outcome);
        Assert.Equal(1, (await client.ReadStateAsync(new StateRequest { Application = imported.Application })).Revisions);
        Assert.Equal(0, (await client.ReadStateAsync(new StateRequest { Application = imported.Application })).Generations);
        Assert.False(await Exists(name));
        Assert.False(await Exists(invalidName));

        var change = await client.CreateChangeSetAsync(new ChangeSetRequest
        {
            Application = imported.Application,
            Revision = imported.Revision
        });
        Assert.Equal("Created", change.Outcome);
        var plan = await client.PlanAsync(new PlanRequest { ChangeSet = change.ChangeSet });
        Assert.Equal("Planned", plan.Outcome);
        Assert.False(await Exists(name));
        var commit = await client.CommitDesiredStateAsync(new CommitRequest
        {
            ChangeSet = change.ChangeSet,
            Plan = plan.Plan
        });
        Assert.Equal("Committed", commit.Outcome);
        Assert.Equal(1, commit.DesiredGeneration);
        Assert.False(await Exists(name));
        var before = await client.ReadStateAsync(new StateRequest { Application = imported.Application });
        Assert.Equal(1, before.Generations);
        Assert.Equal(0, before.AppliedGeneration);

        var execution = await client.ExecuteCommittedGenerationAsync(new ExecuteRequest { Generation = commit.Generation });
        Assert.Equal("Succeeded", execution.State);
        Assert.Equal("None", execution.Failure);
        Assert.True(await Exists(name));
        Assert.Equal(1, (await client.ReadStateAsync(new StateRequest { Application = imported.Application })).AppliedGeneration);

        var equivalent = await client.ImportAsync(new ImportRequest { OriginalEnvelope = ByteString.CopyFrom(originalEnvelope) });
        Assert.Equal("ExistingEquivalent", equivalent.Outcome);
        Assert.Equal(imported.Revision, equivalent.Revision);
        var repeatedCommit = await client.CommitDesiredStateAsync(new CommitRequest
        {
            ChangeSet = change.ChangeSet,
            Plan = plan.Plan
        });
        Assert.Equal(commit.Generation, repeatedCommit.Generation);
        Assert.Equal(1, repeatedCommit.DesiredGeneration);
        Assert.Equal("Succeeded", (await client.ExecuteCommittedGenerationAsync(new ExecuteRequest
        {
            Generation = repeatedCommit.Generation
        })).State);
        var repeatedState = await client.ReadStateAsync(new StateRequest { Application = imported.Application });
        Assert.Equal(1, repeatedState.Revisions);
        Assert.Equal(1, repeatedState.Generations);
        Assert.Equal(1, repeatedState.AppliedGeneration);

        // Another independent database is deliberately created outside Haby to force a deterministic conflict.
        var conflictName = Name();
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand("CREATE DATABASE \"" + conflictName + "\"", connection);
            await create.ExecuteNonQueryAsync();
        }
        // A new Application has its own immutable revision and deployment precondition.
        var conflictEnvelope = Payload(conflictName, "conflict-checkpoint");
        var conflictImport = await client.ImportAsync(new ImportRequest
        {
            OriginalEnvelope = ByteString.CopyFrom(conflictEnvelope)
        });
        Assert.Equal("Created", conflictImport.Outcome);
        var conflictChange = await client.CreateChangeSetAsync(new ChangeSetRequest
        {
            Application = conflictImport.Application,
            Revision = conflictImport.Revision
        });
        Assert.Equal("Created", conflictChange.Outcome);
        var conflictPlan = await client.PlanAsync(new PlanRequest { ChangeSet = conflictChange.ChangeSet });
        Assert.Equal("Planned", conflictPlan.Outcome);
        var conflictCommit = await client.CommitDesiredStateAsync(new CommitRequest
        {
            ChangeSet = conflictChange.ChangeSet,
            Plan = conflictPlan.Plan
        });
        Assert.Equal("Committed", conflictCommit.Outcome);
        var conflictExecution = await client.ExecuteCommittedGenerationAsync(new ExecuteRequest
        {
            Generation = conflictCommit.Generation
        });
        Assert.Equal("Failed", conflictExecution.State);
        Assert.Equal("Conflict", conflictExecution.Failure);
        Assert.Equal(0, (await client.ReadStateAsync(new StateRequest { Application = conflictImport.Application })).AppliedGeneration);
    }

    [Fact]
    public async Task Imported_and_committed_generation_creates_database_once()
    {
        var name = Name();
        var revisions = new InMemoryManifestImportStore();
        var import = new ImportApplicationManifest(revisions, Catalog, TimeProvider.System);
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
            await import.ExecuteAsync(Payload(name), "actor"));
        var configurations = new InMemoryPostgreSqlConfigurationCatalog();
        configurations.Add("provider", 1, fixture.ConnectionString);
        var provider = new PostgreSqlDatabaseProvisioner(configurations);
        var desired = new InMemoryDesiredStateStore(revisions, TimeProvider.System, provider);
        var environment = desired.AddEnvironment(EnvironmentId.Create("test-env"), "Test");
        desired.SetProviderAlias(environment.RecordId, ProviderAlias.Create("main"),
            new ProviderResolution("provider", 1, PostgreSqlDatabaseProvisioner.ImplementationId));
        Assert.False(await Exists(name));
        var change = Assert.IsType<CreateChangeSetResult.Created>(desired.CreateChangeSet(environment.RecordId,
            imported.ManifestRevisionRef.ApplicationRecordId, imported.ManifestRevisionRef,
            new DeploymentPrecondition.ExpectedAbsence(), "actor")).ChangeSet;
        var plan = Assert.IsType<PlanChangeSetResult.Planned>(desired.Plan(change.Id, "actor")).Plan;
        Assert.False(await Exists(name));
        Assert.IsType<CommitDesiredStateResult.Committed>(desired.Commit(change.Id, plan.Id, "actor"));
        Assert.False(await Exists(name));
        var generation = Assert.Single(desired.Generations);
        desired.SetProviderAlias(environment.RecordId, ProviderAlias.Create("main"),
            new ProviderResolution("provider", 2, PostgreSqlDatabaseProvisioner.ImplementationId));
        var executor = new PostgreSqlCheckpointExecutor(desired, revisions, provider);
        Assert.Equal(CheckpointExecutionState.Succeeded, (await executor.ExecuteAsync(generation)).State);
        Assert.True(await Exists(name));
        desired.SetProviderAlias(environment.RecordId, ProviderAlias.Create("main"),
            new ProviderResolution("provider", 1, PostgreSqlDatabaseProvisioner.ImplementationId));
        var equivalent = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
            await import.ExecuteAsync(Payload(name), "actor"));
        Assert.Equal(ImportApplicationManifestResult.ImportDisposition.ExistingEquivalent, equivalent.Disposition);
        var retryChange = Assert.IsType<CreateChangeSetResult.Created>(desired.CreateChangeSet(environment.RecordId,
            imported.ManifestRevisionRef.ApplicationRecordId, imported.ManifestRevisionRef,
            new DeploymentPrecondition.ExpectedGeneration(generation.Generation), "actor")).ChangeSet;
        var retryPlan = Assert.IsType<PlanChangeSetResult.Planned>(desired.Plan(retryChange.Id, "actor")).Plan;
        var retryCommit = Assert.IsType<CommitDesiredStateResult.Committed>(
            desired.Commit(retryChange.Id, retryPlan.Id, "actor"));
        Assert.True(retryCommit.Commit.NoOp);
        Assert.Equal(CheckpointExecutionState.Succeeded, (await executor.ExecuteAsync(generation)).State);
        Assert.Equal(1, Assert.Single(desired.Deployments).AppliedGeneration?.Value);
        Assert.Single(desired.Generations);
    }

    [Fact]
    public async Task Existing_database_is_a_conflict_and_not_adopted()
    {
        var name = Name();
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand("CREATE DATABASE \"" + name + "\"", connection);
            await create.ExecuteNonQueryAsync();
        }
        var provider = Provider();
        Assert.Equal(CheckpointProvisionOutcome.Conflict,
            await provider.CreateAsync(Selection(), name, default));
        Assert.True(await Exists(name));
    }

    [Theory]
    [InlineData("bad-name")]
    [InlineData("A")]
    [InlineData("a\";DROP DATABASE postgres;--")]
    [InlineData("")]
    public async Task Invalid_identifier_is_rejected_before_sql(string name)
    {
        Assert.Equal(CheckpointProvisionOutcome.PreDispatchFailure,
            await Provider().CreateAsync(Selection(), name, default));
    }

    [Fact]
    public async Task Missing_pinned_configuration_fails_without_database()
    {
        var name = Name();
        var provider = new PostgreSqlDatabaseProvisioner(new InMemoryPostgreSqlConfigurationCatalog());
        Assert.Equal(CheckpointProvisionOutcome.ConfigurationUnavailable,
            await provider.CreateAsync(Selection(), name, default));
        Assert.False(await Exists(name));
    }

    [Fact]
    public async Task Server_rejected_create_is_a_definitive_failure()
    {
        var name = Name();
        var readOnly = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c default_transaction_read_only=on"
        };
        var configurations = new InMemoryPostgreSqlConfigurationCatalog();
        configurations.Add("provider", 1, readOnly.ConnectionString);
        var provider = new PostgreSqlDatabaseProvisioner(configurations);
        Assert.Equal(CheckpointProvisionOutcome.ServerRejected,
            await provider.CreateAsync(Selection(), name, default));
        Assert.False(await Exists(name));
    }

    private PostgreSqlDatabaseProvisioner Provider()
    {
        var configurations = new InMemoryPostgreSqlConfigurationCatalog();
        configurations.Add("provider", 1, fixture.ConnectionString);
        return new(configurations);
    }

    private static ProviderResolution Selection() => new("provider", 1, PostgreSqlDatabaseProvisioner.ImplementationId);
    private static string Name() => "db_" + Guid.NewGuid().ToString("N")[..20];
    private static byte[] Payload(string name, string application = "checkpoint") => Encoding.UTF8.GetBytes(
        "{\"applicationVersion\":\"1\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"" + application + "\"},\"spec\":{\"resources\":{\"database\":{\"type\":\"postgresql.database/v1alpha1\",\"providerRef\":\"main\",\"properties\":{\"name\":\"" + name + "\"}}}}}}");

    private async Task<bool> Exists(string name)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", connection);
        query.Parameters.AddWithValue(name);
        return await query.ExecuteScalarAsync() is not null;
    }
}
