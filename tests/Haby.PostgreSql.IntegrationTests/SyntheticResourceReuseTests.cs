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
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Resource_reuse_synthetic_typed_family_uses_common_provenance_without_database_or_login_contracts(bool mixed, bool retain, bool dependent)
    {
        var boundary = new QueueBoundary { ExternalEvidence = true, DependentResources = dependent };
        var scenario = await GraphCase(boundary);
        await using (var execute = Services(scenario.Connection, boundary))
        {
            var runner = execute.GetRequiredService<DurableGenerationRunner>();
            var result = await runner.RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
            Assert.Equal(1, result.Progress!.AppliedGeneration);
        }
        var renderer = new QueueDocumentRenderer();
        var rendering = new ConfigurationRenderingCatalog([new QueueRenderDefinition()], [renderer], QueueFormatting(), [new QueueNeverFormatter()]);
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
            if (dependent && !absent)
            {
                var peer = envelope["manifest"]!["spec"]!["resources"]!["jobs"]!.DeepClone();
                peer["properties"] = System.Text.Json.Nodes.JsonNode.Parse("""{"parentRef":"extra"}"""u8);
                envelope["manifest"]!["spec"]!["resources"]!["extra-peer"] = peer;
            }
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
                binding["documentProjections"]![0]!["rendererRef"] = QueueRenderer.Value;
                var first = binding.DeepClone(); first["target"]!["resourceRef"] = "jobs";
                first["documentProjections"]![0]!["path"] = "/OriginalQueue";
                envelope["manifest"]!["spec"]!["components"]![component]!["bindings"]!["original"] = first;
            }
            var payload = Encoding.UTF8.GetBytes(envelope.ToJsonString());
            var catalog = new ManifestContractCatalog([new ResourceTypeDefinition(QueueContract, "{\"type\":\"object\"}")], [], [], [new(QueueRenderer)]);
            var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                services.GetRequiredService<IManifestImportStore>(), catalog, TimeProvider.System).ExecuteAsync(payload, "tester"));
            var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
            var change = (await planner.CreateChangeSetAsync(scenario.Plan.Inputs.Environment, imported.ManifestRevisionRef.ApplicationRecordId,
                imported.ManifestRevisionRef, new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(version - 1)), "tester", default)).ChangeSet!;
            var planned = await planner.CreatePlanAsync(change.Id, "tester", default);
            Assert.True(planned.Plan is not null, $"Generation {version}: {planned.Status}"); var plan = planned.Plan!;
            Assert.Equal(mixed && version == 2 ? dependent ? 2 : 1 : 0, plan.Graph.Nodes.Count);
            Assert.Equal(mixed && version >= 3 && !absent ? dependent ? 3 : 2 : 1, plan.Graph.ReusedResources.Count);
            Assert.Equal(absent ? dependent ? 2 : 1 : 0, plan.Graph.RetainedResources.Count);
            Assert.All(plan.Graph.RetainedResources, r => Assert.Equal(2, r.SourceGeneration));
            Assert.All(plan.Graph.ReusedResources, r => Assert.Equal(r.Resource.Value == "jobs" ? 1 : 2, r.SourceGeneration));
            if (dependent && version == 5) Assert.All(plan.Graph.ReusedResources.Where(r => r.SourceGeneration == 2), r => Assert.True(r.Reintroduced));
            if (absent)
            {
                // Removed keys are absent from the target. Their authority remains the
                // actual original generation, read independently from persisted storage.
                await using var readOriginal = OutputContext(scenario.Connection);
                var source = await readOriginal.DeploymentGenerations.AsNoTracking().SingleAsync(x =>
                    x.DeploymentId == Guid.Parse(scenario.Commit.DeploymentId.ToString()) && x.Generation == 2);
                var originalInputs = source.Inputs();
                Assert.All(plan.Graph.RetainedResources, retainedResource =>
                {
                    var originalProvider = originalInputs.Providers.Resource(retainedResource.Resource);
                    Assert.Equal(new ProviderInput.Resource(retainedResource.Resource), originalProvider.Input);
                    Assert.False(plan.Inputs.Providers.Entries.ContainsKey(originalProvider.Input));
                    Assert.Equal(scenario.Plan.Inputs.Providers.Resource(ManifestLocalId.Create("jobs")).Instance, originalProvider.Instance);
                    Assert.Equal(2, retainedResource.SourceGeneration);
                });
            }
            var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, plan.Id, "tester", default));
            Assert.Equal(version, commit.Commit.Generation.Value);
            Assert.Equal(component, Assert.Single(plan.Inputs.Configuration!.Layouts).Component.Value);
            Assert.Contains(Assert.Single(plan.Inputs.Configuration.Layouts).BindingRequests, r => r.BindingId.Value == (version == 2 ? "first" : "second"));
            Assert.All(plan.Inputs.Configuration.RenderPins, p => Assert.Equal(QueueRenderer, p.Renderer));
            if (mixed && version >= 3 && !absent)
            {
                Assert.Equal("extra-create", plan.Inputs.Configuration.RenderPins.Single(p => p.ResourceLocalId.Value == "extra").Producer.Value);
                Assert.Equal("10-external", plan.Inputs.Configuration.RenderPins.Single(p => p.ResourceLocalId.Value == "jobs").Producer.Value);
            }
            Assert.Contains(plan.Inputs.Configuration.RenderPins, p => p.ResourceLocalId.Value == (mixed && version >= 3 && !absent ? "extra" : "jobs"));
            var result = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(commit.Commit.DeploymentId, commit.Commit.Generation, default);
            Assert.Null(result.StopOutcome); Assert.Equal(version, result.Progress!.AppliedGeneration); Assert.Equal(mixed && version == 2 ? dependent ? 2 : 1 : 0, result.Progress.Total);
            await using (var proofContext = OutputContext(scenario.Connection))
            {
                var checkpoint = await proofContext.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.Generation == version);
                var proof = ResourcesReadyCheckpointCodec.Decode(checkpoint.EvidenceJson);
                Assert.Equal(version, proof.Generation); Assert.Equal(plan.Fingerprint, proof.InputFingerprint);
                Assert.Equal(mixed ? dependent ? 3 : 2 : 1, proof.Resources.Count);
                foreach (var resourceProof in proof.Resources)
                {
                    Assert.Equal(resourceProof.Resource == "jobs" ? 1 : 2, resourceProof.SourceGeneration);
                    Assert.Equal(resourceProof.Resource != "jobs" && absent, resourceProof.Retained);
                    Assert.Equal(resourceProof.Resource != "jobs" && retain && version == 5, resourceProof.Reintroduced);
                    var attempts = await proofContext.DurableOperationAttempts.AsNoTracking().Where(x => x.Generation == resourceProof.SourceGeneration).ToArrayAsync();
                    Assert.All(resourceProof.Operations, operation => Assert.Contains(attempts, x => x.OperationId == operation.OperationId && x.AttemptId == operation.AttemptId));
                }
            }
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
            if (mixed && version >= 3 && !absent)
            {
                var original = Assert.IsType<ConfigurationDeferredValue>(Assert.IsType<ConfigurationObject>(materialized.Revision!.Tree).Members["OriginalQueue"]);
                Assert.Equal(new ConfigurationString("accepted-1"), original.Arguments.Members["receipt"]);
            }
            if (mixed && !absent)
            {
                var added = await services.GetRequiredService<IConfigurationOutputEvidence>().ReadAsync(
                    new(commit.Commit.DeploymentId, commit.Commit.Generation, OperationId.Create("extra-create")),
                    new(scenario.Plan.Inputs.Environment, imported.ManifestRevisionRef.ApplicationRecordId, ManifestLocalId.Create("extra")),
                    Key, QueueEvidenceContract.Contract, "receipt", default);
                Assert.NotNull(added); Assert.Equal("accepted-2", QueueEvidenceContract.Decode(added)!.Receipt);
            }
        }
        Assert.Equal(mixed ? 2 : 1, boundary.Calls); Assert.Equal(dependent ? ["create:accepted-1", "subscribe:accepted-1", "create:accepted-2", "subscribe:accepted-2"] : mixed ? ["create:accepted-1", "subscribe:accepted-1", "create:accepted-2"] : ["create:accepted-1", "subscribe:accepted-1"], boundary.Effects);
        await using var verify = OutputContext(scenario.Connection);
        Assert.Equal(dependent ? 4 : mixed ? 3 : 2, await verify.DurableOperationAttempts.CountAsync()); Assert.Equal(mixed ? 2 : 1, await verify.OperationOutputs.CountAsync());
        Assert.Equal(retain ? 5 : 3, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }
}
