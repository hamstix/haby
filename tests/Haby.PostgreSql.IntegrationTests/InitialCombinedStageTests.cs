using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Fact]
    public Task Initial_resources_confirm_without_applied_and_survive_failed_workload_restart_without_replay() => InitialCombinedStageAsync(true);

    [Fact]
    public Task Document_free_initial_resources_and_workloads_keep_stage_provenance_and_complete_retirement() => InitialCombinedStageAsync(false);

    private async Task InitialCombinedStageAsync(bool configured)
    {
        JsonNode? envelope = null;
        var scenario = await InternalScenarioAsync(combinedInitial: true, configureManifest: e => { if (configured) ConfiguredDocuments(e); envelope = e.DeepClone(); });
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var deployment = new ConfiguredSubmissionFixture { Pending = true }; var identity = new IdentityFixture();
        if (configured) await Assert.ThrowsAsync<InvalidDataException>(async () => await PrepareRollout(services, scenario, deployment, identity));
        else await Assert.ThrowsAsync<InvalidDataException>(async () => await ConfiguredActions(services, deployment, identity)
            .AcceptAsync(scenario.Commit, RuntimeSubject, deployment.Contract.Action, "before-resources", default));
        var resources = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
        Assert.Null(resources.StopOutcome); Assert.NotNull(resources.Progress!.ResourcesReady);
        Assert.Null(resources.Progress.AppliedGeneration); Assert.Equal(resources.Progress.Total, resources.Progress.Completed);
        var original = ResourcesReadyCheckpointCodec.Encode(resources.Progress.ResourcesReady!);
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured);
        await SubmitRollout(actions, scenario, deployment);
        var pending = await actions.ObserveRolloutAsync(scenario.Commit, "initial-unready", OneRound, default);
        Assert.False(pending.Ready); Assert.False(pending.Applied); Assert.Equal(RolloutOutcome.Pending, Assert.Single(pending.Members).Observation.Outcome);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.True(await verify.ActionIntents.AnyAsync());
        }
        await using var restarted = InternalServices(scenario.Connection, scenario.Time);
        var resumed = await restarted.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
        Assert.Null(resumed.StopOutcome); Assert.Null(resumed.Progress!.AppliedGeneration);
        Assert.Equal(original, ResourcesReadyCheckpointCodec.Encode(resumed.Progress.ResourcesReady!));
        var resourceIds = resources.Progress.Nodes.Select(x => x.Operation.Key.OperationId.Value).ToArray();
        await using (var final = await restarted.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.All(await final.DurableOperations.AsNoTracking().Where(x => resourceIds.Contains(x.OperationId)).ToArrayAsync(), x => Assert.Equal(1, x.AttemptNumber));
            Assert.Equal(resourceIds.Length, await final.OperationOutputs.CountAsync(x => resourceIds.Contains(x.OperationId)));
        }
        envelope!["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:corrected";
        var revision = await ImportUpdate(restarted, envelope);
        var planner = restarted.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var change = await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, revision.ApplicationRecordId, revision,
            new(scenario.Commit.DeploymentId, scenario.Commit.Generation), Guid.CreateVersion7(), "operator", default);
        Assert.Equal(DurableChangeSetCreateStatus.Created, change.Status);
        var plan = await planner.CreateOperatorPlanAsync(change.ChangeSet!.Id, "operator", default, restarted.GetServices<IInternalActionHandler>().ToArray());
        Assert.Equal(DurablePlanStatus.Planned, plan.Status);
        Assert.Null(plan.Plan!.Graph.Correction!.Document.LastApplied);
        Assert.Equal(original, plan.Plan.Graph.Correction.Document.ResourceProof);
        var reused = Assert.Single(plan.Plan.Graph.ReusedResources);
        Assert.Equal(scenario.Commit.Generation.Value, reused.SourceGeneration);
        Assert.Equal(resources.Progress.ResourcesReady!.Resources.Single().ManagementId, reused.ManagementId);
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckOperatorPlanAsync(plan.Plan.Id, restarted.GetServices<IInternalActionHandler>().ToArray(), default));
        deployment.Pending = false;
        var ready = await BatchFacade(restarted, scenario, ConfiguredActions(restarted, deployment, identity), deployment, identity)
            .WaitAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, "complete-combined", "operator", new(MaximumRounds: 1), null, default);
        Assert.True(ready.CurrentReady); Assert.True(ready.CurrentApplied, ready.CompletionReason);
        await using (var completed = await restarted.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(scenario.Commit.Generation.Value, (await completed.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Single(await completed.GenerationStageCheckpoints.Where(x => x.Stage == "RetirementCompleted").ToArrayAsync());
            var source = await completed.DeploymentGenerations.AsNoTracking().SingleAsync();
            _ = await InitialRetirementCompletion.RequireProofAsync(completed, source, default);
        }
        Assert.NotNull(await restarted.GetRequiredService<IExternalOperationOutputs>().FindAcceptedAsync(resources.Progress.Nodes[0].Operation.Key, default));
    }
    [Fact]
    public async Task Initial_resource_dispatch_uncertainty_blocks_workloads_and_corrective_planning()
    {
        JsonNode? envelope = null;
        var scenario = await InternalScenarioAsync(combinedInitial: true, configureManifest: e => { ConfiguredDocuments(e); envelope = e.DeepClone(); });
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var journal = services.GetRequiredService<IDurableOperationJournal>();
        var before = (await journal.ProgressAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default))!;
        var node = before.Nodes.First(x => x.BlockingDependencies.Count == 0).Operation;
        var started = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(node.Key, scenario.Time.GetUtcNow(), default));
        Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(node.Key, started.AttemptId, scenario.Time.GetUtcNow(), default));
        var interrupted = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
        Assert.Equal(DurableExecutionOutcome.NeedsAttention, interrupted.StopOutcome);
        Assert.Null(interrupted.Progress!.ResourcesReady); Assert.Null(interrupted.Progress.AppliedGeneration);
        var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await PrepareRollout(services, scenario, deployment, identity));
        envelope!["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:corrected";
        var revision = await ImportUpdate(services, envelope);
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var change = await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, revision.ApplicationRecordId, revision,
            new(scenario.Commit.DeploymentId, scenario.Commit.Generation), Guid.CreateVersion7(), "operator", default);
        Assert.Equal(DurablePlanStatus.PlanInputUnavailable, (await planner.CreateOperatorPlanAsync(change.ChangeSet!.Id, "operator", default,
            services.GetServices<IInternalActionHandler>().ToArray())).Status);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verify.ActionIntents.ToArrayAsync()); Assert.Empty(await verify.OperationOutputs.ToArrayAsync());
        Assert.Empty(await verify.RuntimeConfigurationPermissions.ToArrayAsync()); Assert.Empty(await verify.GenerationStageCheckpoints.ToArrayAsync());
        Assert.Equal(0, deployment.Writes); Assert.Equal(0, identity.Writes);
    }
}
