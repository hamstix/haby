using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.PostgreSql;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("HABY_POSTGRESQL_EPHEMERAL_EXTERNAL") == "1";
        var supplied = Environment.GetEnvironmentVariable("HABY_POSTGRESQL_EPHEMERAL_CONNECTION");
        if (supplied is not null && !external)
            throw new InvalidOperationException("An external connection requires explicit ephemeral opt-in.");
        if (external)
        {
            if (string.IsNullOrWhiteSpace(supplied))
                throw new InvalidOperationException("The ephemeral external connection is missing.");
            ConnectionString = supplied;
            // The WSLc helper starts the container; Npgsql readiness is checked here.
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(ConnectionString);
                    await connection.OpenAsync();
                    return;
                }
                catch (NpgsqlException)
                {
                    if (attempt == 29) throw new InvalidOperationException("The ephemeral PostgreSQL container did not become ready.");
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
            return;
        }
        _container = new PostgreSqlBuilder("postgres:17.6-alpine").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

public sealed class PostgreSqlIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly ManifestContractCatalog Catalog = new([PostgreSqlDatabaseContract.Definition], [], [], []);

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

    private PostgreSqlDatabaseProvisioner Provider()
    {
        var configurations = new InMemoryPostgreSqlConfigurationCatalog();
        configurations.Add("provider", 1, fixture.ConnectionString);
        return new(configurations);
    }

    private static ProviderResolution Selection() => new("provider", 1, PostgreSqlDatabaseProvisioner.ImplementationId);
    private static string Name() => "db_" + Guid.NewGuid().ToString("N")[..20];
    private static byte[] Payload(string name) => Encoding.UTF8.GetBytes(
        "{\"applicationVersion\":\"1\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"checkpoint\"},\"spec\":{\"resources\":{\"database\":{\"type\":\"postgresql.database/v1alpha1\",\"providerRef\":\"main\",\"properties\":{\"name\":\"" + name + "\"}}}}}}");

    private async Task<bool> Exists(string name)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", connection);
        query.Parameters.AddWithValue(name);
        return await query.ExecuteScalarAsync() is not null;
    }
}
