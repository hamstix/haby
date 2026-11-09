using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ExactActionAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resource_reuse_synthetic_typed_family_uses_common_provenance_without_database_or_login_contracts(bool mixed)
    {
        var boundary = new QueueBoundary { ExternalEvidence = true };
        var scenario = await GraphCase(boundary);
        await using (var execute = Services(scenario.Connection, boundary))
        {
            var runner = execute.GetRequiredService<DurableGenerationRunner>();
            var result = await runner.RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
            Assert.Equal(1, result.Progress!.AppliedGeneration);
        }
        for (var version = 2; version <= 3; version++)
        {
            await using var services = Services(scenario.Connection, boundary);
            var envelope = System.Text.Json.Nodes.JsonNode.Parse("""
                {"applicationVersion":"VERSION","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application",
                "metadata":{"id":"graph-app"},"spec":{"resources":{"jobs":{"type":"sample.queue/v1",
                "providerRef":"queues.primary","properties":{"queueName":"jobs"}}}}}}
                """.Replace("VERSION", "v" + version, StringComparison.Ordinal))!;
            if (mixed) envelope["manifest"]!["spec"]!["resources"]!["extra"] = envelope["manifest"]!["spec"]!["resources"]!["jobs"]!.DeepClone();
            var payload = Encoding.UTF8.GetBytes(envelope.ToJsonString());
            var catalog = new ManifestContractCatalog([new ResourceTypeDefinition(QueueContract, "{\"type\":\"object\"}")], [], [], []);
            var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                services.GetRequiredService<IManifestImportStore>(), catalog, TimeProvider.System).ExecuteAsync(payload, "tester"));
            var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
            var change = (await planner.CreateChangeSetAsync(scenario.Plan.Inputs.Environment, imported.ManifestRevisionRef.ApplicationRecordId,
                imported.ManifestRevisionRef, new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(version - 1)), "tester", default)).ChangeSet!;
            var plan = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
            Assert.NotNull(plan); Assert.Equal(mixed && version == 2 ? 1 : 0, plan.Graph.Nodes.Count);
            Assert.Equal(mixed && version == 3 ? 2 : 1, plan.Graph.ReusedResources.Count);
            Assert.All(plan.Graph.ReusedResources, r => Assert.Equal(r.Resource.Value == "extra" ? 2 : 1, r.SourceGeneration));
            var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, plan.Id, "tester", default));
            Assert.Equal(version, commit.Commit.Generation.Value);
            var result = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(commit.Commit.DeploymentId, commit.Commit.Generation, default);
            Assert.Null(result.StopOutcome); Assert.Equal(version, result.Progress!.AppliedGeneration); Assert.Equal(mixed && version == 2 ? 1 : 0, result.Progress.Total);
            var output = await services.GetRequiredService<IConfigurationOutputEvidence>().ReadAsync(new(commit.Commit.DeploymentId, commit.Commit.Generation, QueueEvidenceProducerId),
                new(scenario.Plan.Inputs.Environment, imported.ManifestRevisionRef.ApplicationRecordId, ManifestLocalId.Create("jobs")), Key,
                QueueEvidenceContract.Contract, "receipt", default);
            Assert.Equal("accepted-1", QueueEvidenceContract.Decode(output!)!.Receipt);
            if (mixed)
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
        Assert.Equal(3, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }
}
