using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

[Collection("Catalog database group")]
public sealed partial class ResourceImportAuthorityTests(CatalogServerFixture fixture) : CatalogTestCase(fixture)
{
    private static readonly ProviderAdapterKey Provider = new("sample/v1", "sample.configuration/v1", "sample.impl/v1");
    private static readonly ResourceTypeId Type = ResourceTypeId.Create("sample.resource/v1");
    private static readonly ManifestLocalId Resource = ManifestLocalId.Create("existing");
    private static readonly ExternalOperationOutputIdentity Receipt = new("sample.observation/v1", "receipt");
    private static readonly DurableActionExecutionKey ImportKey = new(new(Type, Provider) { Mode = ResourceAcquisitionMode.ImportUseAndRetain }, OperationContractId.Create("sample.import/v1"));
    private static readonly ConfigurationRendererId RendererId = ConfigurationRendererId.Create("sample.import-render/v1");
    private static readonly ConfigurationFormatterId FormatterId = ConfigurationFormatterId.Create("sample.import-format/v1");
    private static readonly ManifestLocalId RendererImplementation = ManifestLocalId.Create("sample-import-v1");
    private sealed class Boundary { public int Observations; }
    public sealed record ImportInput(string Identity);

    private static ServiceProvider Services(string connection, Boundary boundary)
    {
        var services = new ServiceCollection().AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton(Rendering()).AddSingleton(boundary)
            .AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore()
            .AddPostgreSqlProviderConfigurationStore().AddSingleton<IProviderConfigurationCodec, SampleConfigurationCodec>()
            .AddPostgreSqlProviderAliasStore().AddPostgreSqlDurableDesiredStateCommitter().AddPostgreSqlDurableOperationJournal()
            .AddSingleton<DurableOperationSerialization>().AddSingleton<ExactDurableOperationExecutor>()
            .AddSingleton<IDurableOperationExecutor>(p => p.GetRequiredService<ExactDurableOperationExecutor>())
            .AddSingleton<DurableGenerationRunner>().AddSingleton<ImportHandler>()
            .AddSingleton<IDurableActionAdmissionHandler>(p => p.GetRequiredService<ImportHandler>())
            .AddSingleton<IDurableActionHandler>(p => p.GetRequiredService<ImportHandler>())
            .AddSingleton<IExternalOperationOutputContract, ImportOutput>()
            .AddSingleton<IDurableResourceReuseVerifier, SampleReuseVerifier>()
            .AddSingleton<IProviderRevisionPreflight, AcceptPreflight>();
        return services.BuildServiceProvider();
    }

