using Haby.Application;
using Haby.Kubernetes;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    private sealed partial class UpdateSubmissionFixture
    {
        internal bool OldRecipients, PendingReadiness;
        internal int ReadinessReads, RecipientReads;
        public int MaximumObservationCalls => 5;
        internal Func<CancellationToken, Task>? BeforeRecipients;
        public int ObservationSeconds(string intent, string receipt) => adapter.ObservationSeconds(intent, receipt);
        public bool ValidateObservation(string intent, string receipt, RolloutObservation observation) => adapter.ValidateObservation(intent, receipt, observation);
        public ValueTask<RolloutObservation> ReadinessAsync(InternalActionContext context, string payload, string receipt, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ReadinessReads++;
            var intent = DeploymentIntents.Decode(payload); var saved = DeploymentIntents.DecodeReceipt(receipt);
            var obj = DeploymentObjects.Read(DeploymentObjects.Create(intent));
            obj["metadata"]!["uid"] = saved.Uid; obj["metadata"]!["resourceVersion"] = "22"; obj["metadata"]!["generation"] = saved.KubernetesGeneration;
            obj["status"] = System.Text.Json.Nodes.JsonNode.Parse("""{"observedGeneration":2,"replicas":1,"updatedReplicas":1,"readyReplicas":1,"availableReplicas":1}"""u8);
            if (PendingReadiness) obj["status"]!["observedGeneration"] = 1;
            return ValueTask.FromResult(DeploymentReadiness.Evaluate(obj, intent, saved));
        }
        public async ValueTask<RecipientObservation> ObserveRecipientsAsync(InternalActionContext context, string intent, string receipt, RecipientObservationOptions options, CancellationToken token)
        {
            RecipientReads++; if (BeforeRecipients is not null) await BeforeRecipients(token); token.ThrowIfCancellationRequested();
            return new(!OldRecipients, OldRecipients ? "old-recipients-present" : "old-recipients-absent", new("fixture-recipient/v1", receipt));
        }
        public bool ValidateRecipients(string intent, string receipt, RecipientObservation observation) =>
            observation.Evidence == new DurableActionPayload("fixture-recipient/v1", receipt);
    }

    [Theory]
    [InlineData(true, "none")]
    [InlineData(true, "mixed")]
    [InlineData(true, "competing-waits")]
    [InlineData(true, "expired-renewal")]
    [InlineData(true, "recipient-budget")]
    [InlineData(false, "missing-workload")]
    [InlineData(true, "recipient-failure")]
    [InlineData(true, "missing-progress")]
    [InlineData(true, "live-reference-race")]
    [InlineData(true, "cancel-after-start")]
    [InlineData(true, "cancel-after-permission")]
    [InlineData(false, "none")]
    [InlineData(true, "old-pod")]
    [InlineData(true, "stale-ready")]
    [InlineData(true, "after-start")]
    [InlineData(true, "after-permission")]
    [InlineData(true, "before-applied")]
    [InlineData(true, "after-applied")]
    [InlineData(true, "cancel")]
    [InlineData(true, "target-revoked")]
    [InlineData(true, "obsolete-revoked")]
    public async Task Internal_UPDATE_requires_fresh_readiness_and_recipient_absence_then_retires_exact_permissions_and_Applies(bool configured, string interruption)
    {
        var (scenario, envelope) = await AppliedUpdateBaseline(interruption != "mixed", configured, multiple: interruption == "missing-workload");
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:v2";
        var change = await UpdateChange(services, scenario, await ImportUpdate(services, envelope));
        var registrations = services.GetServices<IInternalActionHandler>().ToArray();
        var plan = (await services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>().CreateInternalPlanAsync(change.Id, "tester", default, registrations)).Plan!;
        var accepted = Assert.IsType<DurableCommitResult.Committed>(await ((PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>())
            .CommitInternalAsync(change.Id, plan.Id, "tester", default, registrations)).Commit;
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var configuration = ConfigurationPreparation(services).Preparation;
        var update = new UpdateSubmissionFixture(); var create = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture { Exists = true };
        var armed = true;
        using var canceled = new CancellationTokenSource();
        void Fault(InternalActionFault point)
        {
            if (armed && (interruption == "cancel-after-start" && point == InternalActionFault.AfterRetirementStart ||
                interruption == "cancel-after-permission" && point == InternalActionFault.AfterRetirementPermission))
            { armed = false; canceled.Cancel(); return; }
            if (!armed || !(interruption is "after-start" or "live-reference-race" && point == InternalActionFault.AfterRetirementStart ||
                interruption is "after-permission" or "missing-progress" && point == InternalActionFault.AfterRetirementPermission ||
                interruption == "before-applied" && point == InternalActionFault.BeforeUpdateApplied ||
                interruption == "after-applied" && point == InternalActionFault.AfterUpdateApplied)) return;
            armed = false; throw new IOException("Simulated retirement response loss.");
        }
        InternalDurableActions Actions(bool isolated = false) => new(contexts, [create, identity, update], services.GetServices<IProviderConfigurationCodec>(),
            services.GetServices<IExternalOperationOutputContract>(), services.GetRequiredService<DurableActionAdmission>(),
            isolated ? new DurableOperationSerialization() : services.GetRequiredService<DurableOperationSerialization>(), scenario.Time, Fault, configuration);
        var actions = Actions();
        Assert.Null((await configuration.PrepareAsync(accepted, "tester", default)).Failure);
        var publisher = new InternalRuntimePublication(contexts, configuration, actions, scenario.Time);
        if (configured)
        {
            _ = await new InternalRuntimeIdentityPreparation(contexts, configuration, actions, identity.Contract.Action).PrepareAsync(accepted, default);
            Assert.Null((await new InternalRuntimePublicationPreparation(publisher).PrepareAsync(accepted, "tester", default)).Reason);
        }
        var key = await actions.AcceptAsync(accepted, RuntimeSubject, update.Contract.Action, "update-main", default);
        Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(key, default)).Outcome);
        var options = new RolloutOptions(MaximumRounds: 1);
        if (interruption == "expired-renewal")
        {
            update.PendingReadiness = true;
            var before = await actions.ObserveRolloutAsync(accepted, "update-original-budget", options, default);
            ((AdjustableTimeProvider)scenario.Time).Advance(TimeSpan.FromSeconds(1000));
            var resumed = await actions.ResumeRolloutAsync(accepted, "update-explicit-renewal", "tester",
                [new(before.Members[0].Subject, before.Members[0].Attempt!.Value)], default);
            Assert.NotEqual(before.Members[0].Attempt, resumed.Members[0].Attempt);
            Assert.True(resumed.Members[0].Deadline > before.Members[0].Deadline);
            await using var history = await contexts.CreateDbContextAsync();
            Assert.Equal(2, await history.ObservationAttempts.CountAsync(x => x.Generation == accepted.Generation.Value));
            Assert.Equal(before.Members[0].Deadline, (await history.ObservationAttempts.AsNoTracking().SingleAsync(x => x.Id == before.Members[0].Attempt)).Deadline);
            update.PendingReadiness = false;
        }
        var recipientOptions = interruption == "recipient-budget" ? new RecipientObservationOptions(MaximumCalls: 1) : new RecipientObservationOptions();
        update.OldRecipients = interruption == "old-pod"; update.PendingReadiness = interruption == "stale-ready";
        if (interruption == "recipient-failure") update.BeforeRecipients = _ => throw new IOException("Simulated recipient observation unavailable.");
        if (interruption == "cancel") update.BeforeRecipients = _ => { canceled.Cancel(); return Task.CompletedTask; };
        if (interruption is "target-revoked" or "obsolete-revoked")
        {
            RuntimePublicationRequest request;
            if (interruption == "target-revoked") request = (await publisher.PreparationRequestsAsync(accepted, "tester", default))[0];
            else
            {
                await using var read = await contexts.CreateDbContextAsync();
                request = InternalRuntimePublication.ReadReceipt(await read.RuntimePublicationReceipts.AsNoTracking().SingleAsync(x =>
                    x.RequestKey == plan.Graph.WorkloadUpdate!.Document.ObsoletePublications[0].Request)).Request;
            }
            var current = (await publisher.ReadAsync(request.Key, default))!;
            await publisher.ApplyAsync(request with
            {
                Key = "explicit-before-retirement",
                Operation = RuntimePublicationOperation.RevokePermission,
                Origin = null,
                ExpectedToken = current.CurrentPermission!.Token
            }, default);
        }
        var disrupted = interruption is "after-start" or "after-permission" or "before-applied" or "after-applied" or "missing-progress" or "live-reference-race";
        RolloutResult? first = null;
        if (interruption == "competing-waits")
        {
            var arrived = 0; var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            update.BeforeRecipients = async ct =>
            {
                if (Interlocked.Increment(ref arrived) == 2) both.TrySetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            };
            var competing = await Task.WhenAll(actions.WaitUpdateAsync(accepted, "first-update-wait", "tester", options, recipientOptions, default).AsTask(),
                Actions(isolated: true).WaitUpdateAsync(accepted, "competing-update-wait", "tester", options, recipientOptions, default).AsTask());
            Assert.All(competing, x => Assert.True(x.Applied, x.CompletionReason));
            Assert.Equal(competing[0].Retirement, competing[1].Retirement); first = competing[0];
            update.BeforeRecipients = null;
        }
        else if (disrupted) await Assert.ThrowsAsync<IOException>(() => actions.WaitUpdateAsync(accepted, "first-update-wait", "tester", options, recipientOptions, default).AsTask());
        else if (interruption is "cancel" or "cancel-after-start" or "cancel-after-permission") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actions.WaitUpdateAsync(accepted, "first-update-wait", "tester", options, recipientOptions, canceled.Token).AsTask());
        else if (interruption == "recipient-failure") await Assert.ThrowsAsync<IOException>(() => actions.WaitUpdateAsync(accepted, "first-update-wait", "tester", options, recipientOptions, default).AsTask());
        else first = await actions.WaitUpdateAsync(accepted, "first-update-wait", "tester", options, recipientOptions, default);
        if (interruption == "recipient-budget")
        {
            Assert.Equal(0, update.RecipientReads);
            await Assert.ThrowsAsync<InvalidDataException>(() => actions.WaitUpdateAsync(accepted, "first-update-wait", "tester", options, new(), default).AsTask());
        }
        if (interruption is "old-pod" or "stale-ready" or "target-revoked" or "obsolete-revoked" || disrupted && interruption != "after-applied" || interruption is "cancel" or "cancel-after-start" or "cancel-after-permission" or "recipient-failure" or "missing-workload" or "recipient-budget")
        {
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.Equal(scenario.Commit.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.False(await verify.GenerationStageCheckpoints.AnyAsync(x => x.Generation == accepted.Generation.Value && x.Stage == "RetirementCompleted"));
            if (interruption is "old-pod" or "stale-ready") Assert.Equal(4, await verify.RuntimeConfigurationPermissions.CountAsync(x => x.Status == "Published"));
        }
        if (interruption is "target-revoked" or "obsolete-revoked") return;
        if (interruption == "missing-workload")
        {
            var worker = await actions.AcceptAsync(accepted, new ProviderInput.Workload(Haby.Domain.ManifestLocalId.Create("worker"), Haby.Domain.ManifestLocalId.Create("main")), update.Contract.Action, "update-worker", default);
            Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(worker, default)).Outcome);
        }
        if (interruption is "missing-progress" or "live-reference-race")
        {
            await using (var arrange = await contexts.CreateDbContextAsync())
            {
                if (interruption == "missing-progress")
                    await arrange.RuntimePublicationReceipts.Where(x => x.RequestKey.StartsWith("retire-")).ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "null"));
                else
                {
                    var existing = await arrange.WorkloadConfigurationRecipients.AsNoTracking().FirstAsync(x => x.Generation == accepted.Generation.Value);
                    arrange.WorkloadConfigurationRecipients.Add(new()
                    {
                        DeploymentId = existing.DeploymentId,
                        Generation = existing.Generation,
                        Component = existing.Component,
                        Workload = "active-preparation",
                        Document = existing.Document,
                        CheckpointId = existing.CheckpointId,
                        RevisionId = plan.Graph.WorkloadUpdate!.Document.ObsoletePublications[0].Revision,
                        InputFingerprint = existing.InputFingerprint,
                        Actor = existing.Actor,
                        CreatedAt = existing.CreatedAt
                    });
                    await arrange.SaveChangesAsync();
                }
            }
            if (interruption == "live-reference-race")
                await Assert.ThrowsAsync<InvalidDataException>(() => Actions().WaitUpdateAsync(accepted, "refuse-changed-retirement-progress", "tester", options, new(), default).AsTask());
            else Assert.False((await Actions().WaitUpdateAsync(accepted, "refuse-changed-retirement-progress", "tester", options, new(), default)).Applied);
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.Equal(scenario.Commit.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Equal(interruption == "missing-progress" ? 1 : 0, await verify.RuntimeConfigurationPermissions.CountAsync(x => x.Status == "Revoked"));
            return;
        }
        update.OldRecipients = false; update.PendingReadiness = false; update.BeforeRecipients = null;
        var completed = first is { Applied: true } ? first : await Actions().WaitUpdateAsync(accepted, "restart-update-wait", "tester", options, new(), default);
        Assert.True(completed.Ready); Assert.True(completed.Applied, completed.CompletionReason);
        Assert.Equal(interruption == "missing-workload" ? 2 : 1, update.Writes); Assert.Equal(0, identity.Writes);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            Assert.Equal(accepted.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Equal(configured ? 2 : 0, await verify.RuntimeConfigurationPermissions.CountAsync(x => x.Status == "Revoked"));
            Assert.Equal(configured ? 2 : 0, await verify.RuntimeConfigurationPermissions.CountAsync(x => x.Status == "Published"));
            Assert.Equal(configured, await verify.GenerationStageCheckpoints.AnyAsync(x => x.Generation == accepted.Generation.Value && x.Stage == "RetirementStarted"));
            _ = await InternalGenerationAuthority.RequireStageReadAsync(verify, accepted, default);
            _ = await InternalGenerationAuthority.RequireReadAsync(verify, scenario.Commit, default);
            var source = await verify.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == accepted.Generation.Value);
            source.ConfigurationJson += " ";
            await Assert.ThrowsAsync<InvalidDataException>(() => InternalGenerationAuthority.RequireStageReadAsync(verify, source, default));
        }
        var again = await Actions().WaitUpdateAsync(accepted, "fresh-update-confirmation", "tester", options, new(), default);
        Assert.True(again.Applied); Assert.Equal(completed.Retirement, again.Retirement);
        var replay = await Actions().WaitUpdateAsync(accepted, "fresh-update-confirmation", "tester", options, new(), default);
        Assert.True(replay.Replayed); Assert.Equal(interruption == "missing-workload" ? 2 : 1, update.Writes);
    }
}
