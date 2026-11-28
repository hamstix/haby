using Haby.Application;
using Haby.Kubernetes;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Fact]
    public async Task Correction_of_incomplete_ordinary_UPDATE_keeps_prior_Applied_and_original_Resource_producer()
    {
        var (scenario, envelope) = await AppliedUpdateBaseline(initial: false, configured: true);
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var committer = (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>();
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:ordinary-bad";
        var ordinaryRevision = await ImportUpdate(services, envelope, "ordinary-bad-before-correction");
        var ordinaryChange = await UpdateChange(services, scenario, ordinaryRevision);
        var ordinaryPlan = await planner.CreateOperatorPlanAsync(ordinaryChange.Id, "operator", default, handlers);
        Assert.Equal(DurablePlanStatus.Planned, ordinaryPlan.Status);
        var ordinary = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitOperatorAsync(ordinaryChange.Id, ordinaryPlan.Plan!.Id,
            "operator", default, handlers)).Commit;
        var configuration = ConfigurationPreparation(services).Preparation;
        var create = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture { Exists = true };
        var update = new UpdateSubmissionFixture { PendingReadiness = true };
        var actions = new InternalDurableActions(contexts, [create, identity, update], services.GetServices<IProviderConfigurationCodec>(),
            services.GetServices<IExternalOperationOutputContract>(), services.GetRequiredService<DurableActionAdmission>(),
            services.GetRequiredService<DurableOperationSerialization>(), scenario.Time, configurationPreparation: configuration);
        var publication = new InternalRuntimePublication(contexts, configuration, actions, scenario.Time);
        var submitter = new PostgreSqlWorkloadSubmission(contexts, configuration, actions, new(contexts, configuration, actions, identity.Contract.Action),
            new(publication), services.GetRequiredService<DurableOperationSerialization>(), new(), InternalDeploymentAction.Create);
        var submitted = await submitter.SubmitAsync(ordinary.DeploymentId, ordinary.Generation, "operator", default);
        Assert.True(submitted.Outcome == "submitted", submitted.Outcome + "/" + submitted.Stage + "/" + submitted.Reason);
        var pending = await BatchFacade(services, scenario, actions, create, identity).WaitAsync(ordinary.DeploymentId, ordinary.Generation,
            "ordinary-bad-pending", "operator", new(MaximumRounds: 1), null, default);
        Assert.False(pending.CurrentReady); Assert.False(pending.CurrentApplied);
        string originalProof;
        await using (var read = await contexts.CreateDbContextAsync())
        {
            Assert.Equal(scenario.Commit.Generation.Value, (await read.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            originalProof = (await read.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.Generation == ordinary.Generation.Value &&
                x.Stage == ResourcesReadyCheckpoint.Stage)).EvidenceJson;
            Assert.False(await read.GenerationStageCheckpoints.AnyAsync(x => x.Generation == ordinary.Generation.Value && x.Stage == UpdateRetirementCompletion.Started));
        }
        envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:ordinary-corrected";
        var fixedRevision = await ImportUpdate(services, envelope, "correction-of-ordinary");
        var change = (await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, fixedRevision.ApplicationRecordId, fixedRevision,
            new(ordinary.DeploymentId, ordinary.Generation), Guid.CreateVersion7(), "operator", default)).ChangeSet!;
        var plan = await planner.CreateOperatorPlanAsync(change.Id, "operator", default, handlers);
        Assert.Equal(DurablePlanStatus.Planned, plan.Status);
        Assert.Equal(scenario.Commit.Generation.Value, plan.Plan!.Graph.Correction!.Document.LastApplied);
        Assert.Equal(originalProof, plan.Plan.Graph.Correction.Document.ResourceProof);
        Assert.Equal("ConfirmedOwned", Assert.Single(plan.Plan.Graph.Correction.Document.Members).State);
        var corrected = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitOperatorAsync(change.Id, plan.Plan.Id,
            "operator", default, handlers)).Commit;
        update.PendingReadiness = false;
        var correctedSubmission = await submitter.SubmitAsync(corrected.DeploymentId, corrected.Generation, "operator", default);
        Assert.True(correctedSubmission.Outcome == "submitted", correctedSubmission.Outcome + "/" + correctedSubmission.Stage + "/" + correctedSubmission.Reason);
        var ready = await BatchFacade(services, scenario, actions, create, identity).WaitAsync(corrected.DeploymentId, corrected.Generation,
            "corrected-ordinary-ready", "operator", new(MaximumRounds: 1), null, default);
        Assert.True(ready.CurrentReady); Assert.True(ready.CurrentApplied, ready.CompletionReason);
        Assert.Equal(2, update.Writes); Assert.Equal(0, create.Writes);
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Equal(corrected.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        var resource = Assert.Single(await verify.ResourceManagement.AsNoTracking().ToArrayAsync());
        Assert.True(resource.SourceGeneration < scenario.Commit.Generation.Value);
        var originalResource = await verify.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == resource.SourceGeneration);
        var resourceIds = DurableOperationGraphCodec.Decode(originalResource.GraphJson).Nodes.Select(x => x.Id.Value).ToArray();
        Assert.All(await verify.DurableOperations.AsNoTracking().Where(x => x.Generation == resource.SourceGeneration && resourceIds.Contains(x.OperationId)).ToArrayAsync(),
            x => Assert.Equal(1, x.AttemptNumber));
        var permissions = await verify.RuntimeConfigurationPermissions.AsNoTracking().ToArrayAsync();
        Assert.All(permissions.Where(x => RuntimePublicationCodec.Origin(x.OriginPayload).Generation < corrected.Generation.Value), x => Assert.Equal("Revoked", x.Status));
        Assert.All(permissions.Where(x => RuntimePublicationCodec.Origin(x.OriginPayload).Generation == corrected.Generation.Value), x => Assert.Equal("Published", x.Status));
        var historical = await verify.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == ordinary.Generation.Value);
        _ = await InternalGenerationAuthority.RequireStageReadAsync(verify, historical, default);
        Assert.False(await verify.GenerationStageCheckpoints.AnyAsync(x => x.Generation == ordinary.Generation.Value && x.Stage == InitialRetirementCompletion.Stage));
    }
}
