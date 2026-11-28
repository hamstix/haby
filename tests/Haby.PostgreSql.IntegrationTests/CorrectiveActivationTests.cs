using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Haby.Kubernetes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Fact]
    public async Task ApplicationVersion_only_corrective_input_is_significant_with_executable_initial_CREATE_continuation()
    {
        JsonNode? envelope = null;
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => envelope = e.DeepClone());
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var deployment = new ConfiguredSubmissionFixture();
        var actions = await PrepareRollout(services, scenario, deployment, new IdentityFixture(), configured: false);
        var pending = await actions.AcceptAsync(scenario.Commit, RuntimeSubject, deployment.Contract.Action, "pending-version-change", default);
        var revision = await ImportUpdate(services, envelope!, "version-only-correction");
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var change = (await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, revision.ApplicationRecordId, revision,
            new(scenario.Commit.DeploymentId, scenario.Commit.Generation), Guid.CreateVersion7(), "operator", default)).ChangeSet!;
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var result = await planner.CreateOperatorPlanAsync(change.Id, "operator", default, handlers);
        Assert.Equal(DurablePlanStatus.Planned, result.Status);
        var member = Assert.Single(result.Plan!.Graph.Correction!.Document.Members);
        Assert.Equal("NeverConfirmedInitialCreate", member.State); Assert.Equal(member.Image.PreviousImage, member.Image.TargetImage);
        var accepted = Assert.IsType<DurableCommitResult.Committed>(await ((PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>())
            .CommitOperatorAsync(change.Id, result.Plan.Id, "operator", default, handlers)).Commit;
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var configuration = ConfigurationPreparation(services).Preparation; var identity = new IdentityFixture();
        var continuation = ConfiguredActions(services, deployment, identity);
        var publication = new InternalRuntimePublication(contexts, configuration, continuation, scenario.Time);
        var submitter = new PostgreSqlWorkloadSubmission(contexts, configuration, continuation, new(contexts, configuration, continuation, identity.Contract.Action),
            new(publication), services.GetRequiredService<DurableOperationSerialization>(), new(), InternalDeploymentAction.Create);
        var submitted = await submitter.SubmitAsync(accepted.DeploymentId, accepted.Generation, "operator", default);
        Assert.True(submitted.Outcome == "submitted", submitted.Outcome + "/" + submitted.Stage + "/" + submitted.Reason);
        var ready = await BatchFacade(services, scenario, continuation, deployment, identity).WaitAsync(accepted.DeploymentId, accepted.Generation,
            "version-only-corrective-ready", "operator", new(MaximumRounds: 1), null, default);
        Assert.True(ready.CurrentReady); Assert.True(ready.CurrentApplied, ready.CompletionReason); Assert.Equal(1, deployment.Writes);
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Equal(accepted.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        var original = await verify.DurableOperations.AsNoTracking().SingleAsync(x => x.Generation == scenario.Commit.Generation.Value && x.OperationId == pending.OperationId.Value);
        Assert.Equal("Pending", original.State); Assert.Equal(0, original.AttemptNumber); Assert.Null(original.DispatchedAt);
        Assert.Equal(2, await verify.DeploymentGenerations.CountAsync());
    }
    [Fact]
    public Task Corrective_commit_accepts_exact_failed_initial_rollout_without_fabricating_applied() =>
        CompleteFirstCorrectionAsync(configured: false);

    [Fact]
    public Task Configured_corrective_continuation_retires_original_grants_only_after_fresh_readiness() =>
        CompleteFirstCorrectionAsync(configured: true);

    [Fact]
    public Task Corrective_mixed_members_UPDATE_confirmed_UID_and_CREATE_only_positively_never_dispatched_initial_target() =>
        CompleteFirstCorrectionAsync(configured: true, mixed: true);

    [Fact]
    public Task Successive_uncompleted_corrections_preserve_original_resources_and_accumulate_retirement_obligations() =>
        CompleteFirstCorrectionAsync(configured: true, successive: true);

    [Fact]
    public Task Corrective_commit_lost_response_reconciles_read_only_and_restart_continues_without_Resource_replay() =>
        CompleteFirstCorrectionAsync(configured: true, lostCommit: true);

    [Fact]
    public Task Corrective_winner_during_old_retirement_read_prevents_old_revocations_and_Applied() =>
        CompleteFirstCorrectionAsync(configured: true, successive: true, pauseRetirement: true);

    [Fact]
    public Task Actual_retirement_start_refuses_new_handoff_and_fresh_wait_finishes_exact_progress() =>
        CompleteFirstCorrectionAsync(configured: true, startedRetirement: true);

    private async Task CompleteFirstCorrectionAsync(bool configured, bool mixed = false, bool successive = false, bool lostCommit = false,
        bool pauseRetirement = false, bool startedRetirement = false)
    {
        JsonNode? envelope = null;
        var scenario = await InternalScenarioAsync(combinedInitial: true, configureManifest: e =>
        { if (configured) ConfiguredDocuments(e, shared: mixed); envelope = e.DeepClone(); });
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var resources = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
        Assert.Null(resources.StopOutcome); Assert.NotNull(resources.Progress!.ResourcesReady); Assert.Null(resources.Progress.AppliedGeneration);
        var deployment = new ConfiguredSubmissionFixture { Pending = true, ProgressDeadlineExceeded = true, DistinctOwnedUids = mixed };
        var initialIdentity = new IdentityFixture();
        var actions = await PrepareRollout(services, scenario, deployment, initialIdentity, configured);
        await SubmitRollout(actions, scenario, deployment);
        DurableOperationKey? pendingInitial = null;
        if (mixed) pendingInitial = await actions.AcceptAsync(scenario.Commit,
            new ProviderInput.Workload(ManifestLocalId.Create("api"), ManifestLocalId.Create("second")), deployment.Contract.Action, "never-dispatched-second", default);
        var failed = await actions.ObserveRolloutAsync(scenario.Commit, "failed-initial-rollout", OneRound, default);
        Assert.False(failed.Ready); Assert.False(failed.Applied);
        var failedMain = Assert.Single(failed.Members, x => x.Subject == "Workload/api/main");
        Assert.Equal(RolloutOutcome.Pending, failedMain.Observation.Outcome);
        Assert.Equal("progress-deadline", failedMain.Observation.Code);
        envelope!["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:corrected";
        var revision = await ImportUpdate(services, envelope);
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var change = (await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, revision.ApplicationRecordId, revision,
            new(scenario.Commit.DeploymentId, scenario.Commit.Generation), Guid.CreateVersion7(), "operator", default)).ChangeSet!;
        var planned = await planner.CreateOperatorPlanAsync(change.Id, "operator", default, handlers);
        Assert.Equal(DurablePlanStatus.Planned, planned.Status);
        Assert.Null(planned.Plan!.Graph.Correction!.Document.LastApplied);
        var inventory = planned.Plan.Graph.Correction.Document;
        Assert.Equal("ConfirmedOwned", Assert.Single(inventory.Members, x => x.Workload == "main").State);
        if (mixed) Assert.Equal("NeverConfirmedInitialCreate", Assert.Single(inventory.Members, x => x.Workload == "second").State);
        Assert.Equal(ResourcesReadyCheckpointCodec.Encode(resources.Progress.ResourcesReady!), planned.Plan.Graph.Correction.Document.ResourceProof);
        var committer = (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>();
        DurableChangeSetCommit accepted;
        if (lostCommit)
        {
            var loss = new CorrectiveCommitResponseLoss();
            await using var committing = Services(scenario.Connection, scenario.Time, internalActions: true, commandInterceptor: loss);
            var faulted = (PostgreSqlDurableDesiredStateCommitter)committing.GetRequiredService<IDurableDesiredStateCommitter>();
            await Assert.ThrowsAsync<IOException>(async () => await faulted.CommitOperatorAsync(change.Id, planned.Plan.Id, "operator", default, handlers));
            Assert.True(loss.Thrown);
            await using var reconciliation = InternalServices(scenario.Connection, scenario.Time);
            accepted = (await reconciliation.GetRequiredService<IDurableDesiredStateCommitter>().FindCommitAsync(change.Id, default))!;
            Assert.NotNull(accepted); // Read-only reconciliation; never repeat the uncertain accepting write.
        }
        else accepted = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitOperatorAsync(change.Id, planned.Plan.Id, "operator", default, handlers)).Commit;
        Assert.Equal(scenario.Commit.Generation.Value + 1, accepted.Generation.Value);
        if (mixed) { deployment.Pending = false; deployment.ProgressDeadlineExceeded = false; }
        await using var restarted = lostCommit ? InternalServices(scenario.Connection, scenario.Time) : null;
        var runtimeServices = restarted ?? services;
        var contexts = runtimeServices.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var configuration = ConfigurationPreparation(runtimeServices).Preparation;
        var update = new UpdateSubmissionFixture(); var identity = initialIdentity;
        var diagnostics = new List<UpdateCompletionDiagnostic>();
        var interruptAfterStart = startedRetirement;
        var continuation = new InternalDurableActions(contexts, [deployment, identity, update], runtimeServices.GetServices<IProviderConfigurationCodec>(),
            runtimeServices.GetServices<IExternalOperationOutputContract>(), runtimeServices.GetRequiredService<DurableActionAdmission>(),
            runtimeServices.GetRequiredService<DurableOperationSerialization>(), scenario.Time, fault: point =>
            { if (interruptAfterStart && point == InternalActionFault.AfterRetirementStart) { interruptAfterStart = false; throw new IOException("Controlled retirement-start response loss."); } },
            configurationPreparation: configuration, updateDiagnostic: diagnostics.Add);
        var publication = new InternalRuntimePublication(contexts, configuration, continuation, scenario.Time);
        var submitter = new PostgreSqlWorkloadSubmission(contexts, configuration, continuation, new(contexts, configuration, continuation, identity.Contract.Action),
            new(publication), runtimeServices.GetRequiredService<DurableOperationSerialization>(), new(), InternalDeploymentAction.Create);
        var submitted = await submitter.SubmitAsync(accepted.DeploymentId, accepted.Generation, "operator", default);
        Assert.True(submitted.Outcome == "submitted", submitted.Outcome + "/" + submitted.Stage + "/" + submitted.Reason);
        if (successive)
        {
            var firstAccepted = accepted;
            await using (var read = await contexts.CreateDbContextAsync())
            {
                Assert.Null((await read.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
                Assert.False(await read.GenerationStageCheckpoints.AnyAsync(x => x.Generation == firstAccepted.Generation.Value && x.Stage == UpdateRetirementCompletion.Started));
            }
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<GenerationReadinessResult>? retiring = null;
            if (pauseRetirement)
            {
                update.BeforeRecipients = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
                retiring = BatchFacade(runtimeServices, scenario, continuation, deployment, identity).WaitAsync(firstAccepted.DeploymentId, firstAccepted.Generation,
                    "superseded-retirement", "operator", new(MaximumRounds: 1), null, default).AsTask();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            try
            {
                await using var independent = pauseRetirement ? InternalServices(scenario.Connection, scenario.Time) : null;
                var nextServices = independent ?? services;
                var nextPlanner = nextServices.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
                var nextCommitter = (PostgreSqlDurableDesiredStateCommitter)nextServices.GetRequiredService<IDurableDesiredStateCommitter>();
                var nextHandlers = nextServices.GetServices<IInternalActionHandler>().ToArray();
                envelope!["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:corrected-again";
                var nextRevision = await ImportUpdate(nextServices, envelope, "second-correction");
                var nextChange = (await nextPlanner.CreateCorrectiveChangeSetAsync(scenario.Environment, nextRevision.ApplicationRecordId, nextRevision,
                    new(accepted.DeploymentId, accepted.Generation), Guid.CreateVersion7(), "operator", default)).ChangeSet!;
                var nextPlan = await nextPlanner.CreateOperatorPlanAsync(nextChange.Id, "operator", default, nextHandlers);
                Assert.Equal(DurablePlanStatus.Planned, nextPlan.Status);
                Assert.Null(nextPlan.Plan!.Graph.Correction!.Document.LastApplied);
                Assert.Equal(firstAccepted.Generation.Value, Assert.Single(nextPlan.Plan.Graph.Correction.Document.Members).OriginGeneration);
                Assert.Equal(ResourcesReadyCheckpointCodec.Encode(resources.Progress.ResourcesReady!), nextPlan.Plan.Graph.Correction.Document.ResourceProof);
                accepted = Assert.IsType<DurableCommitResult.Committed>(await nextCommitter.CommitOperatorAsync(nextChange.Id, nextPlan.Plan.Id, "operator", default, nextHandlers)).Commit;
                Assert.Equal(firstAccepted.Generation.Value + 1, accepted.Generation.Value);
            }
            finally { release.TrySetResult(); }
            if (retiring is not null)
            {
                var stale = await retiring;
                Assert.False(stale.CurrentApplied); Assert.Equal("prerequisite-incomplete", stale.CompletionReason);
                update.BeforeRecipients = null;
                await using var verify = await contexts.CreateDbContextAsync();
                Assert.False(await verify.GenerationStageCheckpoints.AnyAsync(x => x.Generation == firstAccepted.Generation.Value &&
                    (x.Stage == UpdateRetirementCompletion.Started || x.Stage == InitialRetirementCompletion.Stage)));
                Assert.All(await verify.RuntimeConfigurationPermissions.AsNoTracking().ToArrayAsync(), x => Assert.Equal("Published", x.Status));
            }
            var nextSubmitted = await submitter.SubmitAsync(accepted.DeploymentId, accepted.Generation, "operator", default);
            Assert.True(nextSubmitted.Outcome == "submitted", nextSubmitted.Outcome + "/" + nextSubmitted.Stage + "/" + nextSubmitted.Reason);
            await using var verifyOld = await contexts.CreateDbContextAsync();
            _ = await InternalGenerationAuthority.RequireStageReadAsync(verifyOld, firstAccepted, default);
            Assert.False(await verifyOld.GenerationStageCheckpoints.AnyAsync(x => x.Generation == firstAccepted.Generation.Value && x.Stage == InitialRetirementCompletion.Stage));
        }
        if (configured)
        {
            update.PendingReadiness = true;
            var pending = await BatchFacade(runtimeServices, scenario, continuation, deployment, identity).WaitAsync(accepted.DeploymentId, accepted.Generation,
                "corrected-pending", "operator", new(MaximumRounds: 1), null, default);
            Assert.False(pending.CurrentReady); Assert.False(pending.CurrentApplied);
            await RequirePermissionStatesAsync("Published");
            update.PendingReadiness = false; update.OldRecipients = true;
            var recipients = await BatchFacade(runtimeServices, scenario, continuation, deployment, identity).WaitAsync(accepted.DeploymentId, accepted.Generation,
                "corrected-old-recipients", "operator", new(MaximumRounds: 1), null, default);
            Assert.True(recipients.CurrentReady); Assert.False(recipients.CurrentApplied);
            Assert.Equal("old-recipients-present", recipients.CompletionReason);
            await RequirePermissionStatesAsync("Published");
            update.OldRecipients = false;
        }
        if (startedRetirement)
        {
            var interrupted = await BatchFacade(runtimeServices, scenario, continuation, deployment, identity).WaitAsync(accepted.DeploymentId, accepted.Generation,
                "interrupted-after-start", "operator", new(MaximumRounds: 1), null, default);
            Assert.Equal(GenerationReadinessOutcome.Unavailable, interrupted.Outcome); Assert.False(interrupted.CurrentApplied);
            await using (var verify = await contexts.CreateDbContextAsync())
            {
                Assert.True(await verify.GenerationStageCheckpoints.AnyAsync(x => x.Generation == accepted.Generation.Value && x.Stage == UpdateRetirementCompletion.Started));
                Assert.All(await verify.RuntimeConfigurationPermissions.AsNoTracking().ToArrayAsync(), x => Assert.Equal("Published", x.Status));
                Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            }
            envelope!["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:forbidden-after-start";
            var refusedRevision = await ImportUpdate(services, envelope, "retirement-started-correction");
            var refusedChange = (await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, refusedRevision.ApplicationRecordId, refusedRevision,
                new(accepted.DeploymentId, accepted.Generation), Guid.CreateVersion7(), "operator", default)).ChangeSet!;
            Assert.Equal(DurablePlanStatus.PlanInputUnavailable, (await planner.CreateOperatorPlanAsync(refusedChange.Id, "operator", default, handlers)).Status);
        }
        var ready = await BatchFacade(runtimeServices, scenario, continuation, deployment, identity).WaitAsync(accepted.DeploymentId, accepted.Generation,
            "corrected-first-install", "operator", new(MaximumRounds: 1), null, default);
        Assert.True(ready.CurrentReady, ready.Outcome + "/" + ready.CompletionReason + "/" + string.Join(",", (ready.Members ?? []).Select(x => x.Outcome + ":" + x.Code))); Assert.True(ready.CurrentApplied, ready.CompletionReason + "/" + string.Join(",", diagnostics.Select(x => x.Stage + ":" + x.Predicate))); Assert.Equal(successive ? 2 : 1, update.Writes);
        Assert.Equal(mixed ? 2 : 1, deployment.Writes);
        if (configured) await RequirePermissionStatesAsync("Revoked");
        await using (var read = await contexts.CreateDbContextAsync())
        {
            Assert.Equal(accepted.Generation.Value, (await read.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.False(await read.GenerationStageCheckpoints.AnyAsync(x => x.Generation == scenario.Commit.Generation.Value && x.Stage == "RetirementCompleted"));
            Assert.Single(await read.ResourceManagement.AsNoTracking().ToArrayAsync());
            var nodeIds = resources.Progress.Nodes.Select(x => x.Operation.Key.OperationId.Value).ToArray();
            Assert.All(await read.DurableOperations.AsNoTracking().Where(x => nodeIds.Contains(x.OperationId)).ToArrayAsync(), x => Assert.Equal(1, x.AttemptNumber));
            _ = await InternalGenerationAuthority.RequireStageReadAsync(read, scenario.Commit, default);
            _ = await InternalGenerationAuthority.RequireStageReadAsync(read, accepted, default);
            if (pendingInitial is not null)
            {
                var old = await read.DurableOperations.AsNoTracking().SingleAsync(x => x.Generation == scenario.Commit.Generation.Value && x.OperationId == pendingInitial.OperationId.Value);
                Assert.Equal("Pending", old.State); Assert.Equal(0, old.AttemptNumber);
                Assert.False(await read.DurableOperationAttempts.AnyAsync(x => x.Generation == scenario.Commit.Generation.Value && x.OperationId == old.OperationId));
            }
        }
        if (successive && !pauseRetirement)
        {
            // A completed correction is an ordinary Applied baseline, not a new
            // special acceptance mode. Its immutable completion remains readable.
            var corrected = accepted;
            envelope!["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:ordinary-after-correction";
            var ordinaryRevision = await ImportUpdate(services, envelope, "ordinary-after-correction");
            var ordinaryChange = (await planner.CreateChangeSetAsync(scenario.Environment, ordinaryRevision.ApplicationRecordId, ordinaryRevision,
                new DeploymentPrecondition.ExpectedGeneration(corrected.Generation), "operator", default)).ChangeSet!;
            var ordinaryPlan = await planner.CreateOperatorPlanAsync(ordinaryChange.Id, "operator", default, handlers);
            Assert.Equal(DurablePlanStatus.Planned, ordinaryPlan.Status);
            Assert.Null(ordinaryPlan.Plan!.Graph.Correction); Assert.NotNull(ordinaryPlan.Plan.Graph.WorkloadUpdate);
            var ordinary = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitOperatorAsync(ordinaryChange.Id, ordinaryPlan.Plan.Id,
                "operator", default, handlers)).Commit;
            var ordinarySubmitted = await submitter.SubmitAsync(ordinary.DeploymentId, ordinary.Generation, "operator", default);
            Assert.True(ordinarySubmitted.Outcome == "submitted", ordinarySubmitted.Outcome + "/" + ordinarySubmitted.Stage + "/" + ordinarySubmitted.Reason);
            var completed = await BatchFacade(runtimeServices, scenario, continuation, deployment, identity).WaitAsync(ordinary.DeploymentId, ordinary.Generation,
                "ordinary-after-corrective-ready", "operator", new(MaximumRounds: 1), null, default);
            Assert.True(completed.CurrentReady); Assert.True(completed.CurrentApplied, completed.CompletionReason);
            Assert.Equal(3, update.Writes); Assert.Equal(1, deployment.Writes);
            await using var historical = await contexts.CreateDbContextAsync();
            var correctedSource = await historical.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == corrected.Generation.Value);
            _ = await UpdateRetirementCompletion.RequireProofAsync(historical, correctedSource, default);
            Assert.Equal(ordinary.Generation.Value, (await historical.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Single(await historical.ResourceManagement.AsNoTracking().ToArrayAsync());
        }
        async Task RequirePermissionStatesAsync(string originalStatus)
        {
            await using var read = await contexts.CreateDbContextAsync();
            var permissions = await read.RuntimeConfigurationPermissions.AsNoTracking().ToArrayAsync();
            Assert.Equal((mixed ? 2 : 1) * (successive ? 3 : 2), permissions.Length);
            var original = permissions.Where(x => RuntimePublicationCodec.Origin(x.OriginPayload).Generation < accepted.Generation.Value).ToArray();
            var current = permissions.Where(x => RuntimePublicationCodec.Origin(x.OriginPayload).Generation == accepted.Generation.Value).ToArray();
            Assert.Equal((mixed ? 2 : 1) * (successive ? 2 : 1), original.Length); Assert.Equal(mixed ? 2 : 1, current.Length);
            Assert.All(original, x => Assert.Equal(originalStatus, x.Status));
            Assert.All(current, x => Assert.Equal("Published", x.Status));
            Assert.Equal(mixed ? 2 : 1, await read.RuntimeIdentityBindings.CountAsync());
            if (originalStatus == "Published")
            {
                Assert.Null((await read.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
                Assert.False(await read.GenerationStageCheckpoints.AnyAsync(x => x.Generation == accepted.Generation.Value && x.Stage == UpdateRetirementCompletion.Started));
            }
        }
    }
    private sealed class CorrectiveCommitResponseLoss : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        internal bool Thrown { get; private set; }
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData data, CancellationToken token = default)
        {
            if (!Thrown) { Thrown = true; throw new IOException("Controlled response loss after accepting transaction commit."); }
            return Task.CompletedTask;
        }
    }

}
