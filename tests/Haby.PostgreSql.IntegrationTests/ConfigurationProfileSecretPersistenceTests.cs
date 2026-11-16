using System.Text;
using System.Data.Common;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Configuration.Contracts;
using Haby.Domain;
using Haby.Manifest;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Haby.Tests.ProfilePersistenceSamples;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ConfigurationProfilePersistenceTests
{
    [Fact]
    public async Task Planning_resolves_only_configuration_metadata_without_material_access()
    {
        var connection = await Database();
        var profileText = """
            {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"logging","revision":1},"spec":{
              "documents":{"settings":{"fileName":"settings.json","format":"json","template":{}}},
              "staticContributions":{"logging":{"documentRef":"settings","path":"/Serilog","value":{"WriteTo":[{"Args":{}}]},
                "secretProjections":[{"path":"/WriteTo/0/Args/password","format":"string","secretRef":{"name":"logging-password","version":1}}]}}}}
            """;
        var manifestText = """
            {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},"spec":{"components":{"api":{}}}}}
            """;
        ManifestRevisionRef applicationRef;
        ProfileRevisionRef profileRef;
        EnvironmentMetadata environment;
        await using (var first = Services(connection))
        {
            var imported = Assert.IsType<ImportConfigurationProfileResult.Succeeded>(await new ImportConfigurationProfile(
                first.GetRequiredService<IConfigurationProfileImportStore>(), new Clock(Time))
                .ExecuteAsync(Encoding.UTF8.GetBytes(profileText), "author"));
            profileRef = imported.Revision.Reference;
            var app = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                first.GetRequiredService<IManifestImportStore>(), new ManifestContractCatalog([], [], [], []), new Clock(Time))
                .ExecuteAsync(Encoding.UTF8.GetBytes(manifestText), "author"));
            applicationRef = app.ManifestRevisionRef;
            environment = (await first.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("staging"), default)).Environment;
            var assigned = await AssignmentAdmin(first).ApplyAsync(AssignmentBytes(new ProfileAssignmentTarget.Environment(),
                new ProfileDecision.Use(profileRef)), "staging", null, new AssignmentPrecondition.ExpectedAbsence(), "operator");
            Assert.Equal(AssignmentOutcome.Created, assigned.Outcome);
        }
        var materialReads = new SecretMaterialReads();
        async Task<ConfigurationPlanningSnapshot?> Capture()
        {
            await using var services = Services(connection);
            await using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>()
                .UseNpgsql(connection).AddInterceptors(materialReads).Options);
            await using var transaction = await db.Database.BeginTransactionAsync();
            var app = (await services.GetRequiredService<IManifestRevisionQuery>().FindAsync(applicationRef))!;
            return (await ConfigurationPlanningCapture.CaptureAsync(db, environment, applicationRef, app.Declaration, default,
                ConfigurationRenderingCatalog.Empty)).Snapshot;
        }
        Assert.Null(await Capture());
        SecretMetadata created;
        await using (var secrets = new ServiceCollection().AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore().BuildServiceProvider())
        {
            using var bytes = new SecretMaterial("canary-private"u8.ToArray());
            var result = await secrets.GetRequiredService<SecretAdministration>().CreateAsync("staging", "logging-password",
                Guid.NewGuid(), bytes, "operator", default, SecretPurpose.Configuration);
            Assert.Equal(SecretOutcome.Created, result.Outcome);
            created = result.Secret!;
        }
        var frozen = (await Capture())!;
        var named = new ProfileNamedSecretRef("logging-password", 1);
        Assert.Equal(new SecretRef(created.SecretId, Assert.Single(created.Versions).VersionId), frozen.FrozenSecrets[named]);
        var encoded = ConfigurationPlanningSnapshotCodec.Encode(frozen);
        Assert.DoesNotContain("canary-private", encoded);
        Assert.Equal(frozen.FrozenSecrets[named], ConfigurationPlanningSnapshotCodec.Decode(encoded, applicationRef).FrozenSecrets[named]);
        Assert.Equal(SecretPurpose.Configuration, created.Purpose);
        Assert.Equal(0, materialReads.Count);
    }

    private sealed class SecretMaterialReads : DbCommandInterceptor
    {
        internal int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("secret_payloads", StringComparison.Ordinal) &&
                !command.CommandText.Contains("length(", StringComparison.OrdinalIgnoreCase)) Count++;
            return ValueTask.FromResult(result);
        }
    }
}
