using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

/// <summary>Catalog atomicity only. Stubbed observations do not qualify external PostgreSQL verification.</summary>
[Collection("Catalog database group")]
public sealed class ResourceImportAcceptanceTests(CatalogServerFixture fixture) : CatalogTestCase(fixture)
{
    [Fact]
    public async Task Entire_import_pair_credential_outputs_and_receipt_commit_once_or_remain_unusable()
    {
        var connection = await CreateDatabaseAsync("import_acceptance_");
        DurableChangeSetCommit commit;
        DurablePlan plan;
        ProtectedCredentialReference staged;
        ManagedResourceKey owner;
        var input = new PostgreSqlExistingPairInput("db", "app-login", "existing_database", 123, "app_login", 124, "public", 125, 125);
        using var password = new SecretMaterial(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString() + ";existing"));
        await using (var arrange = Services(connection))
        {
            await arrange.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var environment = await arrange.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            var envelope = PostgreSqlPairPlanningTests.Example();
            envelope["manifest"]!["spec"]!["resources"]!["app-login"]!["properties"]!["schema"] = "public";
            envelope["manifest"]!["spec"]!["components"] = JsonNode.Parse("""
                {"worker":{"documents":{"settings":{"fileName":"resource.json","format":"json","template":{}}},
                  "bindings":{"use":{"target":{"kind":"local-resource","resourceRef":"app-login"},"documentProjections":[
                    {"documentRef":"settings","path":"/Connection","rendererRef":"postgresql.npgsql-connection/v1alpha1"}]}}}}
                """u8);
            var revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                arrange.GetRequiredService<IManifestImportStore>(), new([PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition],
                    [], [], [new(PostgreSqlConnectionRendering.Renderer)]), TimeProvider.System)
                .ExecuteAsync(PostgreSqlPairPlanningTests.Bytes(envelope), "tester")).ManifestRevisionRef;
            owner = new(environment, revision.ApplicationRecordId, ManifestLocalId.Create("app-login"));
            staged = (await arrange.GetRequiredService<IProtectedApplicationCredentialIntake>().StageAsync(Guid.NewGuid(), owner, password, default)).Reference!;
            var admin = new SecretRef(SecretId.New(), SecretVersionId.New());
            await arrange.GetRequiredService<ISecretStore>().CreateAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator),
                admin, [1], default);
            var configuration = new PostgreSqlProviderConfiguration(PostgreSqlProviderConfiguration.CurrentShapeVersion, "localhost", 5432, "postgres", "postgres", admin, PostgreSqlDatabaseCheckpointCapability.ImplementationId, PostgreSqlTransportMode.Disable);
            var providers = arrange.GetRequiredService<IProviderConfigurationStore>();
            var instance = await providers.CreateInstanceAsync(environment, configuration, default);
            await providers.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await arrange.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment,
                ProviderAlias.Create("postgresql.primary"), null, new(instance, ProviderConfigurationRevisionNumber.Create(1)), "tester", default)).Outcome);
            var intent = new ResourceImportIntent([new(ManifestLocalId.Create("db"), PostgreSqlImportContracts.Encode(input)),
                new(owner.ResourceLocalId, PostgreSqlImportContracts.Encode(input), staged)]);
            var planner = arrange.GetRequiredService<IDurableDeploymentPlanner>();
            var change = (await planner.CreateImportChangeSetAsync(environment, revision.ApplicationRecordId, revision, intent, "tester", default)).ChangeSet!;
            var result = await planner.CreatePlanAsync(change.Id, "tester", default);
            Assert.Equal(DurablePlanStatus.Planned, result.Status);
            plan = result.Plan!;
            Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(plan.Id, default));
            commit = Assert.IsType<DurableCommitResult.Committed>(await arrange.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(change.Id, plan.Id, "tester", default)).Commit;
        }
        var confirmations = new List<ObservationOperationConfirmation>();
        await using (var dispatch = Services(connection))
        {
            var journal = dispatch.GetRequiredService<IDurableOperationJournal>();
            foreach (var node in plan.Graph.Nodes)
            {
                var key = new DurableOperationKey(commit.DeploymentId, commit.Generation, node.Id);
                var begin = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(key, DateTimeOffset.UtcNow, default));
                Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(key, begin.AttemptId, DateTimeOffset.UtcNow, default));
                confirmations.Add(new(key, begin.AttemptId, node.ResourceId.Value == "db"
                    ? PostgreSqlDatabaseOutputContract.Encode(new(input.DatabaseName, input.DatabaseOid, input.PublicSchemaOid))
                    : PostgreSqlApplicationConnectionContract.Encode(new("localhost", 5432, input.DatabaseName, input.Schema, input.LoginName, staged.Reference))));
            }
            var service = dispatch.GetRequiredService<IResourceObservationAcceptance>();
            var request = new ResourceObservationAcceptance(confirmations, [new(owner, staged)],
                PostgreSqlImportOutputContract.Reservations(plan.Inputs, input), Observation(input));
            Assert.False((await service.CompleteAsync(request with { Operations = [confirmations[0]] }, DateTimeOffset.UtcNow, default)).IsSuccess);
            var corrupt = confirmations.Select(x => x.Key.OperationId.Value == "db-import"
                ? x with { Output = PostgreSqlDatabaseOutputContract.Encode(new("wrong_database", 123, 125)) } : x).ToArray();
            Assert.False((await service.CompleteAsync(request with { Operations = corrupt }, DateTimeOffset.UtcNow, default)).IsSuccess);
            Assert.False((await service.CompleteAsync(request with { Evidence = new("unknown.observation/v1", "{}") }, DateTimeOffset.UtcNow, default)).IsSuccess);
        }
        await AssertUnconfirmed(connection, staged);
        var acceptance = new ResourceObservationAcceptance(confirmations, [new(owner, staged)],
            PostgreSqlImportOutputContract.Reservations(plan.Inputs, input), Observation(input));
        await using (var act = Services(connection))
        {
            // Concurrent exact replay must converge; an ignored successful response is recovered after restart below.
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => act.GetRequiredService<IResourceObservationAcceptance>()
                .CompleteAsync(acceptance, DateTimeOffset.UtcNow, default).AsTask()));
            Assert.All(results, x => Assert.True(x.IsSuccess));
        }
        Guid receipt;
        string receiptDocument;
        await using (var restart = Services(connection))
        {
            var service = restart.GetRequiredService<IResourceObservationAcceptance>();
            receipt = (await service.FindAsync(confirmations[0].Key, default))!.Id;
            Assert.True((await service.CompleteAsync(acceptance, DateTimeOffset.UtcNow, default)).IsSuccess);
            Assert.Equal(receipt, (await service.FindAsync(confirmations[1].Key, default))!.Id);
            Assert.Equal(new(owner, staged.Reference, ApplicationCredentialProvenance.Imported),
                await restart.GetRequiredService<IApplicationCredentialReader>().FindAsync(owner, staged.Reference, default));
            using var material = await restart.GetRequiredService<ISecretStore>().ResolveAsync(new(new SecretOwner.Resource(owner), SecretPurpose.ApplicationLogin), staged.Reference, default);
            Assert.NotNull(material);
            Assert.True(await restart.GetRequiredService<IDurableOperationJournal>().TryCompleteGenerationAsync(commit.DeploymentId, commit.Generation,
                restart.GetRequiredService<DurableActionAdmission>(), default));
            var documents = new PostgreSqlConfigurationDocumentStore(restart.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
                new(PostgreSqlConnectionRendering.CreateCatalog(), PostgreSqlConnectionRendering.FormatterCatalog(),
                    restart.GetRequiredService<IConfigurationOutputEvidence>()), TimeProvider.System);
            var document = await documents.MaterializeAsync(new(commit.DeploymentId, commit.Generation,
                ManifestLocalId.Create("worker"), ManifestLocalId.Create("settings")), "tester", default);
            Assert.Equal(ConfigurationDocumentStatus.Created, document.Status);
            Assert.Equal(PostgreSqlImportContracts.Login, Assert.Single(plan.Inputs.Configuration!.RenderPins).ProducerAction);
            var access = new PostgreSqlConfigurationAccessStore(restart.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), documents, TimeProvider.System);
            var grant = await access.CreateAsync(new(EnvironmentId.Create("pilot"), document.Revision!.Metadata.Application,
                ManifestLocalId.Create("worker"), ManifestLocalId.Create("settings"), commit.Generation), "tester", null, default);
            Assert.NotNull(grant);
            var delivery = new DeliverConfiguration(access, access, restart.GetRequiredService<ISecretStore>(),
                PostgreSqlConnectionRendering.CreateCatalog(), [new PostgreSqlConnectionResolutionBinding()]);
            using var denied = await delivery.ExecuteAsync("unrelated-credential", document.Revision.Metadata.Id, default);
            Assert.Equal(ConfigurationDeliveryStatus.Denied, denied.Status);
            using var delivered = await delivery.ExecuteAsync(grant.Credential, document.Revision.Metadata.Id, default);
            Assert.Equal(ConfigurationDeliveryStatus.Delivered, delivered.Status);
            using var json = JsonDocument.Parse(delivered.Json);
            var actual = new Npgsql.NpgsqlConnectionStringBuilder(json.RootElement.GetProperty("Connection"u8).GetString());
            Assert.Equal(input.LoginName, actual.Username);
            Assert.Equal(input.DatabaseName, actual.Database);
            var expected = password.CopyBytes();
            try { Assert.True(actual.Password == Encoding.UTF8.GetString(expected), "Authorized delivery must use the exact imported credential."); }
            finally { Array.Clear(expected); actual.Password = null; }
        }
        await using (var verify = Services(connection))
        await using (var db = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(2, await db.OperationOutputs.CountAsync());
            Assert.Equal(2, await db.ResourceManagement.CountAsync(x => x.Origin == ResourceOrigin.Imported && x.Authority == ResourceLifecycleAuthority.UseAndRetain));
            Assert.Equal(4, await db.ProviderResourceReservations.CountAsync());
            var savedReceipt = await db.ResourceObservationReceipts.AsNoTracking().SingleAsync();
            Assert.Equal(receipt, savedReceipt.Id);
            receiptDocument = savedReceipt.Document;
            Assert.Empty(await db.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
            Assert.All(await db.DurableOperationAttempts.AsNoTracking().ToArrayAsync(), x => Assert.Equal(nameof(DurableAttemptOutcome.Succeeded), x.Outcome));
        }
        // Historical confirmation is independent of the current association head.
        await using (var historical = Services(connection))
        await using (var db = await historical.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("DELETE FROM resource_management_heads");
        await using (var historical = Services(connection))
            Assert.Equal(receipt, (await historical.GetRequiredService<IResourceObservationAcceptance>().FindAsync(confirmations[0].Key, default))?.Id);
        await using (var corrupt = Services(connection))
        await using (var db = await corrupt.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE resource_import_receipts
                SET document = jsonb_set(document::jsonb, '{{EvidenceJson}}', '"{{}}"'::jsonb)::text
                """);
        await using (var restart = Services(connection))
            Assert.Null(await restart.GetRequiredService<IResourceObservationAcceptance>().FindAsync(confirmations[0].Key, default));
        await using (var restore = Services(connection))
        await using (var db = await restore.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE resource_import_receipts SET document = {receiptDocument}");
        await using (var corrupt = Services(connection))
        await using (var db = await corrupt.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("UPDATE protected_application_credentials SET confirmed_receipt_id = NULL");
        await using (var restart = Services(connection))
        {
            Assert.Null(await restart.GetRequiredService<IResourceObservationAcceptance>().FindAsync(confirmations[0].Key, default));
            Assert.Null(await restart.GetRequiredService<IApplicationCredentialReader>().FindAsync(owner, staged.Reference, default));
            using var unavailable = await restart.GetRequiredService<ISecretStore>().ResolveAsync(new(new SecretOwner.Resource(owner), SecretPurpose.ApplicationLogin), staged.Reference, default);
            Assert.Null(unavailable);
        }
    }

    private static async Task AssertUnconfirmed(string connection, ProtectedCredentialReference staged)
    {
        await using var services = Services(connection);
        await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await db.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.ResourceObservationReceipts.AsNoTracking().ToArrayAsync());
        Assert.Null((await db.ProtectedApplicationCredentials.AsNoTracking().Select(x => x.ConfirmedReceipt).SingleAsync()));
        Assert.False(await db.Secrets.AnyAsync(x => x.Id == Guid.Parse(staged.Reference.SecretId.ToString())));
    }

    private static DurableActionPayload Observation(PostgreSqlExistingPairInput input) => PostgreSqlImportObservation.Encode(
        new(input.DatabaseName, input.DatabaseOid, input.LoginName, input.LoginOid, input.Schema, input.SchemaOid, input.PublicSchemaOid,
            input.LoginName, input.LoginOid, "postgres", 10, "postgres", 10,
            null, "{=UC/postgres,postgres=UC/postgres}", "{=UC/postgres,postgres=UC/postgres}",
            true, false, false, false, false, false, false, true, true, true, true, true, false, false));

    private static ServiceProvider Services(string connection)
    {
        var services = new ServiceCollection().AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton(PostgreSqlConnectionRendering.CreateCatalog())
            .AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore().AddPostgreSqlProviderConfigurationStore()
            .AddPostgreSqlProviderAliasStore().AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
            .AddPostgreSqlProviderConfigurationCodec().AddSingleton<IProviderRevisionPreflight, AcceptPreflight>()
            .AddPostgreSqlDurableDesiredStateCommitter().AddPostgreSqlDurableOperationJournal().AddPostgreSqlResourceObservationAcceptance();
        foreach (var type in new[] { PostgreSqlDatabaseContract.Id, PostgreSqlApplicationLoginContract.Id })
        {
            services.AddSingleton<IDurableActionAdmissionHandler>(new PostgreSqlImportAdmission(type));
            services.AddSingleton<IDurableActionHandler>(new StubHandler(type));
            services.AddSingleton<IExternalOperationOutputContract>(p => new PostgreSqlImportOutputContract(type,
                p.GetRequiredService<PostgreSqlProviderConfigurationCodec>(), p.GetRequiredService<IApplicationCredentialReader>()));
        }
        return services.BuildServiceProvider();
    }

    private sealed class AcceptPreflight : IProviderRevisionPreflight
    {
        public ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment, ProviderAliasBinding target, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProviderPreflightResult(ProviderPreflightOutcome.Ready));
    }
    private sealed class StubHandler(ResourceTypeId type) : IDurableActionHandler
    {
        public DurableActionExecutionKey Key => type == PostgreSqlDatabaseContract.Id ? PostgreSqlImportContracts.Database : PostgreSqlImportContracts.Login;
        public bool RequiresExternalOutput => true;
        public ExternalOperationOutputIdentity OutputIdentity => type == PostgreSqlDatabaseContract.Id
            ? new(PostgreSqlDatabaseOutputContract.Contract, PostgreSqlDatabaseOutputContract.Key)
            : new(PostgreSqlApplicationConnectionContract.Contract, PostgreSqlApplicationConnectionContract.Key);
        public bool Supports(DurableOperationNode node) => node.Action == Key && PostgreSqlImportContracts.Decode(node) is not null;
        public ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Catalog fixture does not execute external verification.");
    }
}
