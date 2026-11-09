using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Operator.Contracts;
using Haby.Configuration.Contracts;
using Haby.Operator.Host;
using System.Text.Json;
using ConfigurationDocumentStatus = Haby.Application.ConfigurationDocumentStatus;
using ProfileAssignmentTarget = Haby.Domain.ProfileAssignmentTarget;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ExactActionAdmissionTests
{
    private static readonly ConfigurationRendererId QueueRenderer = ConfigurationRendererId.Create("sample.queue-render/v1");
    private static readonly ConfigurationFormatterId QueueFormatter = ConfigurationFormatterId.Create("sample.queue-format/v1");
    private static readonly ManifestLocalId QueueImplementation = ManifestLocalId.Create("sample-queue-v1");
    private sealed record QueueRenderDefinition() : ConfigurationRendererContract(QueueRenderer, QueueContract, QueueEvidenceContract.Contract,
        "receipt", Key, QueueFormatter)
    {
        public override ConfigurationObject Defaults => new([]);
        public override bool ValidateOptions(ConfigurationObject options, bool complete) => options.Members.Count == 0;
        public override ConfigurationObject NormalizeOptions(ConfigurationObject options) => options;
    }
    private sealed record QueueFormatDefinition() : ConfigurationFormatterContract(QueueFormatter)
    {
        public override bool ValidateArguments(ConfigurationObject arguments) => arguments.Members.Count is 1 or 2 && arguments.Members["receipt"] is ConfigurationString &&
            (arguments.Members.Count == 1 || arguments.Members.GetValueOrDefault("credential") is ConfigurationSecretReference);
    }
    private sealed class QueueDocumentRenderer : IConfigurationRenderer
    {
        public int Count;
        public bool Inconsistent;
        public int Padding;
        public ConfigurationRendererId Contract => QueueRenderer;
        public ManifestLocalId Identity => QueueImplementation;
        public ConfigurationFragment? Render(ExternalOperationOutput confirmedOutput, ConfigurationObject settings)
        {
            var count = Interlocked.Increment(ref Count);
            var output = QueueEvidenceContract.Decode(confirmedOutput) ?? throw new InvalidDataException();
            var arguments = new Dictionary<string, ConfigurationValue>
            { ["receipt"] = new ConfigurationString(output.Receipt + new string('a', Padding) + (Inconsistent ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")) };
            if (output.CredentialSecretId is { } secret) arguments.Add("credential", new ConfigurationSecretReference(new(SecretId.Parse(secret), SecretVersionId.Parse(output.CredentialVersionId!))));
            return new(new ConfigurationDeferredValue(QueueFormatter, new(arguments)));
        }
    }
    private sealed class QueueNeverFormatter : IConfigurationFormatter
    {
        public ConfigurationFormatterId Contract => QueueFormatter;
        public ManifestLocalId Identity => QueueImplementation;
        public ConfidentialConfigurationString? Format(ConfigurationObject arguments, ReadOnlySpan<char> material) => throw new InvalidOperationException("Formatter must never run.");
    }
    private static ConfigurationFormatterCatalog QueueFormatting() => new([new QueueFormatDefinition()], [new(QueueImplementation, QueueFormatter)]);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Configuration_document_second_typed_family_crosses_common_composer_store_and_detects_inconsistent_concurrent_content(bool inconsistent, bool delivery)
    {
        var database = "document_queue_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        { await admin.OpenAsync(); await new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin).ExecuteNonQueryAsync(); }
        using var pools = new TestCatalogPools();
        var connection = pools.Register(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database });
        var renderer = new QueueDocumentRenderer { Inconsistent = inconsistent };
        var catalog = new ConfigurationRenderingCatalog([new QueueRenderDefinition()], [renderer], QueueFormatting(), [new QueueNeverFormatter()]);
        var boundary = new QueueBoundary { ExternalEvidence = true };
        DurableChangeSetCommit commit;
        await using (var services = Services(connection, boundary, rendering: catalog))
        {
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var environment = await services.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            var providers = services.GetRequiredService<IProviderConfigurationStore>(); var config = new SampleConfiguration("queue-broker", "sample.impl/v1");
            var instance = await providers.CreateInstanceAsync(environment, config, default);
            await providers.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), config, default);
            await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment, ProviderAlias.Create("queues.primary"), null,
                new(instance, ProviderConfigurationRevisionNumber.Create(1)), "tester", default);
            var profile = Assert.IsType<ImportConfigurationProfileResult.Succeeded>(await new ImportConfigurationProfile(services.GetRequiredService<IConfigurationProfileImportStore>(), TimeProvider.System).ExecuteAsync(Encoding.UTF8.GetBytes("""
                {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"queue-layout","revision":1},"spec":{
                "documents":{"settings":{"fileName":"settings.json","format":"json","template":{"ordinary":true}}},
                "bindingRules":{"queue":{"slot":"queue","rendererRef":"sample.queue-render/v1","documentProjections":[
                {"documentRef":"settings","path":"/Queues/First"},{"documentRef":"settings","path":"/Queues/Second"}]}}}}
                """), "tester"));
            var env = (await services.GetRequiredService<IEnvironmentCatalog>().GetAsync(EnvironmentId.Create("pilot"), default))!;
            await services.GetRequiredService<IProfileAssignmentStore>().ApplyAsync(env, new(new ProfileAssignmentTarget.Environment(), [new ProfileDecision.Use(profile.Revision.Reference)]),
                new AssignmentPrecondition.ExpectedAbsence(), new("tester", DateTimeOffset.UtcNow, null), default);
            var payload = Encoding.UTF8.GetBytes("""
                {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"queue-app"},"spec":{
                "resources":{"jobs":{"type":"sample.queue/v1","providerRef":"queues.primary","properties":{"queueName":"jobs"}}},
                "components":{"worker":{"bindings":{"queue":{"target":{"kind":"local-resource","resourceRef":"jobs"},"configurationSlot":"queue"}}}}}}}
                """);
            var manifestCatalog = new ManifestContractCatalog([new ResourceTypeDefinition(QueueContract, "{\"type\":\"object\"}")], [], [], [new(QueueRenderer)]);
            var revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(), manifestCatalog, TimeProvider.System).ExecuteAsync(payload, "tester")).ManifestRevisionRef;
            if (delivery)
            {
                var generated = await services.GetRequiredService<IGeneratedCredentialSlots>().GetOrCreateAsync(new(new(environment, revision.ApplicationRecordId, ManifestLocalId.Create("jobs")),
                    GeneratedCredentialSlot.ApplicationPassword), GeneratedCredentialSlot.RandomPasswordV1, default);
                Assert.Equal(GeneratedCredentialOutcome.Created, generated.Outcome); boundary.DeliveryCredential = generated.Reference!;
            }
            var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
            var change = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, revision, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
            var planned = await planner.CreatePlanAsync(change.Id, "tester", default);
            Assert.Equal(DurablePlanStatus.Planned, planned.Status);
            var plan = planned.Plan!;
            commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, plan.Id, "tester", default)).Commit;
            Assert.Equal(2, (await services.GetRequiredService<DurableGenerationRunner>().RunAsync(commit.DeploymentId, commit.Generation, default)).Progress!.Completed);
        }
        await using var act = Services(connection, boundary, rendering: catalog);
        var evidence = new QueueRenderBarrier(act.GetRequiredService<IConfigurationOutputEvidence>());
        var store = new PostgreSqlConfigurationDocumentStore(act.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), new(catalog, QueueFormatting(), evidence), TimeProvider.System);
        var target = new ConfigurationDocumentTarget(commit.DeploymentId, commit.Generation, ManifestLocalId.Create("worker"), ManifestLocalId.Create("settings"));
        var results = await Task.WhenAll(store.MaterializeAsync(target, "first", default).AsTask(), store.MaterializeAsync(target, "second", default).AsTask());
        Assert.Single(results, x => x.Status == ConfigurationDocumentStatus.Created);
        Assert.Single(results, x => x.Status == (inconsistent ? ConfigurationDocumentStatus.IntegrityConflict : ConfigurationDocumentStatus.Existing));
        Assert.Equal(2, renderer.Count); // Two candidates; each Binding fragment serves both explicit projections.
        var winner = results.Single(x => x.Status == ConfigurationDocumentStatus.Created).Revision!;
        var preview = ConfigurationJson.Preview(winner.Tree); Assert.Equal(new[] { "/Queues/First", "/Queues/Second" }, preview.RedactedPaths);
        Assert.DoesNotContain("accepted-1", preview.Json); Assert.Contains("ordinary", preview.Json);
        var repeat = await store.MaterializeAsync(target, "retry", default); Assert.Equal(winner, repeat.Revision); Assert.Equal(2, renderer.Count);
        if (!inconsistent)
        {
            // Exact internal payload budget through the actual common store. UUIDs and
            // round-trip UTC times have fixed encoded lengths; only receipt padding and
            // the trusted actor length vary. Two projections consume the identical fragment.
            var baselineBytes = Encoding.UTF8.GetByteCount(ConfigurationDocumentCodec.Encode(winner));
            var remaining = ConfigurationDocumentCodec.MaxBytes - baselineBytes;
            var actor = winner.Metadata.Actor + (remaining % 2 == 0 ? "" : "x");
            renderer.Padding = remaining / 2;
            await using (var arrange = OutputContext(connection)) await arrange.ConfigurationDocuments.ExecuteDeleteAsync();
            var exact = await store.MaterializeAsync(target, actor, default);
            Assert.Equal(ConfigurationDocumentStatus.Created, exact.Status);
            Assert.Equal(ConfigurationDocumentCodec.MaxBytes, Encoding.UTF8.GetByteCount(ConfigurationDocumentCodec.Encode(exact.Revision!)));
            Assert.Equal(exact.Revision, (await store.ReadAsync(target, default)).Revision);
            await using (var arrange = OutputContext(connection)) await arrange.ConfigurationDocuments.ExecuteDeleteAsync();
            // One additional ordinary ASCII actor byte makes the same candidate precisely one byte over.
            Assert.Equal(ConfigurationDocumentStatus.LimitExceeded, (await store.MaterializeAsync(target, actor + "x", default)).Status);
            await using (var verifyLimit = OutputContext(connection)) Assert.Equal(0, await verifyLimit.ConfigurationDocuments.AsNoTracking().CountAsync());
            renderer.Padding = 0;
            Assert.Equal(ConfigurationDocumentStatus.Created, (await store.MaterializeAsync(target, "restored-test-artifact", default)).Status);
        }
        await using (var verify = OutputContext(connection))
        { Assert.Equal(1, await verify.ConfigurationDocuments.AsNoTracking().CountAsync()); Assert.Equal(1, await verify.OperationOutputs.AsNoTracking().CountAsync()); }
        if (delivery) await QueueAuthorizedDelivery(connection, renderer);
    }

    private sealed class QueueDeliveryFormatter : IConfigurationFormatter, IConfigurationResolutionBinding
    {
        public ConfigurationFormatterId Contract => QueueFormatter;
        public ConfigurationFormatterId Formatter => QueueFormatter;
        public ManifestLocalId Identity => QueueImplementation;
        public ManifestLocalId Implementation => QueueImplementation;
        public int Calls;
        public bool ValidateMaterial(ReadOnlySpan<char> material) => material.Length > 0;
        public SecretResolutionReference? Bind(ConfigurationRenderPin pin, ExternalOperationOutput output, ConfigurationDeferredValue value)
        {
            if (pin.Renderer != QueueRenderer || pin.RendererImplementation != QueueImplementation || pin.Formatter != QueueFormatter ||
                pin.Producer != QueueEvidenceProducerId || pin.ProducerAction != Key || pin.Resource != QueueContract ||
                value.Formatter != QueueFormatter || !new QueueFormatDefinition().ValidateArguments(value.Arguments) ||
                QueueEvidenceContract.Decode(output) is not { CredentialSecretId: { } secret, CredentialVersionId: { } version } evidence ||
                value.Arguments.Members["receipt"] is not ConfigurationString receipt || receipt.Value != evidence.Receipt ||
                value.Arguments.Members.GetValueOrDefault("credential") is not ConfigurationSecretReference reference ||
                reference.Reference != new SecretRef(SecretId.Parse(secret), SecretVersionId.Parse(version))) return null;
            return new(reference.Reference, SecretPurpose.ApplicationLogin);
        }
        public ConfidentialConfigurationString? Format(ConfigurationObject arguments, ReadOnlySpan<char> material)
        {
            Calls++;
            return new("queue://" + ((ConfigurationString)arguments.Members["receipt"]).Value + "/" + material.ToString());
        }
    }
    private static async Task QueueAuthorizedDelivery(string connection, QueueDocumentRenderer renderer)
    {
        var formatter = new QueueDeliveryFormatter();
        // No renderer at delivery time. Actual production grant/store/Host are reused.
        var catalog = new ConfigurationRenderingCatalog([new QueueRenderDefinition()], [], QueueFormatting(), [formatter]);
        var count = renderer.Count;
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"],
            s => s.AddSingleton(catalog).AddSingleton(QueueFormatting()).AddSingleton<IConfigurationResolutionBinding>(formatter));
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var admin = OperatorTestCredentials.Client(channel); var runtime = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
        var grant = await admin.CreateConfigurationAccessAsync(new() { Selector = new() { Environment = "pilot", ApplicationId = "queue-app", ComponentId = "worker", DocumentId = "settings" } });
        Assert.Equal(ConfigurationAccessStatus.Created, grant.Status);
        for (var i = 0; i < 2; i++)
        {
            var reply = await runtime.DeliverAsync(new() { RevisionId = grant.Grant.Document.RevisionId }, new Metadata { { "authorization", "Bearer " + grant.Credential } });
            using var json = JsonDocument.Parse(reply.Json.Memory);
            Assert.True(json.RootElement.GetProperty("Queues"u8).GetProperty("First"u8).GetString() == json.RootElement.GetProperty("Queues"u8).GetProperty("Second"u8).GetString());
            Assert.True(json.RootElement.GetProperty("ordinary"u8).GetBoolean()); Assert.Equal(2 * (i + 1), formatter.Calls);
        }
        Assert.Equal(count, renderer.Count);
        await admin.RevokeConfigurationAccessAsync(new() { GrantId = grant.Grant.GrantId });
        var failure = await Assert.ThrowsAsync<RpcException>(async () => await runtime.DeliverAsync(new() { RevisionId = grant.Grant.Document.RevisionId }, new Metadata { { "authorization", "Bearer " + grant.Credential } }));
        Assert.Equal(StatusCode.PermissionDenied, failure.StatusCode);
        await host.StopAsync();
    }
    private sealed class QueueRenderBarrier(IConfigurationOutputEvidence inner) : IConfigurationOutputEvidence
    {
        private int _count;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ExternalOperationOutput?> ReadAsync(DurableOperationKey producer, ManagedResourceKey owner, DurableActionExecutionKey action, string contract, string key, CancellationToken token)
        {
            var output = await inner.ReadAsync(producer, owner, action, contract, key, token);
            if (Interlocked.Increment(ref _count) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), token); return output;
        }
    }
}
