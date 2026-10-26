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

public sealed class DurableDeploymentPlanningTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Implementation = PostgreSqlDatabaseCheckpointCapability.ImplementationId;
    private const string AnyObject = """{"type":"object"}""";
    private static readonly ManifestContractCatalog Catalog = new(
        [new(ResourceTypeId.Create("postgresql.database/v1alpha1"), AnyObject, AnyObject)],
        [], [], []);
    private static readonly byte[] Envelope = Encoding.UTF8.GetBytes(
        """{"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"operator-example"},"spec":{"resources":{"db":{"type":"postgresql.database/v1alpha1","providerRef":"postgresql.primary","properties":{"name":"sample_db"}}}}}}""");

    [Fact]
    public async Task Restart_alias_switch_replan_close_and_expiry_preserve_immutable_secret_free_plans()
    {
        var connection = await NewDatabaseAsync();
        var time = new AdjustableTimeProvider();
        var alias = ProviderAlias.Create("postgresql.primary");
        EnvironmentRecordId environment;
        ManifestRevisionRef revision;
        DeploymentChangeSetId changeSetId;
        DurablePlan first;
        ProviderAliasBinding oldBinding;
        ProviderAliasBinding newBinding;
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        await using (var services = Services(connection, time))
        {
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var secrets = services.GetRequiredService<ISecretStore>();
            environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
            await secrets.CreateAsync(environment, secret, Encoding.UTF8.GetBytes(admin.Password!), default);
            var provider = services.GetRequiredService<IProviderConfigurationStore>();
            var configuration = new PostgreSqlProviderConfiguration(1, admin.Host!, admin.Port,
                admin.Database!, admin.Username!, secret, Implementation);
            var instance = await provider.CreateInstanceAsync(environment, configuration, default);
            await provider.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1),
                configuration, default);
            await provider.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(2),
                configuration, default);
            oldBinding = new(instance, ProviderConfigurationRevisionNumber.Create(1));
            newBinding = new(instance, ProviderConfigurationRevisionNumber.Create(2));
            var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                services.GetRequiredService<IManifestImportStore>(), Catalog, time).ExecuteAsync(Envelope, "tester"));
            revision = imported.ManifestRevisionRef;
            var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
            var intent = await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, revision,
                new DeploymentPrecondition.ExpectedAbsence(), "tester", default);
            changeSetId = Assert.IsType<DurableChangeSet>(intent.ChangeSet).Id;
            Assert.Equal(DurableChangeSetCreateStatus.Created, intent.Status);
            Assert.Equal(DurablePlanStatus.AliasNotResolved,
                (await planner.CreatePlanAsync(changeSetId, "tester", default)).Status);
            Assert.Equal(ProviderAliasChangeOutcome.Changed,
                (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment, alias, null,
                    oldBinding, "tester", default)).Outcome);
            first = Assert.IsType<DurablePlan>((await planner.CreatePlanAsync(changeSetId, "tester", default)).Plan);
            Assert.Equal(time.GetUtcNow().AddHours(24), first.ExpiresAt);
            Assert.Equal(oldBinding.InstanceId, first.Inputs.ProviderInstance);
            Assert.Equal(oldBinding.RevisionNumber, first.Inputs.ConfigurationRevision);
            Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(first.Id, default));
        }
        await using (var restarted = Services(connection, time))
        {
            var planner = restarted.GetRequiredService<IDurableDeploymentPlanner>();
            Assert.Equal(first, await planner.FindPlanAsync(first.Id, default));
            Assert.NotNull(await planner.FindChangeSetAsync(changeSetId, default));
            Assert.Equal(ProviderAliasChangeOutcome.Changed,
                (await restarted.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment, alias,
                    oldBinding, newBinding, "tester", default)).Outcome);
            Assert.Equal(DurablePlanCurrentStatus.PlanStale, await planner.CheckPlanAsync(first.Id, default));
            var second = Assert.IsType<DurablePlan>((await planner.CreatePlanAsync(changeSetId, "tester", default)).Plan);
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(newBinding.RevisionNumber, second.Inputs.ConfigurationRevision);
            Assert.Equal(oldBinding.RevisionNumber, (await planner.FindPlanAsync(first.Id, default))!.Inputs.ConfigurationRevision);
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
                await planner.CreatePlanAsync(changeSetId, "tester", default)));
            Assert.All(concurrent, result => Assert.Equal(DurablePlanStatus.Planned, result.Status));
            Assert.NotEqual(concurrent[0].Plan!.Id, concurrent[1].Plan!.Id);
            Assert.Equal(DurablePlanCloseStatus.Closed, await planner.ClosePlanAsync(first.Id, default));
            Assert.Equal(DurablePlanCloseStatus.AlreadyClosed, await planner.ClosePlanAsync(first.Id, default));
            Assert.Equal(DurablePlanCurrentStatus.Closed, await planner.CheckPlanAsync(first.Id, default));
            time.Advance(TimeSpan.FromHours(24));
            Assert.Equal(DurablePlanCurrentStatus.Expired, await planner.CheckPlanAsync(second.Id, default));
            Assert.NotNull(await planner.FindPlanAsync(second.Id, default));
        }
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        var count = (long)(await new NpgsqlCommand("SELECT count(*) FROM deployment_plans", database).ExecuteScalarAsync())!;
        Assert.Equal(4, count);
        await using var overwrite = new NpgsqlCommand("UPDATE deployment_plans SET actor_id = 'replacement' WHERE id = @id", database);
        overwrite.Parameters.AddWithValue("id", Guid.Parse(first.Id.ToString()));
        await Assert.ThrowsAsync<PostgresException>(async () => await overwrite.ExecuteNonQueryAsync());
        var snapshot = (string)(await new NpgsqlCommand("SELECT string_agg(provider_alias || implementation_id || fingerprint, '') FROM deployment_plans", database).ExecuteScalarAsync())!;
        Assert.DoesNotContain("CREATE DATABASE", snapshot);
        var sensitiveColumns = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'deployment_plans' AND column_name ~ '(secret|password|payload|credential)'", database)
            .ExecuteScalarAsync())!;
        Assert.Equal(0, sensitiveColumns);
        await using var noCodec = Services(connection, time, registerCodec: false);
        Assert.Equal(DurablePlanStatus.ProviderRevisionUnavailable,
            (await noCodec.GetRequiredService<IDurableDeploymentPlanner>()
                .CreatePlanAsync(changeSetId, "tester", default)).Status);
        await using var noCapability = Services(connection, time, supportsCapability: false);
        Assert.Equal(DurablePlanStatus.ProviderCapabilityMismatch,
            (await noCapability.GetRequiredService<IDurableDeploymentPlanner>()
                .CreatePlanAsync(changeSetId, "tester", default)).Status);
    }

    [Fact]
    public async Task Wrong_environment_missing_revision_and_generation_are_typed_without_writes()
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection, new AdjustableTimeProvider());
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var environment = await services.GetRequiredService<ISecretStore>()
            .CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), Catalog, new AdjustableTimeProvider())
            .ExecuteAsync(Envelope, "tester"));
        var reference = imported.ManifestRevisionRef;
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        Assert.Equal(DurableChangeSetCreateStatus.EnvironmentNotFound,
            (await planner.CreateChangeSetAsync(EnvironmentRecordId.New(), reference.ApplicationRecordId,
                reference, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).Status);
        Assert.Equal(DurableChangeSetCreateStatus.RevisionNotOwnedByApplication,
            (await planner.CreateChangeSetAsync(environment, ApplicationRecordId.New(), reference,
                new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).Status);
        Assert.Equal(DurableChangeSetCreateStatus.RevisionNotFound,
            (await planner.CreateChangeSetAsync(environment, reference.ApplicationRecordId,
                ManifestRevisionRef.Create(reference.ApplicationRecordId, ManifestRevisionNumber.Create(99)),
                new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).Status);
        Assert.Equal(DurableChangeSetCreateStatus.ExpectedGenerationUnsupported,
            (await planner.CreateChangeSetAsync(environment, reference.ApplicationRecordId, reference,
                new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(1)), "tester", default)).Status);
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM deployment_change_sets", database).ExecuteScalarAsync())!);
    }

    private async Task<string> NewDatabaseAsync()
    {
        var name = "haby_plan_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    private static ServiceProvider Services(string connection, TimeProvider clock, bool registerCodec = true,
        bool supportsCapability = true)
    {
        var services = new ServiceCollection()
            .AddSingleton(clock)
            .AddPostgreSqlManifestCatalog(connection)
            .AddPostgreSqlPlaintextSecretStore()
            .AddPostgreSqlProviderConfigurationStore()
            .AddPostgreSqlProviderAliasStore();
        if (supportsCapability) services.AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>();
        else services.AddSingleton<ICheckpointProviderCapability, RejectCapability>();
        if (registerCodec) services.AddPostgreSqlProviderConfigurationCodec();
        return services.AddPostgreSqlProviderRevisionPreflight()
            .AddPostgreSqlDurableDeploymentPlanner().BuildServiceProvider();
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class RejectCapability : ICheckpointProviderCapability
    {
        public bool Supports(ProviderResolution selection, ResourceTypeId contract) => false;
    }
}
