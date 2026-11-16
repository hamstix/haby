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
        DurablePlan acceptedPlan;
        DurableChangeSetCommit committed;
        ConfigurationDocumentTarget savedTarget;
        ConfigurationDocumentRevision savedDocument;
        var planningReads = new SecretMaterialReads();
        await using (var planning = DurableServices(connection, planningReads))
        {
            var planner = planning.GetRequiredService<IDurableDeploymentPlanner>();
            var change = (await planner.CreateChangeSetAsync(environment.RecordId, applicationRef.ApplicationRecordId,
                applicationRef, new DeploymentPrecondition.ExpectedAbsence(), "operator", default)).ChangeSet!;
            var result = await planner.CreatePlanAsync(change.Id, "operator", default);
            Assert.Equal(DurablePlanStatus.Planned, result.Status);
            acceptedPlan = result.Plan!;
            Assert.Equal(frozen.FrozenSecrets[named], acceptedPlan.Inputs.Configuration!.FrozenSecrets[named]);
            committed = Assert.IsType<DurableCommitResult.Committed>(await planning.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(change.Id, acceptedPlan.Id, "operator", default)).Commit;
            Assert.Equal(frozen.FrozenSecrets[named], (await planning.GetRequiredService<IDurableDesiredStateCommitter>()
                .FindGenerationAsync(committed.DeploymentId, committed.Generation, default))!.Inputs.Configuration!.FrozenSecrets[named]);
            var progress = await planning.GetRequiredService<DurableGenerationRunner>()
                .RunAsync(committed.DeploymentId, committed.Generation, default);
            Assert.Equal(committed.Generation.Value, progress.Progress!.AppliedGeneration);
            savedTarget = new(committed.DeploymentId, committed.Generation, ManifestLocalId.Create("api"), ManifestLocalId.Create("settings"));
            var document = await planning.GetRequiredService<IConfigurationDocumentStore>().MaterializeAsync(savedTarget, "operator", default);
            Assert.Equal(ConfigurationDocumentStatus.Created, document.Status);
            savedDocument = document.Revision!;
            var node = Assert.IsType<ConfigurationProjectedSecret>(Assert.IsType<ConfigurationObject>(
                Assert.IsType<ConfigurationObject>(Assert.IsType<ConfigurationArray>(
                Assert.IsType<ConfigurationObject>(Assert.IsType<ConfigurationObject>(savedDocument.Tree)
                    .Members["Serilog"]).Members["WriteTo"]).Items[0]).Members["Args"]).Members["password"]);
            Assert.Equal(frozen.FrozenSecrets[named], node.Reference);
            Assert.Equal(ProfileSecretFormat.String, node.Format);
            Assert.DoesNotContain("canary-private", savedDocument.FrozenInputs);
            Assert.Equal(0, planningReads.Count);
        }
        var materialReads = new SecretMaterialReads();
        await using (var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + connection,
            "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
            services.AddDbContextFactory<ManifestCatalogDbContext>(options => options.AddInterceptors(materialReads))))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var admin = OperatorTestCredentials.Client(channel);
            var selector = new Haby.Operator.Contracts.ConfigurationDocumentRequest
            {
                Environment = "staging", ApplicationId = "sample", ComponentId = "api", DocumentId = "settings",
                Generation = committed.Generation.Value
            };
            var preview = await admin.PreviewConfigurationAsync(selector);
            Assert.Equal(Haby.Operator.Contracts.ConfigurationDocumentStatus.Found, preview.Status);
            Assert.Contains("/Serilog/WriteTo/0/Args/password", preview.PreviewJson.ToStringUtf8());
            Assert.DoesNotContain("canary-private", preview.PreviewJson.ToStringUtf8());
            Assert.Equal(0, materialReads.Count);
            var grant = await admin.CreateConfigurationAccessAsync(new() { Selector = selector });
            Assert.Equal(Haby.Operator.Contracts.ConfigurationAccessStatus.Created, grant.Status);
            var client = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
            var headers = new Metadata { { "authorization", "Bearer " + grant.Credential } };
            async Task Denied(ConfigurationDocumentRevisionId id)
            {
                var failure = await Assert.ThrowsAsync<RpcException>(async () => await client.DeliverAsync(
                    new() { RevisionId = id.ToString() }, headers));
                Assert.Equal(StatusCode.PermissionDenied, failure.StatusCode);
            }
            await Denied(ConfigurationDocumentRevisionId.New());
            Assert.Equal(0, materialReads.Count);
            var delivered = await client.DeliverAsync(new() { RevisionId = savedDocument.Metadata.Id.ToString() }, headers);
            using (var json = JsonDocument.Parse(delivered.Json.Memory))
                Assert.Equal("canary-private", json.RootElement.GetProperty("Serilog"u8).GetProperty("WriteTo"u8)[0]
                    .GetProperty("Args"u8).GetProperty("password"u8).GetString());
            Assert.Equal(1, materialReads.Count);
            Assert.False((await admin.RevokeConfigurationAccessAsync(new() { GrantId = grant.Grant.GrantId })).HasCredential);
            await Denied(savedDocument.Metadata.Id);
            Assert.Equal(1, materialReads.Count);
            await host.StopAsync();
        }
        await using (var restarted = DurableServices(connection))
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
            var loadedPlan = (await restarted.GetRequiredService<IDurableDeploymentPlanner>().FindPlanAsync(acceptedPlan.Id, default))!;
            Assert.Equal(frozen.FrozenSecrets[named], loadedPlan.Inputs.Configuration!.FrozenSecrets[named]);
            var loadedGeneration = (await restarted.GetRequiredService<IDurableDesiredStateCommitter>()
                .FindGenerationAsync(committed.DeploymentId, committed.Generation, default))!;
            Assert.Equal(frozen.FrozenSecrets[named], loadedGeneration.Inputs.Configuration!.FrozenSecrets[named]);
            var loadedDocument = (await restarted.GetRequiredService<IConfigurationDocumentStore>().ReadAsync(savedTarget, default)).Revision!;
            Assert.Equal(savedDocument, loadedDocument);
            Assert.Equal(savedDocument.Metadata.Fingerprint, loadedDocument.Metadata.Fingerprint);
            using var retained = await restarted.GetRequiredService<ISecretStore>().ResolveBoundedAsync(
                new(new SecretOwner.Environment(environment.RecordId), SecretPurpose.Configuration), frozen.FrozenSecrets[named], 65_536, default);
            Assert.NotNull(retained);
            Assert.Equal("canary-private"u8.ToArray(), retained.CopyBytes());
        }
    }

    private static ServiceProvider DurableServices(string connection, SecretMaterialReads? reads = null) => new ServiceCollection()
        .AddSingleton<TimeProvider>(new Clock(Time))
        .AddSingleton(ConfigurationRenderingCatalog.Empty)
        .AddPostgreSqlManifestCatalog(connection)
        .AddDbContextFactory<ManifestCatalogDbContext>(options => { if (reads is not null) options.AddInterceptors(reads); })
        .AddPostgreSqlPlaintextSecretStore()
        .AddPostgreSqlProviderConfigurationStore()
        .AddPostgreSqlProviderAliasStore()
        .AddPostgreSqlProviderConfigurationCodec()
        .AddPostgreSqlProviderRevisionPreflight()
        .AddPostgreSqlDurableOperationJournal()
        .AddPostgreSqlDurableOperationExecution()
        .AddPostgreSqlDurableDesiredStateCommitter()
        .AddSingleton<IConfigurationDocumentStore>(services => new PostgreSqlConfigurationDocumentStore(
            services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
            new(ConfigurationRenderingCatalog.Empty, new([], []), services.GetRequiredService<IConfigurationOutputEvidence>()),
            services.GetRequiredService<TimeProvider>()))
        .BuildServiceProvider();

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
