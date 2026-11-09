using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ExactActionAdmissionTests
{
    private sealed record DefaultQueueDefinition(string Mode) : ConfigurationRendererContract(QueueRenderer, QueueContract,
        QueueEvidenceContract.Contract, "receipt", QueueEvidenceProducerId, Key, QueueFormatter)
    {
        public override ConfigurationObject Defaults => new(new Dictionary<string, ConfigurationValue> { ["mode"] = new ConfigurationString(Mode) });
        public override bool ValidateOptions(ConfigurationObject options, bool complete) =>
            options.Members.Keys.All(x => x == "mode") && (!complete || options.Members.Count == 1) &&
            (options.Members.Count == 0 || options.Members["mode"] is ConfigurationString { Value: "original" or "changed" });
        public override ConfigurationObject NormalizeOptions(ConfigurationObject options) => options;
    }
    private sealed class DefaultQueueRenderer : IConfigurationRenderer
    {
        public ConfigurationRendererId Contract => QueueRenderer;
        public ManifestLocalId Identity => QueueImplementation;
        public ConfigurationFragment? Render(ExternalOperationOutput output, ConfigurationObject options) => new(new ConfigurationDeferredValue(QueueFormatter,
            new(new Dictionary<string, ConfigurationValue> { ["mode"] = options.Members["mode"], ["receipt"] = new ConfigurationString(QueueEvidenceContract.Decode(output)!.Receipt) })));
    }
    private sealed record DefaultQueueFormatDefinition() : ConfigurationFormatterContract(QueueFormatter)
    {
        public override bool ValidateArguments(ConfigurationObject arguments) => arguments.Members.Count == 2 &&
            arguments.Members.GetValueOrDefault("mode") is ConfigurationString && arguments.Members.GetValueOrDefault("receipt") is ConfigurationString;
    }
    private static ConfigurationFormatterCatalog DefaultQueueFormatting() => new([new DefaultQueueFormatDefinition()], [new(QueueImplementation, QueueFormatter)]);
    private static ConfigurationRenderingCatalog DefaultQueueCatalog(string mode) => new([new DefaultQueueDefinition(mode)],
        [new DefaultQueueRenderer()], DefaultQueueFormatting(), [new QueueNeverFormatter()]);

    [Fact]
    public async Task Configuration_only_new_plans_capture_compatible_defaults_old_plans_generations_and_documents_keep_frozen_values()
    {
        var boundary = new QueueBoundary { ExternalEvidence = true };
        var scenario = await GraphCase(boundary);
        await using (var execute = Services(scenario.Connection, boundary))
            Assert.Equal(1, (await execute.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default)).Progress!.AppliedGeneration);
        var original = DefaultQueueCatalog("original"); var changed = DefaultQueueCatalog("changed");
        DurablePlan saved;
        await using (var arrange = Services(scenario.Connection, boundary, rendering: original))
        {
            var envelope = Encoding.UTF8.GetBytes("""
                {"applicationVersion":"v2","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application",
                "metadata":{"id":"graph-app"},"spec":{"resources":{"jobs":{"type":"sample.queue/v1",
                "providerRef":"queues.primary","properties":{"queueName":"jobs"}}},"components":{"worker":{
                "documents":{"settings":{"fileName":"queue.json","format":"json","template":{}}},
                "bindings":{"use":{"target":{"kind":"local-resource","resourceRef":"jobs"},"documentProjections":[
                {"documentRef":"settings","path":"/Queue","rendererRef":"sample.queue-render/v1"}]}}}}}}}
                """);
            var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                arrange.GetRequiredService<IManifestImportStore>(), new([new(QueueContract, "{\"type\":\"object\"}")], [], [], [new(QueueRenderer)]),
                TimeProvider.System).ExecuteAsync(envelope, "tester"));
            saved = await Plan(arrange, imported.ManifestRevisionRef, 1);
        }
        // A new compatible build must validate and Commit the saved Plan's defaults, not today's defaults.
        await using (var act = Services(scenario.Connection, boundary, rendering: changed))
        {
            var planner = act.GetRequiredService<IDurableDeploymentPlanner>();
            Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(saved.Id, default));
            var commit = Assert.IsType<DurableCommitResult.Committed>(await act.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(saved.ChangeSetId, saved.Id, "tester", default)).Commit;
            Assert.Equal(2, commit.Generation.Value);
            Assert.Equal(2, (await act.GetRequiredService<DurableGenerationRunner>().RunAsync(commit.DeploymentId, commit.Generation, default)).Progress!.AppliedGeneration);
            var store = new PostgreSqlConfigurationDocumentStore(act.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
                new(changed, DefaultQueueFormatting(), act.GetRequiredService<IConfigurationOutputEvidence>()), TimeProvider.System);
            var oldTarget = new ConfigurationDocumentTarget(commit.DeploymentId, commit.Generation, ManifestLocalId.Create("worker"), ManifestLocalId.Create("settings"));
            var old = await store.MaterializeAsync(oldTarget, "tester", default); Assert.Equal(ConfigurationDocumentStatus.Created, old.Status);
            Assert.Equal(new ConfigurationString("original"), Mode(old.Revision!));
            var fresh = await Plan(act, saved.Inputs.Revision, 2);
            Assert.Equal(saved.Inputs.Revision, fresh.Inputs.Revision);
            Assert.Equal(new ConfigurationString("changed"), Assert.Single(fresh.Inputs.Configuration!.RenderPins).Defaults.Members["mode"]);
            Assert.Equal(new ConfigurationString("original"), Assert.Single(saved.Inputs.Configuration!.RenderPins).Defaults.Members["mode"]);
            var next = Assert.IsType<DurableCommitResult.Committed>(await act.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(fresh.ChangeSetId, fresh.Id, "tester", default)).Commit;
            Assert.Equal(3, next.Generation.Value);
            Assert.Empty(fresh.Graph.Nodes); Assert.Single(fresh.Graph.ReusedResources);
            Assert.Equal(3, (await act.GetRequiredService<DurableGenerationRunner>().RunAsync(next.DeploymentId, next.Generation, default)).Progress!.AppliedGeneration);
            var materialized = await store.MaterializeAsync(oldTarget with { Generation = next.Generation }, "tester", default);
            Assert.Equal(ConfigurationDocumentStatus.Created, materialized.Status); Assert.Equal(new ConfigurationString("changed"), Mode(materialized.Revision!));
            Assert.Equal(old.Revision, (await store.MaterializeAsync(oldTarget, "retry", default)).Revision);
            var noop = await Plan(act, saved.Inputs.Revision, 3);
            var receipt = Assert.IsType<DurableCommitResult.Committed>(await act.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(noop.ChangeSetId, noop.Id, "tester", default));
            Assert.Equal(3, receipt.Commit.Generation.Value);
            Assert.Equal(saved.Inputs.Configuration, (await planner.FindPlanAsync(saved.Id, default))!.Inputs.Configuration);
        }
        Assert.Equal(1, boundary.Calls);
        await using var verify = OutputContext(scenario.Connection);
        Assert.Equal(3, await verify.DeploymentGenerations.CountAsync()); Assert.Equal(2, await verify.DurableOperationAttempts.CountAsync());
        Assert.Single(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());

        static ConfigurationValue Mode(ConfigurationDocumentRevision document) => Assert.IsType<ConfigurationDeferredValue>(
            Assert.IsType<ConfigurationObject>(document.Tree).Members["Queue"]).Arguments.Members["mode"];

        async Task<DurablePlan> Plan(ServiceProvider services, ManifestRevisionRef revision, int generation)
        {
            var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
            var change = (await planner.CreateChangeSetAsync(scenario.Plan.Inputs.Environment, revision.ApplicationRecordId, revision,
                new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(generation)), "tester", default)).ChangeSet!;
            var result = await planner.CreatePlanAsync(change.Id, "tester", default); Assert.Equal(DurablePlanStatus.Planned, result.Status);
            return result.Plan!;
        }
    }
}
