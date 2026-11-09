using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ExactActionAdmissionTests
{
    private static readonly ConfigurationRendererId AddedQueueRenderer = ConfigurationRendererId.Create("sample.added-queue-render/v1");
    private sealed record AddedQueueDefinition() : ConfigurationRendererContract(AddedQueueRenderer, QueueContract, QueueEvidenceContract.Contract,
        "receipt", OperationId.Create("extra-create"), Key, QueueFormatter)
    {
        public override ConfigurationObject Defaults => new([]);
        public override bool ValidateOptions(ConfigurationObject options, bool complete) => options.Members.Count == 0;
        public override ConfigurationObject NormalizeOptions(ConfigurationObject options) => options;
    }
    private sealed class AddedQueueDocumentRenderer : IConfigurationRenderer
    {
        public ConfigurationRendererId Contract => AddedQueueRenderer;
        public ManifestLocalId Identity => QueueImplementation;
        public ConfigurationFragment? Render(ExternalOperationOutput output, ConfigurationObject options) => new QueueDocumentRenderer().Render(output, options);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Resource_reuse_synthetic_typed_family_uses_common_provenance_without_database_or_login_contracts(bool mixed, bool retain)
    {
        var boundary = new QueueBoundary { ExternalEvidence = true };
        var scenario = await GraphCase(boundary);
        await using (var execute = Services(scenario.Connection, boundary))
        {
            var runner = execute.GetRequiredService<DurableGenerationRunner>();
            var result = await runner.RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
            Assert.Equal(1, result.Progress!.AppliedGeneration);
        }
        var renderer = new QueueDocumentRenderer();
        var rendering = new ConfigurationRenderingCatalog([new QueueRenderDefinition(), new AddedQueueDefinition()], [renderer, new AddedQueueDocumentRenderer()], QueueFormatting(), [new QueueNeverFormatter()]);
        for (var version = 2; version <= (retain ? 5 : 3); version++)
        {
            await using var services = Services(scenario.Connection, boundary, rendering: rendering);
            var envelope = System.Text.Json.Nodes.JsonNode.Parse("""
                {"applicationVersion":"VERSION","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application",
                "metadata":{"id":"graph-app"},"spec":{"resources":{"jobs":{"type":"sample.queue/v1",
                "providerRef":"queues.primary","properties":{"queueName":"jobs"}}}}}}
                """.Replace("VERSION", "v" + version, StringComparison.Ordinal))!;
            var absent = retain && version is 3 or 4;
            if (mixed && !absent) envelope["manifest"]!["spec"]!["resources"]!["extra"] = envelope["manifest"]!["spec"]!["resources"]!["jobs"]!.DeepClone();
            var component = version == 2 ? "scheduler" : "worker";
            envelope["manifest"]!["spec"]!["components"] = System.Text.Json.Nodes.JsonNode.Parse("""
                {"COMPONENT":{"documents":{"settings":{"fileName":"queue.json","format":"json","template":{}}},
                "bindings":{"BINDING":{"target":{"kind":"local-resource","resourceRef":"jobs"},"documentProjections":[
                {"documentRef":"settings","path":"/Queue","rendererRef":"sample.queue-render/v1"}]}}}}
                """.Replace("COMPONENT", component, StringComparison.Ordinal).Replace("BINDING", version == 2 ? "first" : "second", StringComparison.Ordinal));
            if (mixed && version >= 3 && !absent)
            {
                // Retarget a changed Binding to the existing Resource created in the mixed generation.
                var binding = envelope["manifest"]!["spec"]!["components"]![component]!["bindings"]!["second"]!;
                binding["target"]!["resourceRef"] = "extra";
                binding["documentProjections"]![0]!["rendererRef"] = AddedQueueRenderer.Value;
            }
            var payload = Encoding.UTF8.GetBytes(envelope.ToJsonString());
            var catalog = new ManifestContractCatalog([new ResourceTypeDefinition(QueueContract, "{\"type\":\"object\"}")], [], [], [new(QueueRenderer), new(AddedQueueRenderer)]);
            var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                services.GetRequiredService<IManifestImportStore>(), catalog, TimeProvider.System).ExecuteAsync(payload, "tester"));
            var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
            var change = (await planner.CreateChangeSetAsync(scenario.Plan.Inputs.Environment, imported.ManifestRevisionRef.ApplicationRecordId,
                imported.ManifestRevisionRef, new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(version - 1)), "tester", default)).ChangeSet!;
            var plan = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
            Assert.NotNull(plan); Assert.Equal(mixed && version == 2 ? 1 : 0, plan.Graph.Nodes.Count);
            Assert.Equal(mixed && version >= 3 && !absent ? 2 : 1, plan.Graph.ReusedResources.Count);
            Assert.Equal(absent ? 1 : 0, plan.Graph.RetainedResources.Count);
            if (absent) Assert.Equal(2, Assert.Single(plan.Graph.RetainedResources).SourceGeneration);
            Assert.All(plan.Graph.ReusedResources, r => Assert.Equal(r.Resource.Value == "extra" ? 2 : 1, r.SourceGeneration));
            var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, plan.Id, "tester", default));
            Assert.Equal(version, commit.Commit.Generation.Value);
            Assert.Equal(component, Assert.Single(plan.Inputs.Configuration!.Layouts).Component.Value);
            Assert.Equal(version == 2 ? "first" : "second", Assert.Single(Assert.Single(plan.Inputs.Configuration.Layouts).BindingRequests).BindingId.Value);
            Assert.Equal(mixed && version >= 3 && !absent ? AddedQueueRenderer : QueueRenderer, Assert.Single(plan.Inputs.Configuration.RenderPins).Renderer);
            Assert.Equal(mixed && version >= 3 && !absent ? "extra" : "jobs", Assert.Single(plan.Inputs.Configuration.RenderPins).ResourceLocalId.Value);
            var result = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(commit.Commit.DeploymentId, commit.Commit.Generation, default);
            Assert.Null(result.StopOutcome); Assert.Equal(version, result.Progress!.AppliedGeneration); Assert.Equal(mixed && version == 2 ? 1 : 0, result.Progress.Total);
            var output = await services.GetRequiredService<IConfigurationOutputEvidence>().ReadAsync(new(commit.Commit.DeploymentId, commit.Commit.Generation, QueueEvidenceProducerId),
                new(scenario.Plan.Inputs.Environment, imported.ManifestRevisionRef.ApplicationRecordId, ManifestLocalId.Create("jobs")), Key,
                QueueEvidenceContract.Contract, "receipt", default);
            Assert.Equal("accepted-1", QueueEvidenceContract.Decode(output!)!.Receipt);
            var documents = new PostgreSqlConfigurationDocumentStore(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
                new(rendering, QueueFormatting(), services.GetRequiredService<IConfigurationOutputEvidence>()), TimeProvider.System);
            var materialized = await documents.MaterializeAsync(new(commit.Commit.DeploymentId, commit.Commit.Generation,
                ManifestLocalId.Create(component), ManifestLocalId.Create("settings")), "tester", default);
            Assert.Equal(ConfigurationDocumentStatus.Created, materialized.Status);
            var value = Assert.IsType<ConfigurationDeferredValue>(Assert.IsType<ConfigurationObject>(materialized.Revision!.Tree).Members["Queue"]);
            Assert.Equal(new ConfigurationString(mixed && version >= 3 && !absent ? "accepted-2" : "accepted-1"), value.Arguments.Members["receipt"]);
            if (mixed && !absent)
            {
                var added = await services.GetRequiredService<IConfigurationOutputEvidence>().ReadAsync(
                    new(commit.Commit.DeploymentId, commit.Commit.Generation, OperationId.Create("extra-create")),
                    new(scenario.Plan.Inputs.Environment, imported.ManifestRevisionRef.ApplicationRecordId, ManifestLocalId.Create("extra")),
                    Key, QueueEvidenceContract.Contract, "receipt", default);
                Assert.NotNull(added); Assert.Equal("accepted-2", QueueEvidenceContract.Decode(added)!.Receipt);
            }
        }
        Assert.Equal(mixed ? 2 : 1, boundary.Calls); Assert.Equal(mixed ? ["create:accepted-1", "subscribe:accepted-1", "create:accepted-2"] : ["create:accepted-1", "subscribe:accepted-1"], boundary.Effects);
        await using var verify = OutputContext(scenario.Connection);
        Assert.Equal(mixed ? 3 : 2, await verify.DurableOperationAttempts.CountAsync()); Assert.Equal(mixed ? 2 : 1, await verify.OperationOutputs.CountAsync());
        Assert.Equal(retain ? 5 : 3, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }
}
