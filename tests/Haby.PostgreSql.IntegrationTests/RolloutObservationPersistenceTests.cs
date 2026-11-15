using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    private static readonly RolloutOptions OneRound = new(MaximumRounds: 1);
    private async Task<InternalScenario> RolloutScenario(bool initial = true, bool shared = false, bool configured = true) =>
        await InternalScenarioAsync(initialOnly: initial, configureManifest: e =>
        {
            if (configured) ConfiguredDocuments(e, shared: shared);
            var workloads = e["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!;
            if (!configured && shared) workloads["second"] = workloads["main"]!.DeepClone();
            foreach (var item in workloads.AsObject()) item.Value!["properties"]!["overrides"] = new JsonObject { ["rolloutSeconds"] = item.Key == "main" ? 2 : 4 };
        });
    private static async Task<InternalDurableActions> PrepareRollout(ServiceProvider services, InternalScenario scenario,
        ConfiguredSubmissionFixture deployment, IdentityFixture identity, bool configured = true, Action<InternalActionFault>? fault = null)
    {
        if (configured)
        {
            await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
            await IdentityPreparation(services, identity).PrepareAsync(scenario.Commit, default);
            await new InternalRuntimePublicationPreparation(Publications(services, identity)).PrepareAsync(scenario.Commit, "tester", default);
        }
        return ConfiguredActions(services, deployment, identity, fault);
    }
    private static async Task<DurableOperationKey> SubmitRollout(InternalDurableActions actions, InternalScenario scenario, ConfiguredSubmissionFixture deployment, string workload = "main")
    {
        var key = await actions.AcceptAsync(scenario.Commit, new ProviderInput.Workload(ManifestLocalId.Create("api"), ManifestLocalId.Create(workload)), deployment.Contract.Action, "submit-" + workload, default);
        Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(key, default)).Outcome); return key;
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rollout_ready_preserves_exact_submission_origin_and_deadline_through_current_build_restore(bool initial)
    {
        var scenario = await RolloutScenario(initial); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        RolloutResult ready; DurableOperationKey key;
        await using (var services = InternalServices(scenario.Connection, scenario.Time))
        {
            var actions = await PrepareRollout(services, scenario, deployment, identity);
            key = await SubmitRollout(actions, scenario, deployment);
            ready = await actions.ObserveRolloutAsync(scenario.Commit, "ready", OneRound, default);
            Assert.True(ready.Ready); Assert.NotNull(ready.Submitted); Assert.NotNull(ready.SavedReady);
            Assert.Equal(1, deployment.ReadinessReads);
            var history = (await actions.HistoryAsync(key, default))!;
            Assert.Equal(history.Operation.FinalizedAt!.Value.AddSeconds(2), Assert.Single(ready.Members).Deadline);
        }
        var restored = await RestoreRuntimeCatalog(scenario.Connection);
        await using var restarted = InternalServices(restored, scenario.Time);
        var observer = ConfiguredActions(restarted, deployment, identity);
        var replay = await observer.ObserveRolloutAsync(scenario.Commit, "ready", OneRound, default);
        Assert.Equal(RolloutJson.Encode(ready with { Replayed = true }), RolloutJson.Encode(replay)); Assert.Equal(1, deployment.ReadinessReads);
        deployment.Pending = true;
        var current = await observer.ObserveRolloutAsync(scenario.Commit, "fresh-conflict", OneRound, default);
        Assert.False(current.Ready); Assert.Equal(ready.SavedReady, current.SavedReady); Assert.Equal(RolloutOutcome.Pending, Assert.Single(current.Members).Observation.Outcome);
        Assert.Equal(ready.Members[0].Attempt, current.Members[0].Attempt); Assert.Equal(ready.Members[0].Deadline, current.Members[0].Deadline);
        await using var verify = await restarted.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(initial ? null : (long?)1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Equal(3, await verify.GenerationStageCheckpoints.CountAsync(x => x.Generation == scenario.Commit.Generation.Value));
        Assert.Equal(1, deployment.Writes); Assert.Equal(0, deployment.Reads);
    }
    [Fact]
    public async Task Rollout_expiry_is_retained_single_fresh_read_can_recover_and_resume_is_audited_idempotent()
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture { Pending = true }; var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity); await SubmitRollout(actions, scenario, deployment);
        ((AdjustableTimeProvider)scenario.Time).Advance(TimeSpan.FromSeconds(2));
        var expired = await actions.ObserveRolloutAsync(scenario.Commit, "expired", new(MaximumRounds: 20), default);
        Assert.False(expired.Ready); var original = Assert.Single(expired.Members); Assert.Equal(RolloutBudget.Expired, original.Budget);
        Assert.Equal(1, deployment.ReadinessReads);
        var resume = new[] { new RolloutResume(original.Subject, original.Attempt!.Value) };
        var renewed = await actions.ResumeRolloutAsync(scenario.Commit, "resume", "operator", resume, default);
        Assert.Equal(1, deployment.ReadinessReads); Assert.Equal(RolloutOutcome.NotObserved, renewed.Members[0].Observation.Outcome);
        Assert.NotEqual(original.Attempt, renewed.Members[0].Attempt); Assert.Equal(original.Deadline!.Value.AddSeconds(2), renewed.Members[0].Deadline);
        Assert.Equal(RolloutJson.Encode(renewed with { Replayed = true }), RolloutJson.Encode(await actions.ResumeRolloutAsync(scenario.Commit, "resume", "operator", resume, default)));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await actions.ResumeRolloutAsync(scenario.Commit, "stale", "operator", resume, default));
        ((AdjustableTimeProvider)scenario.Time).Advance(TimeSpan.FromSeconds(2)); deployment.Pending = false;
        var recovered = await actions.ObserveRolloutAsync(scenario.Commit, "recovered", new(MaximumRounds: 20), default);
        Assert.True(recovered.Ready); Assert.Equal(RolloutBudget.Expired, recovered.Members[0].Budget); Assert.Equal(2, deployment.ReadinessReads);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(2, await verify.ObservationAttempts.CountAsync()); Assert.Equal(2, await verify.ObservationRecords.CountAsync(x => x.Kind == "Expired"));
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration); Assert.Equal(1, deployment.Writes);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rollout_complete_set_cannot_be_fabricated_from_a_confirmed_prefix_or_quota_limited_old_calls(bool configured)
    {
        var scenario = await RolloutScenario(shared: true, configured: configured); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured); await SubmitRollout(actions, scenario, deployment);
        var partial = await actions.ObserveRolloutAsync(scenario.Commit, "prefix", OneRound, default);
        Assert.False(partial.Ready); Assert.Null(partial.Submitted); Assert.Null(partial.SavedReady);
        Assert.Contains(partial.Members, x => x.Observation.Outcome == RolloutOutcome.NotSubmitted);
        await SubmitRollout(actions, scenario, deployment, "second");
        var quota = await actions.ObserveRolloutAsync(scenario.Commit, "quota", OneRound with { MaximumCalls = 1 }, default);
        Assert.False(quota.Ready); Assert.NotNull(quota.Submitted); Assert.Null(quota.SavedReady);
        Assert.Single(quota.Members, x => x.Observation.Outcome == RolloutOutcome.NotObserved);
        var other = await actions.ObserveRolloutAsync(scenario.Commit, "quota-next", OneRound with { MaximumCalls = 1 }, default);
        Assert.False(other.Ready); Assert.Null(other.SavedReady);
        Assert.NotEqual(quota.Members.Single(x => x.Observation.Outcome == RolloutOutcome.Ready).Subject,
            other.Members.Single(x => x.Observation.Outcome == RolloutOutcome.Ready).Subject);
        var ready = await actions.ObserveRolloutAsync(scenario.Commit, "whole", OneRound, default); Assert.True(ready.Ready);
        Assert.Equal(TimeSpan.FromSeconds(2), ready.Members[1].Deadline!.Value - ready.Members[0].Deadline!.Value);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rollout_concurrent_resumes_use_expected_attempt_and_same_request_converges(bool sameRequest)
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity); await SubmitRollout(actions, scenario, deployment);
        var first = await actions.ObserveRolloutAsync(scenario.Commit, "first", OneRound, default);
        ((AdjustableTimeProvider)scenario.Time).Advance(TimeSpan.FromSeconds(2));
        var resume = new[] { new RolloutResume(first.Members[0].Subject, first.Members[0].Attempt!.Value) };
        await using var independent = InternalServices(scenario.Connection, scenario.Time);
        var other = ConfiguredActions(independent, deployment, identity);
        async Task<bool> Invoke(InternalDurableActions target, string request)
        {
            try { await target.ResumeRolloutAsync(scenario.Commit, request, "operator", resume, default); return true; }
            catch (InvalidDataException) { return false; }
        }
        var results = await Task.WhenAll(Invoke(actions, "race"), Invoke(other, sameRequest ? "race" : "race-other"));
        Assert.Equal(sameRequest ? 2 : 1, results.Count(x => x));
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(2, await verify.ObservationAttempts.CountAsync()); Assert.Equal(1, deployment.Writes);
    }
    [Fact]
    public async Task Rollout_cancellation_and_request_identity_do_not_reset_durable_deadline()
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture { Pending = true }; var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity); await SubmitRollout(actions, scenario, deployment);
        var first = await actions.ObserveRolloutAsync(scenario.Commit, "first", OneRound, default);
        using var cancel = new CancellationTokenSource();
        deployment.BeforeRead = token => { cancel.Cancel(); return Task.FromCanceled(token); };
        var canceled = await actions.ObserveRolloutAsync(scenario.Commit, "cancel", OneRound, cancel.Token);
        Assert.Equal(RolloutStop.Canceled, canceled.Stop); Assert.Equal(first.Members[0].Deadline, canceled.Members[0].Deadline);
        deployment.BeforeRead = null;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await actions.ObserveRolloutAsync(scenario.Commit, "first", OneRound with { MaximumCalls = 1 }, default));
        var reads = deployment.ReadinessReads;
        _ = await actions.ObserveRolloutAsync(scenario.Commit, "first", OneRound, default); Assert.Equal(reads, deployment.ReadinessReads);
    }
    [Fact]
    public async Task Rollout_checkpoint_and_last_ready_sample_roll_back_together_and_interrupted_request_never_redispatches()
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity, fault: point =>
        { if (point == InternalActionFault.BeforeReadinessCheckpoint) throw new InvalidOperationException("injected completion fault"); });
        await SubmitRollout(actions, scenario, deployment);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await actions.ObserveRolloutAsync(scenario.Commit, "interrupted", OneRound, default));
        await using (var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.False(await verify.GenerationStageCheckpoints.AnyAsync(x => x.Stage == "RolloutReady"));
            Assert.Null((await verify.ObservationRecords.AsNoTracking().SingleAsync(x => x.Kind == "Read")).Payload);
        }
        var restarted = ConfiguredActions(services, deployment, identity);
        Assert.Equal(RolloutStop.Interrupted, (await restarted.ObserveRolloutAsync(scenario.Commit, "interrupted", OneRound, default)).Stop);
        Assert.Equal(1, deployment.ReadinessReads);
        Assert.True((await restarted.ObserveRolloutAsync(scenario.Commit, "new-request", OneRound, default)).Ready); Assert.Equal(1, deployment.Writes);
    }
    [Fact]
    public async Task Rollout_unresolved_write_and_cross_generation_commit_cannot_authorize_readiness()
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture { LoseResponse = true }; var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity);
        var key = await actions.AcceptAsync(scenario.Commit, RuntimeSubject, deployment.Contract.Action, "uncertain", default);
        Assert.Equal(InternalActionOutcome.Uncertain, (await actions.ExecuteAsync(key, default)).Outcome);
        var result = await actions.ObserveRolloutAsync(scenario.Commit, "observe", OneRound, default);
        Assert.False(result.Ready); Assert.Equal(RolloutOutcome.NotSubmitted, result.Members[0].Observation.Outcome); Assert.Equal(0, deployment.ReadinessReads);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await actions.ObserveRolloutAsync(scenario.Commit with { Generation = DeploymentGeneration.Create(2) }, "wrong", OneRound, default));
        Assert.Equal(0, deployment.ReadinessReads); Assert.Equal(1, deployment.Writes);
    }
    [Fact]
    public async Task Rollout_old_inflight_result_cannot_satisfy_a_concurrently_resumed_attempt()
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity); await SubmitRollout(actions, scenario, deployment);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        deployment.BeforeRead = async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); };
        var observing = actions.ObserveRolloutAsync(scenario.Commit, "inflight", OneRound, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        RolloutResult resumed;
        try
        {
            ((AdjustableTimeProvider)scenario.Time).Advance(TimeSpan.FromSeconds(2));
            await using var independent = InternalServices(scenario.Connection, scenario.Time);
            var contexts = independent.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            Guid original;
            await using (var read = await contexts.CreateDbContextAsync()) original = (await read.ObservationAttempts.AsNoTracking().SingleAsync()).Id;
            resumed = await ConfiguredActions(independent, deployment, identity).ResumeRolloutAsync(scenario.Commit, "resume-inflight", "operator",
                [new("Workload/api/main", original)], default);
        }
        finally { release.SetResult(); }
        var stale = await observing;
        Assert.False(stale.Ready); Assert.Null(stale.SavedReady); Assert.Equal(resumed.Members[0].Attempt, stale.Members[0].Attempt);
        Assert.Equal("attempt-superseded", stale.Members[0].Observation.Code); Assert.Equal(1, deployment.Writes);
    }
    [Fact]
    public async Task Rollout_permission_revoke_does_not_republish_or_prevent_truthful_exact_object_observation()
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity); await SubmitRollout(actions, scenario, deployment);
        var publication = Publications(services, identity);
        var request = (await publication.PreparationRequestsAsync(scenario.Commit, "tester", default))[0];
        var before = (await publication.ReadAsync(request.Key, default))!;
        await publication.ApplyAsync(Revoke(request, before.CurrentPermission!.Token), default);
        Assert.True((await actions.ObserveRolloutAsync(scenario.Commit, "revoked", OneRound, default)).Ready);
        Assert.Null(await publication.AuthorizeMetadataAsync(request.Binding, request.Revision!.Value, default));
        Assert.Equal(1, deployment.Writes); Assert.Equal(1, identity.Writes);
    }
    [Theory]
    [InlineData("receipt")]
    [InlineData("inventory")]
    public async Task Rollout_fabricated_submission_evidence_is_rejected_before_API_observation(string corrupt)
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity); var key = await SubmitRollout(actions, scenario, deployment);
        await using (var arrange = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            if (corrupt == "receipt")
                await arrange.OperationOutputs.Where(x => x.OperationId == key.OperationId.Value).ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{}"));
            else await arrange.OwnedObjects.Where(x => x.Role == "deployment").ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{}"));
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await actions.ObserveRolloutAsync(scenario.Commit, "fabricated", OneRound, default));
        Assert.Equal(0, deployment.ReadinessReads);
    }
    private sealed class SubmissionOnly(ConfiguredSubmissionFixture inner) : IInternalActionHandler
    {
        public InternalActionContract Contract => inner.Contract;
        public bool ObservationRecovery => inner.ObservationRecovery;
        public bool RequiresRuntimePublication => true;
        public InternalManagedTarget? ManagedTarget(InternalActionContext context) => inner.ManagedTarget(context);
        public IReadOnlyList<ManifestLocalId> PreparationDocuments(InternalActionContext context) => inner.PreparationDocuments(context);
        public string Prepare(InternalActionContext context) => inner.Prepare(context);
        public bool Validate(InternalActionContext context, string payload) => inner.Validate(context, payload);
        public bool ValidateReceipt(InternalActionContext context, string payload, string receipt, bool observation) => inner.ValidateReceipt(context, payload, receipt, observation);
        public InternalObjectConfirmation? ObjectConfirmation(InternalActionContext context, string payload, string receipt) => inner.ObjectConfirmation(context, payload, receipt);
        public ValueTask<InternalActionResult> ExecuteAsync(InternalActionContext context, string payload, CancellationToken token) => inner.ExecuteAsync(context, payload, token);
        public ValueTask<InternalActionResult> ObserveAsync(InternalActionContext context, string payload, CancellationToken token) => inner.ObserveAsync(context, payload, token);
    }
    [Fact]
    public async Task Rollout_existing_v2_confirmation_initializes_from_original_anchor_without_a_fresh_budget()
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture { Pending = true }; var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        _ = await PrepareRollout(services, scenario, deployment, identity);
        var old = new InternalDurableActions(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), [new SubmissionOnly(deployment), identity],
            services.GetServices<IProviderConfigurationCodec>(), services.GetServices<IExternalOperationOutputContract>(), services.GetRequiredService<DurableActionAdmission>(),
            services.GetRequiredService<DurableOperationSerialization>(), scenario.Time, configurationPreparation: ConfigurationPreparation(services).Preparation);
        var key = await SubmitRollout(old, scenario, deployment);
        var confirmation = (await old.HistoryAsync(key, default))!.Operation.FinalizedAt!.Value;
        ((AdjustableTimeProvider)scenario.Time).Advance(TimeSpan.FromSeconds(10));
        var result = await ConfiguredActions(services, deployment, identity).ObserveRolloutAsync(scenario.Commit, "late-first", new(MaximumRounds: 10), default);
        Assert.False(result.Ready); Assert.Equal(confirmation.AddSeconds(2), result.Members[0].Deadline);
        Assert.Equal(RolloutBudget.Expired, result.Members[0].Budget); Assert.Equal(1, deployment.ReadinessReads);
    }
    private sealed class ReservationDeadlineCrossing(AdjustableTimeProvider time) : DbCommandInterceptor
    {
        internal int Crossings;
        private void Cross(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal) &&
                command.CommandText.Contains("generation_observation_records", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(x => Equals(x.Value, "Read")) &&
                Interlocked.CompareExchange(ref Crossings, 1, 0) == 0) time.Advance(TimeSpan.FromSeconds(2));
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        { Cross(command); return ValueTask.FromResult(result); }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        { Cross(command); return ValueTask.FromResult(result); }
    }
    [Fact]
    public async Task Rollout_deadline_crossed_during_read_reservation_does_not_dispatch_with_the_stale_active_budget()
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture { Pending = true }; var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity); await SubmitRollout(actions, scenario, deployment);
        var boundary = new ReservationDeadlineCrossing((AdjustableTimeProvider)scenario.Time);
        var observing = new InternalDurableActions(new ConfigFactory(scenario.Connection, boundary), [deployment, identity],
            services.GetServices<IProviderConfigurationCodec>(), services.GetServices<IExternalOperationOutputContract>(), services.GetRequiredService<DurableActionAdmission>(),
            services.GetRequiredService<DurableOperationSerialization>(), scenario.Time, configurationPreparation: ConfigurationPreparation(services).Preparation);
        var result = await observing.ObserveRolloutAsync(scenario.Commit, "reservation-crossing", new(MaximumRounds: 2, PollMilliseconds: 100), default);
        Assert.Equal(1, boundary.Crossings); Assert.False(result.Ready); Assert.Equal(RolloutBudget.Expired, result.Members[0].Budget);
        Assert.Equal(1, deployment.ReadinessReads); // Only the explicit post-expiry read, never the stale active one.
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Single(await verify.ObservationRecords.AsNoTracking().Where(x => x.Kind == "Expired").ToArrayAsync());
        var records = await verify.ObservationRecords.AsNoTracking().Where(x => x.Kind == "Read").OrderBy(x => x.Ordinal).ToArrayAsync();
        Assert.Equal(2, records.Length); Assert.Equal("read-timeout", RolloutJson.Observation(records[0].Payload!).Code);
        Assert.Equal(RolloutOutcome.Pending, RolloutJson.Observation(records[1].Payload!).Outcome);
    }
    [Fact]
    public async Task Rollout_observed_expiry_survives_UTC_reversal_and_only_explicit_resume_renews_authority()
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture { Pending = true }; var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity); await SubmitRollout(actions, scenario, deployment);
        ((AdjustableTimeProvider)scenario.Time).Advance(TimeSpan.FromSeconds(2));
        var expired = await actions.ObserveRolloutAsync(scenario.Commit, "before-reversal", new(MaximumRounds: 4, PollMilliseconds: 100), default);
        Assert.Equal(RolloutBudget.Expired, expired.Members[0].Budget);
        ((AdjustableTimeProvider)scenario.Time).Advance(TimeSpan.FromSeconds(-1));
        await using var restarted = InternalServices(scenario.Connection, scenario.Time);
        var observer = ConfiguredActions(restarted, deployment, identity); var reads = deployment.ReadinessReads;
        var reversed = await observer.ObserveRolloutAsync(scenario.Commit, "after-reversal", new(MaximumRounds: 4, PollMilliseconds: 100), default);
        Assert.Equal(RolloutBudget.Expired, reversed.Members[0].Budget); Assert.Equal(expired.Members[0].Deadline, reversed.Members[0].Deadline);
        Assert.Equal(reads + 1, deployment.ReadinessReads);
        var resumed = await observer.ResumeRolloutAsync(scenario.Commit, "resume-reversal", "operator",
            [new(expired.Members[0].Subject, expired.Members[0].Attempt!.Value)], default);
        Assert.NotEqual(expired.Members[0].Attempt, resumed.Members[0].Attempt); Assert.Equal(RolloutBudget.Active, resumed.Members[0].Budget);
        await using var verify = await restarted.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        var historicalExpiry = Assert.Single(await verify.ObservationRecords.AsNoTracking().Where(x => x.Kind == "Expired").ToArrayAsync());
        Assert.Equal(expired.Members[0].Attempt, historicalExpiry.AttemptId); Assert.Equal(expired.Members[0].Deadline, historicalExpiry.ReservedAt);
        Assert.Equal(2, await verify.ObservationAttempts.CountAsync()); Assert.Equal(1, deployment.Writes);
    }
}
