using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Theory]
    [InlineData("postgresql.primary", false)]
    [InlineData("kubernetes.primary", false)]
    [InlineData("kubernetes.primary", true)]
    public async Task Parsed_current_workloads_still_refuse_durable_planning_before_acceptance(string alias, bool workloadOnly)
    {
        var setup = await PrepareCommitAsync();
        await using var services = Services(setup.Connection, setup.Time);
        var envelope = JsonNode.Parse(Envelope)!.AsObject();
        envelope["applicationVersion"] = "workload-v1";
        if (workloadOnly) envelope["manifest"]!["spec"]!["resources"] = new JsonObject();
        envelope["manifest"]!["spec"]!["components"] = JsonNode.Parse("""{"api":{"workloads":{"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary","placementRef":"standard","properties":{"containerName":"orders-service","image":{"name":"orders","tagFrom":"applicationVersion"},"overrides":{"replicas":2}}}}}}"""u8);
        envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["providerRef"] = alias;
        var catalog = new ManifestContractCatalog([PostgreSqlDatabaseContract.Definition], [KubernetesContract.Definition], [], []);
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), catalog, setup.Time).ExecuteAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), "tester"));
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(setup.Environment, imported.ManifestRevisionRef.ApplicationRecordId,
            imported.ManifestRevisionRef, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
        Assert.Equal(DurablePlanStatus.UnsupportedShape, (await planner.CreatePlanAsync(change.Id, "tester", default)).Status);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verify.DurablePlans.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.DeploymentGenerations.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.DurableOperations.AsNoTracking().ToListAsync());
    }
    [Fact]
    public async Task Old_normalized_model_refuses_startup_without_reset_or_backfill()
    {
        var setup = await PrepareCommitAsync();
        await using var services = Services(setup.Connection, setup.Time);
        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        await using (var arrange = await factory.CreateDbContextAsync())
        {
            await arrange.Database.OpenConnectionAsync();
            await arrange.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
            await arrange.Revisions.ExecuteUpdateAsync(set => set.SetProperty(row => row.NormalizedModelVersion, 2)
                .SetProperty(row => row.SnapshotFormatVersion, 2));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ManifestCatalogMigrationInitializer(factory).MigrateAsync());
        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.Revisions.AsNoTracking().SingleAsync();
        Assert.Equal(2, row.NormalizedModelVersion); Assert.Equal(2, row.SnapshotFormatVersion);
        Assert.Contains("sample_db", row.Snapshot, StringComparison.Ordinal);
    }

}
