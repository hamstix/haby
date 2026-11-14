using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ApplicationConfigurationTests
{
    [Fact]
    public async Task Mixed_preparation_consumes_original_confirmed_ApplicationLogin_outputs_without_reexecution_or_material_persistence()
    {
        var scenario = await Prepare(full: true, render: true);
        await using (var execute = Services(scenario.Connection, full: true, access: true))
            Assert.Null((await execute.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default)).StopOutcome);
        await using var services = Services(scenario.Connection, full: true, access: true, internalActions: true);
        var environment = scenario.Plan.Inputs.Environment;
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        await services.GetRequiredService<ISecretStore>().CreateAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator),
            secret, "fixture-token"u8.ToArray(), default);
        var configuration = new KubernetesProviderConfiguration(new Uri("https://127.0.0.1:6443"), null, secret, null, null);
        var providers = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await providers.CreateInstanceAsync(environment, configuration, default);
        await providers.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
        var alias = ProviderAlias.Create("kubernetes.primary");
        Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment,
            alias, null, new(instance, ProviderConfigurationRevisionNumber.Create(1)), "tester", default)).Outcome);
        var codec = new KubernetesPlacementCodec();
        var placement = codec.Decode("""{"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"standard","revision":1},"spec":{"namespace":"test"}}"""u8.ToArray());
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var reference = new PlacementRevisionKey(environment, alias, placement.Name, placement.Revision);
        await new PostgreSqlPlacementRevisionStore(contexts, codec).CompareAndCreateAsync(reference, placement, new("tester", DateTimeOffset.UtcNow), default);
        await new PostgreSqlPlacementSelectionStore(contexts, codec).ApplyAsync(PlacementSelectionTarget.Create("environment", "", "", ""), reference,
            new PlacementSelectionPrecondition.ExpectedAbsence(), new("tester", DateTimeOffset.UtcNow, null), default);
        var envelope = UpdateEnvelope(scenario, 2);
        foreach (var component in new[] { "api", "worker" })
            envelope["manifest"]!["spec"]!["components"]![component]!["workloads"] = JsonNode.Parse("""{"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary","properties":{"image":{"reference":"registry.example/app:v2"},"configuration":{"documents":["settings"]}}}}"""u8);
        var catalog = new ManifestContractCatalog([PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition],
            [KubernetesContract.Definition], [new(OutputContractId.Create(PostgreSqlApplicationConnectionContract.Contract), PostgreSqlConsumerOptions.Schema)],
            [new(PostgreSqlConnectionRendering.Renderer)]);
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), catalog, TimeProvider.System).ExecuteAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), "tester"));
        var accepted = await new InternalAcceptedGenerationBuilder(services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>(),
            (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>(), services.GetServices<IInternalActionHandler>().ToArray())
            .AcceptForEffectAsync(environment, imported.ManifestRevisionRef.ApplicationRecordId, imported.ManifestRevisionRef, scenario.Commit.Generation, "tester", default);
        var before = await ReuseHistory(scenario.Connection);
        var preparation = DurableDeploymentPlanningTests.ConfigurationPreparation(services, rendering: PostgreSqlConnectionRendering.CreateCatalog(),
            formatters: PostgreSqlConnectionRendering.FormatterCatalog());
        var result = await preparation.Preparation.PrepareAsync(accepted, "tester", default);
        Assert.Null(result.Failure); Assert.Equal(2, result.Prepared.Count);
        Assert.Equal(before, await ReuseHistory(scenario.Connection));
        string[] payloads;
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            var artifacts = await verify.ConfigurationDocuments.AsNoTracking().OrderBy(x => x.Component).ToArrayAsync();
            Assert.Equal(2, artifacts.Length);
            payloads = artifacts.Select(x => x.Payload).ToArray();
            foreach (var artifact in artifacts)
            {
                var revision = ConfigurationDocumentCodec.Decode(artifact.Payload, artifact.Fingerprint);
                Assert.Contains("***REDACTED***", ConfigurationJson.Preview(revision.Tree).Json);
                Assert.NotEqual("[]", revision.OutputAssociations);
            }
            Assert.Equal(2, await verify.WorkloadConfigurationRecipients.CountAsync());
            Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Empty(await verify.ActionIntents.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
        }
        var noEvidence = new NoConfigurationEvidence();
        var replay = await DurableDeploymentPlanningTests.ConfigurationPreparation(services, noEvidence).Preparation.PrepareAsync(accepted, "replay", default);
        Assert.Equal(result.Prepared, replay.Prepared); Assert.Equal(0, noEvidence.Reads);
        await using var check = await contexts.CreateDbContextAsync();
        Assert.Equal(payloads, await check.ConfigurationDocuments.AsNoTracking().OrderBy(x => x.Component).Select(x => x.Payload).ToArrayAsync());
        Assert.Equal(before, await ReuseHistory(scenario.Connection));
    }

    private sealed class NoConfigurationEvidence : IConfigurationOutputEvidence
    {
        internal int Reads;
        public ValueTask<ExternalOperationOutput?> ReadAsync(DurableOperationKey producer, ManagedResourceKey owner,
            DurableActionExecutionKey action, string contract, string key, CancellationToken token)
        { Reads++; throw new InvalidOperationException("Replay rerendered a saved artifact."); }
    }
}
