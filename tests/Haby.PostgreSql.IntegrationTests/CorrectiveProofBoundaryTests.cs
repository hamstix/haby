using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Theory]
    [InlineData("different-valid-older-proof")]
    [InlineData("altered-origin-fingerprint")]
    [InlineData("broken-original-plan-link")]
    public async Task Older_Resource_shape_never_authorizes_substituted_plan_CAS_or_historical_source(string fault)
    {
        JsonNode? envelope = null;
        var scenario = await InternalScenarioAsync(configureManifest: e => envelope = e.DeepClone());
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var deployment = new ConfiguredSubmissionFixture();
        var actions = await PrepareRollout(services, scenario, deployment, new IdentityFixture(), configured: false);
        await SubmitRollout(actions, scenario, deployment);
        envelope!["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:proof-checked";
        var revision = await ImportUpdate(services, envelope, "proof-checked");
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var change = (await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, revision.ApplicationRecordId, revision,
            new(scenario.Commit.DeploymentId, scenario.Commit.Generation), Guid.CreateVersion7(), "operator", default)).ChangeSet!;
        var plan = (await planner.CreateOperatorPlanAsync(change.Id, "operator", default, handlers)).Plan!;
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        Guid fakeId; string forgedGraph; string forgedFingerprint;
        await using (var arrange = await contexts.CreateDbContextAsync())
        {
            var original = await arrange.DurablePlans.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(plan.Id.ToString()));
            var proofRow = await arrange.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.Generation == 1 && x.Stage == "ResourcesReady");
            Assert.NotEqual(plan.Graph.Correction!.Document.ResourcesReady, proofRow.Id);
            var older = await DurableAggregateCompletion.ReadAsync(arrange,
                await arrange.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == 1), default);
            Assert.Equal(proofRow.EvidenceJson, ResourcesReadyCheckpointCodec.Encode(older!));
            var document = fault switch
            {
                "different-valid-older-proof" => plan.Graph.Correction.Document with { ResourcesReady = older!.Id, ResourceProof = proofRow.EvidenceJson },
                "altered-origin-fingerprint" => plan.Graph.Correction.Document with { ResourcesReady = older!.Id, ResourceProof = proofRow.EvidenceJson, OriginalFingerprint = older.InputFingerprint },
                _ => plan.Graph.Correction.Document with { OriginalPlan = Guid.CreateVersion7() }
            };
            // Shape construction is deliberately not authority: even a valid older same-deployment proof must be recaptured.
            var graph = new DurableOperationGraph(plan.Graph.Nodes, plan.Graph.ReusedResources, plan.Graph.Baseline, plan.Graph.RetainedResources)
            { Acquisitions = plan.Graph.Acquisitions, Import = plan.Graph.Import, ObservationGroups = plan.Graph.ObservationGroups }.WithCorrection(new(document));
            forgedGraph = DurableOperationGraphCodec.Encode(graph); forgedFingerprint = DurableOperationGraphCodec.Fingerprint(original.Inputs(), graph);
            fakeId = Guid.CreateVersion7();
            arrange.DurablePlans.Add(new() { Id = fakeId, ChangeSetId = original.ChangeSetId, InternalEffects = true,
                EnvironmentRecordId = original.EnvironmentRecordId, EnvironmentKey = original.EnvironmentKey, ApplicationRecordId = original.ApplicationRecordId,
                RevisionNumber = original.RevisionNumber, SnapshotFormatVersion = original.SnapshotFormatVersion, ProviderInputsJson = original.ProviderInputsJson,
                GraphJson = forgedGraph, ConfigurationJson = original.ConfigurationJson, Fingerprint = forgedFingerprint,
                CreatedAt = original.CreatedAt, ExpiresAt = original.ExpiresAt, ActorId = original.ActorId });
            foreach (var pin in await arrange.InternalPlanInputs.AsNoTracking().Where(x => x.PlanId == original.Id).ToArrayAsync())
                arrange.InternalPlanInputs.Add(new() { PlanId = fakeId, Subject = pin.Subject, ProviderId = pin.ProviderId,
                    ConfigurationRevision = pin.ConfigurationRevision, PlacementId = pin.PlacementId });
            await arrange.SaveChangesAsync();
        }
        var fake = DeploymentPlanId.Parse(fakeId.ToString());
        Assert.Equal(DurablePlanCurrentStatus.PlanStale, await planner.CheckOperatorPlanAsync(fake, handlers, default));
        var committer = (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>();
        Assert.IsType<DurableCommitResult.PlanStale>(await committer.CommitOperatorAsync(change.Id, fake, "operator", default, handlers));
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            Assert.Equal(scenario.Commit.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
            Assert.Equal(2, await verify.DeploymentGenerations.CountAsync());
        }
        var accepted = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitOperatorAsync(change.Id, plan.Id, "operator", default, handlers)).Commit;
        await using var history = await contexts.CreateDbContextAsync();
        var source = await InternalGenerationAuthority.RequireStageReadAsync(history, accepted, default);
        var forged = (DeploymentGenerationRow)history.Entry(source).CurrentValues.ToObject();
        forged.GraphJson = forgedGraph; forged.Fingerprint = forgedFingerprint;
        var rejected = await Assert.ThrowsAsync<InvalidDataException>(() => InternalGenerationAuthority.RequireStageReadAsync(history, forged, default));
        Assert.Equal("Internal source identity mismatch.", rejected.Message);
        Assert.False(await history.GenerationStageCheckpoints.AnyAsync(x => x.Generation == accepted.Generation.Value && x.Stage == InitialRetirementCompletion.Stage));
    }
}
