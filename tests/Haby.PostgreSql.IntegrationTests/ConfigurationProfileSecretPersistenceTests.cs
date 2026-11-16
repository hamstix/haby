using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Haby.Tests.ProfilePersistenceSamples;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ConfigurationProfilePersistenceTests
{
    [Fact]
    public async Task Planning_resolves_only_configuration_metadata_and_freezes_exact_version_across_restart()
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
        async Task<ConfigurationPlanningSnapshot?> Capture()
        {
            await using var services = Services(connection);
            await using var db = Context(connection);
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
        await using (var restarted = new ServiceCollection().AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore().BuildServiceProvider())
        {
            Assert.Equal(frozen.FrozenSecrets[named], (await Capture())!.FrozenSecrets[named]);
            Assert.Equal(SecretPurpose.Configuration,
                (await restarted.GetRequiredService<SecretAdministration>().GetAsync("staging", "logging-password", null, default)).Secret!.Purpose);
            using var replacementBytes = new SecretMaterial("replacement-private"u8.ToArray());
            var replacement = await restarted.GetRequiredService<SecretAdministration>().CreateAsync("staging", "logging-password-v2",
                Guid.NewGuid(), replacementBytes, "operator", default, SecretPurpose.Configuration);
            Assert.Equal(SecretOutcome.Created, replacement.Outcome);
            var nextProfile = profileText.Replace("\"revision\":1", "\"revision\":2", StringComparison.Ordinal)
                .Replace("logging-password", "logging-password-v2", StringComparison.Ordinal);
            var imported = Assert.IsType<ImportConfigurationProfileResult.Succeeded>(await new ImportConfigurationProfile(
                restarted.GetRequiredService<IConfigurationProfileImportStore>(), new Clock(Time))
                .ExecuteAsync(Encoding.UTF8.GetBytes(nextProfile), "author"));
            var prior = (await AssignmentAdmin(restarted).GetAsync("staging", new ProfileAssignmentTarget.Environment())).Assignment!;
            var updated = await AssignmentAdmin(restarted).ApplyAsync(AssignmentBytes(new ProfileAssignmentTarget.Environment(),
                new ProfileDecision.Use(imported.Revision.Reference)), "staging", null,
                new AssignmentPrecondition.ExpectedConcurrencyToken(prior.Token), "operator");
            Assert.Equal(AssignmentOutcome.Updated, updated.Outcome);
            var changed = (await Capture())!;
            Assert.NotEqual(frozen.FrozenSecrets[named], changed.FrozenSecrets[new("logging-password-v2", 1)]);
            Assert.Equal(frozen.FrozenSecrets[named], ConfigurationPlanningSnapshotCodec.Decode(encoded, applicationRef).FrozenSecrets[named]);
            using var retained = await restarted.GetRequiredService<ISecretStore>().ResolveBoundedAsync(
                new(new SecretOwner.Environment(environment.RecordId), SecretPurpose.Configuration), frozen.FrozenSecrets[named], 65_536, default);
            Assert.NotNull(retained);
            Assert.Equal("canary-private"u8.ToArray(), retained.CopyBytes());
        }
    }
}
