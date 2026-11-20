using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

[Collection("Catalog database group")]
public sealed class ProtectedApplicationCredentialTests(CatalogServerFixture fixture) : CatalogTestCase(fixture)
{
    [Fact]
    public async Task Supported_unrelated_provider_revisions_do_not_block_import_schema_startup()
    {
        var connection = await CreateDatabaseAsync("unrelated_provider_");
        EnvironmentRecordId environment;
        ProviderInstanceId instance;
        var configuration = new SampleConfiguration("unrelated-target", "sample.impl/v1");
        await using (var arrange = Services(connection))
        {
            await using (var db = await arrange.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
                await db.GetService<IMigrator>().MigrateAsync("20261120122700_PersistResourceImportAuthority");
            environment = await arrange.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            var providers = arrange.GetRequiredService<IProviderConfigurationStore>();
            instance = await providers.CreateInstanceAsync(environment, configuration, default);
            await providers.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
        }
        await using (var act = Services(connection))
            await act.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await using (var verify = Services(connection))
        {
            Assert.Equal(configuration, (await verify.GetRequiredService<IProviderConfigurationStore>().FindRevisionAsync<SampleConfiguration>(
                environment, instance, ProviderConfigurationRevisionNumber.Create(1), default))!.Configuration);
            await using var db = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.Empty(await db.ResourceImportReceipts.AsNoTracking().ToArrayAsync());
            Assert.Empty(await db.ProtectedApplicationCredentials.AsNoTracking().ToArrayAsync());
        }
    }
    [Fact]
    public async Task Private_staging_survives_restart_and_conflicts_without_publishing_a_credential()
    {
        var connection = await CreateDatabaseAsync("protected_import_");
        ManagedResourceKey owner;
        await using (var arrange = Services(connection))
        {
            await arrange.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var environment = await arrange.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            await using var db = await arrange.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            var app = new ApplicationRow { Id = Guid.CreateVersion7(), ApplicationId = "existing-app" };
            db.Applications.Add(app);
            await db.SaveChangesAsync();
            owner = new(environment, ApplicationRecordId.Parse(app.Id.ToString()), ManifestLocalId.Create("login"));
        }
        var request = Guid.NewGuid();
        ProtectedCredentialReference reference;
        using var material = new SecretMaterial(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString() + "; existing password"));
        await using (var act = Services(connection))
        {
            var staged = await act.GetRequiredService<IProtectedApplicationCredentialIntake>().StageAsync(request, owner, material, default);
            Assert.Equal(ProtectedCredentialStageStatus.Staged, staged.Status);
            reference = Assert.IsType<ProtectedCredentialReference>(staged.Reference);
        }
        await using (var restart = Services(connection))
        {
            var intake = restart.GetRequiredService<IProtectedApplicationCredentialIntake>();
            Assert.Equal(new(ProtectedCredentialStageStatus.Existing, reference), await intake.StageAsync(request, owner, material, default));
            Assert.Equal(ProtectedCredentialStageStatus.Conflict, (await intake.StageAsync(Guid.NewGuid(), owner, material, default)).Status);
            Assert.Equal(ProtectedCredentialStageStatus.Conflict, (await intake.StageAsync(request, owner with { ResourceLocalId = ManifestLocalId.Create("other") }, material, default)).Status);
            using var changed = new SecretMaterial(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
            Assert.Equal(ProtectedCredentialStageStatus.Conflict, (await intake.StageAsync(request, owner, changed, default)).Status);
            using var resolved = await intake.ResolveStagedAsync(reference, owner, default);
            Assert.NotNull(resolved);
            Assert.Null(await restart.GetRequiredService<IApplicationCredentialReader>().FindAsync(owner, reference.Reference, default));
            using var unavailable = await restart.GetRequiredService<ISecretStore>().ResolveAsync(new(new SecretOwner.Resource(owner), SecretPurpose.ApplicationLogin), reference.Reference, default);
            Assert.Null(unavailable);
        }
        await using (var verify = Services(connection))
        await using (var db = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.Single(await db.ProtectedApplicationCredentials.AsNoTracking().Select(x => x.Id).ToArrayAsync());
            Assert.Empty(await db.Secrets.AsNoTracking().ToArrayAsync());
            Assert.Empty(await db.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
            Assert.Empty(await db.OperationOutputs.AsNoTracking().ToArrayAsync());
            Assert.Empty(await db.ResourceImportReceipts.AsNoTracking().ToArrayAsync());
        }
    }

    private static ServiceProvider Services(string connection) => new ServiceCollection()
        .AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore()
        .AddPostgreSqlProviderConfigurationStore().AddSingleton<IProviderConfigurationCodec, SampleConfigurationCodec>().BuildServiceProvider();
}
