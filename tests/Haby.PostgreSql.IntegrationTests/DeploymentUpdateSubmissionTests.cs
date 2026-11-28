using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    private sealed partial class UpdateSubmissionFixture : IInternalActionHandler, IInternalReadinessHandler, IInternalRecipientObserver,
        IInternalWorkloadUpdatePlanner, IInternalWorkloadCorrectionPlanner
    {
        private readonly InternalDeploymentAction adapter = new(null!, update: true);
        internal int Writes, Reads;
        internal bool Refuse;
        private bool submitted;
        public InternalActionContract Contract => adapter.Contract;
        public bool UpdatesObject => true;
        public bool ObservationRecovery => true;
        public bool RequiresRuntimePublication => true;
        public InternalManagedTarget ManagedTarget(InternalActionContext context) => adapter.ManagedTarget(context);
        public IReadOnlyList<ManifestLocalId> PreparationDocuments(InternalActionContext context) => adapter.PreparationDocuments(context);
        public string Prepare(InternalActionContext context) => adapter.Prepare(context);
        public bool Validate(InternalActionContext context, string payload) => !Refuse && adapter.Validate(context, payload);
        public bool ValidateReceipt(InternalActionContext context, string payload, string receipt, bool observation) => adapter.ValidateReceipt(context, payload, receipt, observation);
        public InternalObjectConfirmation? ObjectConfirmation(InternalActionContext context, string payload, string receipt) => adapter.ObjectConfirmation(context, payload, receipt);
        public DurableActionPayload CaptureUpdate(InternalActionContext previous, DurablePlanningInputs target, string version, string intent, string receipt) =>
            adapter.CaptureUpdate(previous, target, version, intent, receipt);
        public CorrectiveConfirmedObject CaptureConfirmedObject(InternalActionContext previous, string intent, string receipt) => adapter.CaptureConfirmedObject(previous, intent, receipt);
        public CorrectiveImageDelta CaptureCorrection(DurablePlanningInputs previous, string previousVersion, DurablePlanningInputs target, string version, ProviderInput.Workload subject) =>
            adapter.CaptureCorrection(previous, previousVersion, target, version, subject);
        private static InternalActionResult Confirm(string payload, bool observation)
        {
            var intent = DeploymentIntents.Decode(payload);
            return new(InternalActionOutcome.Confirmed, "fixture-confirmed", DeploymentIntents.Encode(new DeploymentReceipt(4, intent.Binding,
                intent.Namespace, intent.Name, intent.Owner, intent.ManagedWorkload, intent.TargetGeneration, intent.Operation, intent.ExpectedUid!, 2,
                intent.Target, observation ? ConfirmationSource.Observation : ConfirmationSource.Response)));
        }
        public ValueTask<InternalActionResult> ExecuteAsync(InternalActionContext context, string payload, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Writes++; submitted = true; return ValueTask.FromResult(Confirm(payload, false)); }
        public ValueTask<InternalActionResult> ObserveAsync(InternalActionContext context, string payload, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Reads++; return ValueTask.FromResult(submitted ? Confirm(payload, true) : new InternalActionResult(InternalActionOutcome.NotConfirmed, "prior-target-only")); }
    }

    [Theory]
    [InlineData(true, "none")]
    [InlineData(false, "none")]
    [InlineData(true, "before-marker")]
    [InlineData(true, "after-marker")]
    [InlineData(true, "after-external")]
    [InlineData(true, "before-completion")]
    [InlineData(true, "cancel-after-marker")]
    [InlineData(true, "mixed")]
    [InlineData(true, "version-only")]
    [InlineData(true, "revoked-binding")]
    [InlineData(true, "revoked-new-permission")]
    [InlineData(true, "handler-refusal")]
    [InlineData(true, "forged-old-intent")]
    public async Task Internal_update_confirms_same_UID_and_current_projection_preserving_original_history(bool configured, string fault)
    {
        var (scenario, envelope) = await AppliedUpdateBaseline(fault != "mixed", configured);
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        if (fault != "version-only") envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:v2";
        var reference = await ImportUpdate(services, envelope);
        var change = await UpdateChange(services, scenario, reference);
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var plan = (await services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>().CreateInternalPlanAsync(change.Id, "tester", default, handlers)).Plan!;
        var accepted = Assert.IsType<DurableCommitResult.Committed>(await ((PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>())
            .CommitInternalAsync(change.Id, plan.Id, "tester", default, handlers)).Commit;
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var configuration = ConfigurationPreparation(services).Preparation;
        var update = new UpdateSubmissionFixture(); var identity = new IdentityFixture { Exists = true }; var create = new ConfiguredSubmissionFixture();
        using var request = new CancellationTokenSource();
        var interrupted = fault is "before-marker" or "after-marker" or "after-external" or "before-completion" or "cancel-after-marker";
        var armed = interrupted;
        void Inject(InternalActionFault point)
        {
            if (!armed || !(fault == "before-marker" && point == InternalActionFault.BeforeMarkerCommit ||
                fault is "after-marker" or "cancel-after-marker" && point == InternalActionFault.AfterMarkerCommit ||
                fault == "after-external" && point == InternalActionFault.AfterExternalResult ||
                fault == "before-completion" && point == InternalActionFault.BeforeCompletionCommit)) return;
            armed = false;
            if (fault == "cancel-after-marker") { request.Cancel(); return; }
            throw new IOException("Simulated update boundary interruption.");
        }
        InternalDurableActions ActionsForUpdate(ServiceProvider provider) => new(provider.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
            [create, identity, update], provider.GetServices<IProviderConfigurationCodec>(), provider.GetServices<IExternalOperationOutputContract>(),
            provider.GetRequiredService<DurableActionAdmission>(), provider.GetRequiredService<DurableOperationSerialization>(), scenario.Time,
            fault: Inject, configurationPreparation: ConfigurationPreparation(provider).Preparation);
        var actions = ActionsForUpdate(services);
        Assert.Null((await configuration.PrepareAsync(accepted, "tester", default)).Failure);
        if (configured)
        {
            var reused = await new InternalRuntimeIdentityPreparation(contexts, configuration, actions, identity.Contract.Action).PrepareAsync(accepted, default);
            Assert.Equal(InternalActionOutcome.Confirmed, Assert.Single(reused).Result.Outcome);
            Assert.Equal(0, identity.Writes);
            var publication = await new InternalRuntimePublicationPreparation(new(contexts, configuration, actions, scenario.Time)).PrepareAsync(accepted, "tester", default);
            Assert.Null(publication.Reason); Assert.Equal(2, publication.Completed.Count);
        }
        if (fault is "revoked-binding" or "revoked-new-permission")
        {
            var publisher = new InternalRuntimePublication(contexts, configuration, actions, scenario.Time);
            var original = (await publisher.PreparationRequestsAsync(accepted, "tester", default))[0];
            var current = (await publisher.ReadAsync(original.Key, default))!;
            await publisher.ApplyAsync(original with { Key = "test-revoke-update", Origin = null,
                Operation = fault == "revoked-binding" ? RuntimePublicationOperation.RevokeBinding : RuntimePublicationOperation.RevokePermission,
                Revision = fault == "revoked-binding" ? null : original.Revision,
                ExpectedToken = fault == "revoked-binding" ? current.CurrentBinding.Token : current.CurrentPermission!.Token }, default);
        }
        if (fault == "handler-refusal") update.Refuse = true;
        if (fault is "revoked-binding" or "revoked-new-permission" or "handler-refusal")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => actions.AcceptAsync(accepted, RuntimeSubject, update.Contract.Action, "update-main", default).AsTask());
            await using var refused = await contexts.CreateDbContextAsync();
            Assert.Equal(0, update.Writes); Assert.Equal(0, identity.Writes);
            Assert.False(await refused.ActionIntents.AnyAsync(x => x.Generation == accepted.Generation.Value));
            Assert.False(await refused.ManagedObjectStates.AnyAsync());
            Assert.Equal(scenario.Commit.Generation.Value, (await refused.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            return;
        }
        var key = await actions.AcceptAsync(accepted, RuntimeSubject, update.Contract.Action, "update-main", default);
        if (fault == "forged-old-intent")
        {
            await using (var corrupt = await contexts.CreateDbContextAsync())
            {
                var oldIntent = await corrupt.ActionIntents.AsNoTracking().Where(x => x.Generation == scenario.Commit.Generation.Value && x.ActionContract == InternalDeploymentAction.Create).Select(x => x.Payload).SingleAsync();
                await corrupt.ActionIntents.Where(x => x.Generation == accepted.Generation.Value).ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, oldIntent));
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => actions.ExecuteAsync(key, default).AsTask());
            Assert.Equal(0, update.Writes);
            return;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => actions.AcceptAsync(accepted, RuntimeSubject, update.Contract.Action, "duplicate-update", default).AsTask());
        if (interrupted)
        {
            if (fault == "cancel-after-marker") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actions.ExecuteAsync(key, request.Token).AsTask());
            else await Assert.ThrowsAsync<IOException>(() => actions.ExecuteAsync(key, request.Token).AsTask());
        }
        await using var restarted = InternalServices(scenario.Connection, scenario.Time);
        var replay = ActionsForUpdate(restarted);
        if (fault == "before-marker")
            Assert.Equal(InternalActionOutcome.NotConfirmed, (await replay.RecoverByObservationAsync(key, default)).Outcome);
        var result = await replay.ExecuteAsync(key, default);
        if (fault is "after-marker" or "cancel-after-marker")
        {
            Assert.Equal(InternalActionOutcome.NotConfirmed, result.Outcome);
            Assert.Equal(InternalActionOutcome.NotConfirmed, (await replay.ExecuteAsync(key, default)).Outcome);
            Assert.Equal(0, update.Writes);
            await using var uncertain = await contexts.CreateDbContextAsync();
            Assert.Empty(await uncertain.ManagedObjectStates.ToArrayAsync());
            Assert.False(await uncertain.OperationOutputs.AnyAsync(x => x.Generation == accepted.Generation.Value));
            Assert.Equal(nameof(DurableOperationState.NeedsAttention), (await replay.HistoryAsync(key, default))!.Operation.State);
            Assert.Equal(scenario.Commit.Generation.Value, (await uncertain.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            return;
        }
        Assert.Equal(InternalActionOutcome.Confirmed, result.Outcome);
        var receipt = DeploymentIntents.DecodeReceipt(result.Receipt!);
        Assert.Equal("deployment-uid", receipt.Uid); Assert.Equal(fault == "version-only" ? "registry.example/app:v1" : "registry.example/app:v2", receipt.Projection.Image);
        Assert.Contains(fault == "version-only" ? "UnchangedImage" : "ImageChanged", plan.Graph.WorkloadUpdate!.Document.Members.Single().Delta.Json);
        Assert.Equal(result.Receipt, (await replay.ExecuteAsync(key, default)).Receipt);
        Assert.Equal(1, update.Writes);
        await using var verify = await contexts.CreateDbContextAsync();
        var state = await verify.ManagedObjectStates.AsNoTracking().SingleAsync();
        Assert.Equal(result.Receipt, state.Payload); Assert.Equal(accepted.Generation.Value, state.Generation);
        Assert.Equal(scenario.Commit.Generation.Value, state.OriginGeneration);
        Assert.Equal(scenario.Commit.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.All(await verify.OwnedObjects.AsNoTracking().ToArrayAsync(), x => Assert.Equal(scenario.Commit.Generation.Value, x.Generation));
        Assert.Equal(configured ? 4 : 0, await verify.RuntimeConfigurationPermissions.CountAsync(x => x.Status == "Published"));
        _ = await InternalGenerationAuthority.RequireReadAsync(verify, scenario.Commit, default);
        Assert.Equal(plan.Graph.WorkloadUpdate!.Document.Members.Single().Operation, state.OriginOperation);
        Assert.False(await verify.ActionIntents.AnyAsync(x => x.Generation == accepted.Generation.Value && x.SubjectKind == "Resource"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Internal_update_prepares_exact_successor_documents_without_initial_completion_authority(bool configured, bool multiple)
    {
        var (scenario, envelope) = await AppliedUpdateBaseline(true, configured, multiple);
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:v2";
        var reference = await ImportUpdate(services, envelope);
        var change = await UpdateChange(services, scenario, reference);
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var plan = (await services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>().CreateInternalPlanAsync(change.Id, "tester", default, handlers)).Plan!;
        var accepted = Assert.IsType<DurableCommitResult.Committed>(await ((PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>())
            .CommitInternalAsync(change.Id, plan.Id, "tester", default, handlers)).Commit;
        var preparation = ConfigurationPreparation(services).Preparation;
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var initialDeployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        var publicActions = ConfiguredActions(services, initialDeployment, identity);
        var publicSubmission = new PostgreSqlWorkloadSubmission(contexts, preparation, publicActions,
            new(contexts, preparation, publicActions, identity.Contract.Action),
            new(new(contexts, preparation, publicActions, scenario.Time)), services.GetRequiredService<DurableOperationSerialization>(),
            new(), initialDeployment.Contract.Action);
        var refused = await publicSubmission.SubmitAsync(accepted.DeploymentId, accepted.Generation, "operator", default);
        Assert.Equal("unsupported-inventory", refused.Reason);
        Assert.Equal(0, initialDeployment.Writes); Assert.Equal(0, identity.Writes);
        await using (var unchanged = await contexts.CreateDbContextAsync())
        {
            Assert.False(await unchanged.ConfigurationDocuments.AnyAsync(x => x.Generation == accepted.Generation.Value));
            Assert.Equal(accepted.Generation.Value, (await unchanged.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
        }
        var prepared = await preparation.PrepareAsync(accepted, "tester", default);
        Assert.Null(prepared.Failure);
        Assert.Equal(configured ? multiple ? 4 : 2 : 0, prepared.Prepared.Count);
        if (multiple)
        {
            Assert.Equal(2, prepared.Prepared.Select(x => x.Workload).Distinct().Count());
            Assert.All(prepared.Prepared.GroupBy(x => x.Workload), group => Assert.Equal(2, group.Count()));
            Assert.Contains("UnchangedImage", plan.Graph.WorkloadUpdate!.Document.Members.Single(x => x.Component == "worker").Delta.Json);
        }
        Assert.Equal(prepared.Prepared, (await preparation.PrepareAsync(accepted, "retry", default)).Prepared);
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Equal(scenario.Commit.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.All(prepared.Prepared, x => Assert.False(verify.WorkloadConfigurationRecipients.AsNoTracking().Any(r => r.Generation == scenario.Commit.Generation.Value && r.RevisionId.ToString() == x.Revision.ToString())));
        await Assert.ThrowsAsync<InvalidDataException>(() => InternalGenerationAuthority.RequireAsync(verify, accepted, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => InitialRetirementAuthority.ValidateAsync(verify,
            verify.DeploymentGenerations.AsNoTracking().Single(x => x.Generation == accepted.Generation.Value), default));
    }
}