    private static byte[] Manifest(string version, string properties = "{}")
    {
        var manifest = JsonNode.Parse("{\"applicationVersion\":\"" + version + "\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"import-app\"},\"spec\":{\"resources\":{\"existing\":{\"type\":\"sample.resource/v1\",\"providerRef\":\"sample.primary\",\"properties\":" + properties + "}}}}}")!;
        manifest["manifest"]!["spec"]!["components"] = JsonNode.Parse("""
            {"worker":{"documents":{"settings":{"fileName":"resource.json","format":"json","template":{}}},
              "bindings":{"use":{"target":{"kind":"local-resource","resourceRef":"existing"},"documentProjections":[
                {"documentRef":"settings","path":"/Resource","rendererRef":"sample.import-render/v1"}]}}}}
            """u8);
        return Encoding.UTF8.GetBytes(manifest.ToJsonString());
    }
    private static Task<ImportApplicationManifestResult> Import(ServiceProvider services, string version, string properties = "{}") =>
        new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(), new([new(Type, "{}")], [], [], [new(RendererId)]), TimeProvider.System)
            .ExecuteAsync(Manifest(version, properties), "tester").AsTask();

    [Fact]
    public async Task Imported_authority_survives_restart_reuse_and_refuses_changed_declaration()
    {
        var connection = await CreateDatabaseAsync("import_authority_");
        var boundary = new Boundary();
        var (committed, revision, environment) = await PrepareImportAsync(connection, boundary);
        await using (var restart = Services(connection, boundary))
        {
            var key = new DurableOperationKey(committed.DeploymentId, committed.Generation, OperationId.Create("verify-existing"));
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await restart.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(key, default)).Outcome);
            var result = await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(committed.DeploymentId, committed.Generation, default);
            Assert.Null(result.StopOutcome);
            Assert.Equal(1, result.Progress!.AppliedGeneration);
            var factory = restart.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            var saved = await restart.GetRequiredService<IDurableDesiredStateCommitter>().FindGenerationAsync(committed.DeploymentId, committed.Generation, default);
            Assert.Equal(ImportKey, Assert.Single(saved!.Inputs.Configuration!.RenderPins).ProducerAction);
            var documents = new PostgreSqlConfigurationDocumentStore(factory,
                new(Rendering(), Formatting(), restart.GetRequiredService<IConfigurationOutputEvidence>()), TimeProvider.System);
            var document = await documents.MaterializeAsync(new(committed.DeploymentId, committed.Generation,
                ManifestLocalId.Create("worker"), ManifestLocalId.Create("settings")), "tester", default);
            Assert.Equal(ConfigurationDocumentStatus.Created, document.Status);
            var deferred = Assert.IsType<ConfigurationDeferredValue>(Assert.IsType<ConfigurationObject>(document.Revision!.Tree).Members["Resource"]);
            Assert.Equal(FormatterId, deferred.Formatter);
            Assert.Equal(new ConfigurationString("external-42"), deferred.Arguments.Members["identity"]);
        }
        await using (var verify = Services(connection, boundary))
        await using (var db = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            var managed = await db.ResourceManagement.AsNoTracking().SingleAsync();
            Assert.Equal(ResourceOrigin.Imported, managed.Origin);
            Assert.Equal(ResourceLifecycleAuthority.UseAndRetain, managed.Authority);
            Assert.Equal(1, managed.SourceGeneration);
            Assert.Single(await db.OperationOutputs.AsNoTracking().ToArrayAsync());
            Assert.NotNull((await db.DurableOperationAttempts.AsNoTracking().SingleAsync()).DispatchedAt);
            Assert.Empty(await db.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
        }
        await using (var mutation = Services(connection, boundary))
        await using (var db = await mutation.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            var managed = await db.ResourceManagement.SingleAsync();
            managed.Authority = ResourceLifecycleAuthority.Provisioned;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var reuse = Services(connection, boundary))
        {
            var next = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await Import(reuse, "v2")).ManifestRevisionRef;
            var planner = reuse.GetRequiredService<IDurableDeploymentPlanner>();
            var change = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, next,
                new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(1)), "tester", default)).ChangeSet!;
            var result = await planner.CreatePlanAsync(change.Id, "tester", default);
            Assert.Equal(DurablePlanStatus.Planned, result.Status);
            Assert.Empty(result.Plan!.Graph.Nodes);
            Assert.Single(result.Plan.Graph.ReusedResources);
            Assert.IsType<DurableCommitResult.Committed>(await reuse.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, result.Plan.Id, "tester", default));
        }
        await using (var restart = Services(connection, boundary))
        {
            Assert.Null((await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(committed.DeploymentId, DeploymentGeneration.Create(2), default)).StopOutcome);
            var next = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await Import(restart, "v3", "{\"changed\":true}")).ManifestRevisionRef;
            var planner = restart.GetRequiredService<IDurableDeploymentPlanner>();
            var change = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, next,
                new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(2)), "tester", default)).ChangeSet!;
            Assert.Equal(DurablePlanStatus.UnsupportedShape, (await planner.CreatePlanAsync(change.Id, "tester", default)).Status);
        }
        Assert.Equal(1, boundary.Observations);
    }

    private static async Task<(DurableChangeSetCommit Commit, ManifestRevisionRef Revision, EnvironmentRecordId Environment)> PrepareImportAsync(string connection, Boundary boundary)
    {
        DurableChangeSetCommit committed;
        ManifestRevisionRef revision;
        EnvironmentRecordId environment;
        await using (var arrange = Services(connection, boundary))
        {
            await arrange.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            environment = await arrange.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            var configurations = arrange.GetRequiredService<IProviderConfigurationStore>();
            var configuration = new SampleConfiguration("observation-target", "sample.impl/v1");
            var instance = await configurations.CreateInstanceAsync(environment, configuration, default);
            await configurations.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
            await arrange.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment, ProviderAlias.Create("sample.primary"), null,
                new(instance, ProviderConfigurationRevisionNumber.Create(1)), "tester", default);
            revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await Import(arrange, "v1")).ManifestRevisionRef;
        }
        await using (var act = Services(connection, boundary))
        {
            var planner = act.GetRequiredService<IDurableDeploymentPlanner>();
            var invalid = new ResourceImportIntent([new(ManifestLocalId.Create("other"), new("sample.import-input/v1", "{}"))]);
            Assert.Equal(DurableChangeSetCreateStatus.ImportNotSupported, (await planner.CreateImportChangeSetAsync(environment,
                revision.ApplicationRecordId, revision, invalid, "tester", default)).Status);
            var invalidPayload = new ResourceImportIntent([new(Resource, new("sample.import-input/v1", "{\"Identity\":\"external-42\",\"unknown\":true}"))]);
            var invalidChange = (await planner.CreateImportChangeSetAsync(environment, revision.ApplicationRecordId, revision, invalidPayload, "tester", default)).ChangeSet!;
            Assert.Equal(DurablePlanStatus.UnsupportedShape, (await planner.CreatePlanAsync(invalidChange.Id, "tester", default)).Status);
            var intent = new ResourceImportIntent([new(Resource, new("sample.import-input/v1", "{\"Identity\":\"external-42\"}"))]);
            var change = (await planner.CreateImportChangeSetAsync(environment, revision.ApplicationRecordId, revision, intent, "tester", default)).ChangeSet!;
            var planned = await planner.CreatePlanAsync(change.Id, "tester", default);
            Assert.Equal(DurablePlanStatus.Planned, planned.Status);
            Assert.Equal(intent, planned.Plan!.Inputs.Import);
            Assert.Equal(ImportKey, Assert.Single(planned.Plan.Inputs.Configuration!.RenderPins).ProducerAction);
            Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(planned.Plan.Id, default));
            committed = Assert.IsType<DurableCommitResult.Committed>(await act.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(change.Id, planned.Plan.Id, "tester", default)).Commit;
            Assert.Equal(0, boundary.Observations);
        }
        return (committed, revision, environment);
    }

    [Fact]
    public async Task Import_output_publication_refuses_lost_authority_after_dispatch()
    {
        var connection = await CreateDatabaseAsync("import_corruption_");
        var boundary = new Boundary();
        var (committed, _, _) = await PrepareImportAsync(connection, boundary);
        var key = new DurableOperationKey(committed.DeploymentId, committed.Generation, OperationId.Create("verify-existing"));
        Guid attempt;
        await using (var dispatch = Services(connection, boundary))
        {
            var journal = dispatch.GetRequiredService<IDurableOperationJournal>();
            attempt = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(key, DateTimeOffset.UtcNow, default)).AttemptId;
            Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(key, attempt, DateTimeOffset.UtcNow, default));
            Assert.False(await journal.TryRecordSuccessAsync(key, attempt, DateTimeOffset.UtcNow, default));
        }
        await using (var corruption = Services(connection, boundary))
        await using (var db = await corruption.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("UPDATE resource_management SET origin = 'Provisioned', authority = 'Provisioned'");
        await using (var restart = Services(connection, boundary))
        {
            Assert.False(await restart.GetRequiredService<IExternalOperationOutputs>().CompleteAsync(key, attempt,
                new(Receipt.Contract, Receipt.Key, "external-42"), DateTimeOffset.UtcNow, default));
        }
        await using var verify = Services(connection, boundary);
        await using var verification = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verification.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Null((await verification.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Fact]
    public async Task Missing_original_import_output_or_dispatch_refuses_later_reuse_without_effects()
    {
        foreach (var missingOutput in new[] { true, false })
        {
            var connection = await CreateDatabaseAsync("import_evidence_");
            var boundary = new Boundary();
            var (committed, revision, environment) = await PrepareImportAsync(connection, boundary);
            await using (var execute = Services(connection, boundary))
                Assert.Null((await execute.GetRequiredService<DurableGenerationRunner>().RunAsync(committed.DeploymentId, committed.Generation, default)).StopOutcome);
            await using (var corruption = Services(connection, boundary))
            await using (var db = await corruption.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
                await db.Database.ExecuteSqlRawAsync(missingOutput
                    ? "DELETE FROM operation_outputs"
                    : "UPDATE durable_create_operation_attempts SET dispatched_at = NULL");
            await using (var restart = Services(connection, boundary))
            {
                var next = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await Import(restart, "v2")).ManifestRevisionRef;
                var planner = restart.GetRequiredService<IDurableDeploymentPlanner>();
                var change = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, next,
                    new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(1)), "tester", default)).ChangeSet!;
                Assert.Equal(DurablePlanStatus.PlanInputUnavailable, (await planner.CreatePlanAsync(change.Id, "tester", default)).Status);
            }
            await using var verify = Services(connection, boundary);
            await using var verification = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.Equal(1, await verification.DeploymentGenerations.CountAsync());
            Assert.Equal(1, (await verification.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
            Assert.Equal(1, boundary.Observations);
        }
    }

    private static ConfigurationFormatterCatalog Formatting() => new([new FormatDefinition()], [new(RendererImplementation, FormatterId)]);
    private static ConfigurationRenderingCatalog Rendering() => new([new RenderDefinition()], [new Renderer()], Formatting(), [new Formatter()]);
    private sealed record RenderDefinition() : ConfigurationRendererContract(RendererId, Type, Receipt.Contract, Receipt.Key, ImportKey, FormatterId)
    {
        public override ConfigurationObject Defaults => new([]);
        public override bool ValidateOptions(ConfigurationObject options, bool complete) => options.Members.Count == 0;
        public override ConfigurationObject NormalizeOptions(ConfigurationObject options) => options;
    }
    private sealed class Renderer : IConfigurationRenderer
    {
        public ConfigurationRendererId Contract => RendererId;
        public ManifestLocalId Identity => RendererImplementation;
        public ConfigurationFragment? Render(ExternalOperationOutput output, ConfigurationObject settings) =>
            output == new ExternalOperationOutput(Receipt.Contract, Receipt.Key, "external-42")
                ? new(new ConfigurationDeferredValue(FormatterId, new(new Dictionary<string, ConfigurationValue> { ["identity"] = new ConfigurationString("external-42") }))) : null;
    }
    private sealed record FormatDefinition() : ConfigurationFormatterContract(FormatterId)
    {
        public override bool ValidateArguments(ConfigurationObject arguments) => arguments.Members.Count == 1 &&
            arguments.Members.GetValueOrDefault("identity") is ConfigurationString { Value: "external-42" };
    }
    private sealed class Formatter : IConfigurationFormatter
    {
        public ConfigurationFormatterId Contract => FormatterId;
        public ManifestLocalId Identity => RendererImplementation;
        public ConfidentialConfigurationString? Format(ConfigurationObject arguments, ReadOnlySpan<char> material) =>
            new FormatDefinition().ValidateArguments(arguments) ? new("external-42") : null;
    }

    private sealed class AcceptPreflight : IProviderRevisionPreflight
    {
        public ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment, ProviderAliasBinding target, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProviderPreflightResult(ProviderPreflightOutcome.Ready));
    }

    private sealed class ImportHandler(IDurableOperationJournal journal, IExternalOperationOutputs outputs, Boundary boundary)
        : IDurableActionAdmissionHandler, IDurableActionHandler, IDurableResourceReusePolicy, IDurableResourceRetentionPolicy
    {
        public DurableActionExecutionKey Key => ImportKey;
        public bool RequiresExternalOutput => true;
        public ExternalOperationOutputIdentity OutputIdentity => Receipt;
        private static ImportInput? Decode(DurableActionPayload payload)
        {
            if (payload.Format != "sample.import-input/v1") return null;
            try { return JsonSerializer.Deserialize(payload.Json, ImportJsonContext.Default.ImportInput); }
            catch (JsonException) { return null; }
        }
        public bool Validate(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) =>
            inputs.Import?.Resources.SingleOrDefault(x => x.Resource == resourceId) is { } request && Decode(request.Input)?.Identity == "external-42";
        public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs)
        {
            var provider = inputs.Providers.Resource(resourceId);
            return [new(OperationId.Create("verify-existing"), resourceId, Key,
                new(provider.Environment, provider.Instance, provider.Configuration, provider.Implementation.Value),
                inputs.Import!.Resources.Single(x => x.Resource == resourceId).Input, [])
                { RequiresExternalOutput = true, RequiredOutput = Receipt }];
        }
        public bool Supports(DurableOperationNode node) => node.Action == Key && Decode(node.Input)?.Identity == "external-42";
        public bool CanReuse(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
            target.Resources.ContainsKey(resource);
        public bool CanRetain(NormalizedManifestSpec original, NormalizedManifestSpec target, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
            !target.Resources.ContainsKey(resource);
        public async ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken cancellationToken)
        {
            var started = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(operation.Key, DateTimeOffset.UtcNow, cancellationToken));
            Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(operation.Key, started.AttemptId, DateTimeOffset.UtcNow, cancellationToken));
            Interlocked.Increment(ref boundary.Observations);
            Assert.True(await outputs.CompleteAsync(operation.Key, started.AttemptId,
                new(Receipt.Contract, Receipt.Key, "external-42"), DateTimeOffset.UtcNow, cancellationToken));
            return new(DurableExecutionOutcome.Succeeded, await journal.FindAsync(operation.Key, cancellationToken));
        }
    }
    private sealed class SampleReuseVerifier : IDurableResourceReuseVerifier
    {
        public DurableActionAdmissionKey Key => ImportKey.Admission;
        public ValueTask<ResourceReuseValidationStatus> VerifyAsync(AcceptedOperation original, ResourceImportReceipt receipt, CancellationToken token) =>
            ValueTask.FromResult(original.Node.Action == ImportKey && receipt.Evidence == new DurableActionPayload("haby.import-observation/v1", "{}")
                ? ResourceReuseValidationStatus.Confirmed : ResourceReuseValidationStatus.InputUnavailable);
    }
    private sealed class ImportOutput : IExternalOperationOutputContract
    {
        public bool ValidateImportEvidence(AcceptedOperation accepted, DurableActionPayload evidence) =>
            accepted.Node.Action == ImportKey && evidence == new DurableActionPayload("haby.import-observation/v1", "{}");
        public DurableActionExecutionKey Producer => ImportKey;
        public ExternalOperationOutputIdentity Identity => Receipt;
        public bool Validate(DurableOperationNode node, ExternalOperationOutput output) => node.Action == ImportKey && output == new ExternalOperationOutput(Receipt.Contract, Receipt.Key, "external-42");
    }
    [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true)]
    [JsonSerializable(typeof(ImportInput))]
    internal partial class ImportJsonContext : JsonSerializerContext;
}
