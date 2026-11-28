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
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Corrective_capture_is_read_only_and_stale_runtime_authority_blocks_acceptance(bool initial, bool mixed)
    {
        JsonNode? envelope = null;
        var scenario = await InternalScenarioAsync(initialOnly: initial, twoComponents: mixed, configureManifest: e =>
        {
            ConfiguredDocuments(e, secondDocument: true);
            if (mixed)
            {
                var components = e["manifest"]!["spec"]!["components"]!;
                components["worker"]!["documents"] = components["api"]!["documents"]!.DeepClone();
                components["worker"]!["workloads"]!["main"]!["properties"]!["configuration"] = components["api"]!["workloads"]!["main"]!["properties"]!["configuration"]!.DeepClone();
            }
            envelope = e.DeepClone();
        });
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        var actions = await PrepareRollout(services, scenario, deployment, identity);
        await SubmitRollout(actions, scenario, deployment);
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var beforeProof = ""; var beforeOperations = 0;
        await using (var read = await contexts.CreateDbContextAsync())
        {
            beforeProof = (await read.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.Generation == scenario.Commit.Generation.Value && x.Stage == "ResourcesReady")).EvidenceJson;
            beforeOperations = await read.DurableOperations.CountAsync();
        }
        envelope!["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:corrected";
        var revision = await ImportUpdate(services, envelope);
        var ordinary = await UpdateChange(services, scenario, revision);
        Assert.Equal(DurablePlanStatus.PlanInputUnavailable, (await planner.CreateOperatorPlanAsync(ordinary.Id, "operator", default, handlers)).Status);
        var intent = new CorrectiveChangeSetIntent(scenario.Commit.DeploymentId, scenario.Commit.Generation);
        var request = Guid.CreateVersion7();
        var created = await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, revision.ApplicationRecordId, revision, intent, request, "first-actor", default);
        Assert.Equal(DurableChangeSetCreateStatus.Created, created.Status);
        var change = created.ChangeSet!;
        var retry = await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, revision.ApplicationRecordId, revision, intent, request, "retry-actor", default);
        Assert.Equal(change, retry.ChangeSet); Assert.Equal("first-actor", retry.ChangeSet!.ActorId);
        Assert.Equal(DurableChangeSetCreateStatus.InvalidInput, (await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, revision.ApplicationRecordId, scenario.Revision, intent, request, "different", default)).Status);
        var planned = await planner.CreateOperatorPlanAsync(change.Id, "operator", default, handlers);
        Assert.Equal(DurablePlanStatus.Planned, planned.Status);
        var plan = planned.Plan!; var captured = plan.Graph.Correction!.Document;
        Assert.Equal(scenario.Commit.Generation.Value, captured.ExpectedDesired);
        Assert.Equal(initial ? null : (long?)1, captured.LastApplied);
        Assert.Equal(beforeProof, captured.ResourceProof);
        Assert.Equal(scenario.Commit.PlanId.ToString(), captured.OriginalPlan.ToString());
        Assert.Equal("ConfirmedOwned", captured.Members.Single(x => x.Component == "api").State);
        Assert.NotNull(captured.Members.Single(x => x.Component == "api").Attempt);
        Assert.Equal("deployment-uid", captured.Members.Single(x => x.Component == "api").Confirmation!.Identity);
        Assert.Equal("registry.example/app:v1", captured.Members.Single(x => x.Component == "api").Confirmation!.Image);
        if (mixed)
        {
            var pending = captured.Members.Single(x => x.Component == "worker");
            Assert.Equal("NeverConfirmedInitialCreate", pending.State); Assert.Null(pending.Attempt); Assert.Null(pending.ReceiptHash);
        }
        Assert.Equal(mixed ? 4 : 2, captured.Publications.Length);
        Assert.Equal(initial ? 0 : 1, plan.Graph.ReusedResources.Count);
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckOperatorPlanAsync(plan.Id, handlers, default));
        await using var restarted = InternalServices(scenario.Connection, scenario.Time);
        var saved = (await restarted.GetRequiredService<IDurableDeploymentPlanner>().FindPlanAsync(plan.Id, default))!;
        Assert.Equal(plan.Fingerprint, saved.Fingerprint); Assert.Equal(plan.Graph.Correction.Fingerprint, saved.Graph.Correction!.Fingerprint);
        Assert.Equal(intent, (await planner.FindChangeSetAsync(change.Id, default))!.Correction);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            var owner = await verify.ApplicationDeployments.AsNoTracking().SingleAsync();
            Assert.Equal(scenario.Commit.Generation.Value, owner.DesiredGeneration); Assert.Equal(captured.LastApplied, owner.AppliedGeneration);
            Assert.Equal(beforeOperations, await verify.DurableOperations.CountAsync());
            Assert.False(await verify.ChangeSetCommits.AnyAsync(x => x.ChangeSetId.ToString() == change.Id.ToString()));
            Assert.Equal(beforeProof, (await verify.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.Generation == scenario.Commit.Generation.Value && x.Stage == "ResourcesReady")).EvidenceJson);
        }
        // A new publication state makes the immutable Plan stale; it is never rewritten.
        await using (var mutate = await contexts.CreateDbContextAsync())
        {
            var permission = await mutate.RuntimeConfigurationPermissions.FirstAsync();
            permission.Status = "Revoked"; permission.ConcurrencyToken = Guid.CreateVersion7();
            await mutate.SaveChangesAsync();
        }
        Assert.Equal(DurablePlanCurrentStatus.PlanStale, await planner.CheckOperatorPlanAsync(plan.Id, handlers, default));
        var committer = (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>();
        Assert.IsType<DurableCommitResult.PlanStale>(await committer.CommitOperatorAsync(change.Id, plan.Id, "operator", default, handlers));
        Assert.IsType<DurableCommitResult.PlanStale>(await committer.CommitInternalAsync(change.Id, plan.Id, "internal", default, handlers));
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            var owner = await verify.ApplicationDeployments.AsNoTracking().SingleAsync();
            Assert.Equal(scenario.Commit.Generation.Value, owner.DesiredGeneration); Assert.Equal(captured.LastApplied, owner.AppliedGeneration);
            Assert.Equal(beforeOperations, await verify.DurableOperations.CountAsync());
            Assert.False(await verify.ChangeSetCommits.AnyAsync(x => x.ChangeSetId.ToString() == change.Id.ToString()));
        }
        Assert.Equal(plan.Fingerprint, (await planner.FindPlanAsync(plan.Id, default))!.Fingerprint);
    }
    [Theory]
    [InlineData("no-op")]
    [InlineData("settings")]
    [InlineData("missing-proof")]
    [InlineData("uncertain")]
    [InlineData("preparing")]
    [InlineData("retirement")]
    public async Task Corrective_plan_refuses_nonimage_stale_or_unproven_incomplete_authority(string fault)
    {
        JsonNode? envelope = null;
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => envelope = e.DeepClone());
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var deployment = new ConfiguredSubmissionFixture { LoseResponse = fault == "uncertain" };
        var identity = new IdentityFixture();
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured: false);
        var key = await actions.AcceptAsync(scenario.Commit, RuntimeSubject, deployment.Contract.Action, "pending-corrective", default);
        if (fault == "uncertain") Assert.Equal(InternalActionOutcome.Uncertain, (await actions.ExecuteAsync(key, default)).Outcome);
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        if (fault == "preparing")
        {
            // The public Resource journal deliberately cannot start an internal Workload.
            // Arrange the same shared Preparing transition used by the internal executor.
            await using var prepare = await contexts.CreateDbContextAsync();
            await using var transaction = await prepare.Database.BeginTransactionAsync();
            Assert.NotNull(await DurableOperationTransitions.BeginAsync(prepare, key, scenario.Time.GetUtcNow(), default, "fixture"));
            await transaction.CommitAsync();
        }
        if (fault is "missing-proof" or "retirement")
        {
            await using var mutate = await contexts.CreateDbContextAsync();
            var checkpoint = await mutate.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.Stage == "ResourcesReady");
            if (fault == "missing-proof")
                await mutate.Database.ExecuteSqlInterpolatedAsync($"UPDATE generation_stage_checkpoints SET evidence_fingerprint = {new string('0',64)} WHERE id = {checkpoint.Id}");
            else
            {
                mutate.GenerationStageCheckpoints.Add(new() { Id = Guid.CreateVersion7(), DeploymentId = checkpoint.DeploymentId, Generation = checkpoint.Generation,
                    Stage = "RetirementStarted", FormatVersion = 1, InputFingerprint = checkpoint.InputFingerprint, EvidenceJson = "{}",
                    EvidenceFingerprint = ResourcesReadyCheckpointCodec.Hash("{}"), ConfirmedAt = scenario.Time.GetUtcNow() });
                await mutate.SaveChangesAsync();
            }
        }
        if (fault != "no-op") envelope!["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:corrected";
        if (fault == "settings") envelope!["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["containerName"] = "different";
        var revision = fault == "no-op" ? scenario.Revision : await ImportUpdate(services, envelope!);
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var intent = new CorrectiveChangeSetIntent(scenario.Commit.DeploymentId, scenario.Commit.Generation);
        Assert.Equal(DurableChangeSetCreateStatus.ExpectedGenerationUnsupported, (await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, revision.ApplicationRecordId,
            revision, intent with { ExpectedIncompleteDesiredGeneration = DeploymentGeneration.Create(scenario.Commit.Generation.Value + 1) }, Guid.CreateVersion7(), "stale", default)).Status);
        Assert.Equal(DurableChangeSetCreateStatus.ExpectedGenerationUnsupported, (await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, revision.ApplicationRecordId,
            revision, intent with { Deployment = ApplicationDeploymentId.New() }, Guid.CreateVersion7(), "foreign", default)).Status);
        var change = (await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, revision.ApplicationRecordId, revision, intent, Guid.CreateVersion7(), "operator", default)).ChangeSet!;
        Assert.Equal(DurablePlanStatus.PlanInputUnavailable, (await planner.CreateOperatorPlanAsync(change.Id, "operator", default, services.GetServices<IInternalActionHandler>().ToArray())).Status);
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Empty(await verify.DurablePlans.AsNoTracking().Where(x => x.ChangeSetId.ToString() == change.Id.ToString()).ToArrayAsync());
        var owner = await verify.ApplicationDeployments.AsNoTracking().SingleAsync();
        Assert.Null(owner.AppliedGeneration); Assert.Equal(1, owner.DesiredGeneration);
    }

}
