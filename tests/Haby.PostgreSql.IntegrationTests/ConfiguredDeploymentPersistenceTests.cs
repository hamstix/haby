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
    private sealed class ConfiguredSubmissionFixture : IInternalActionHandler, IInternalReadinessHandler
    {
        private readonly InternalDeploymentAction adapter = new(null!);
        internal int Writes, Reads;
        internal bool LoseResponse;
        internal bool Pending;
        internal int ReadinessReads;
        internal Func<CancellationToken, Task>? BeforeRead;
        public int ObservationSeconds(string intent, string receipt) => adapter.ObservationSeconds(intent, receipt);
        public bool ValidateObservation(string intent, string receipt, RolloutObservation observation) => adapter.ValidateObservation(intent, receipt, observation);
        public async ValueTask<RolloutObservation> ReadinessAsync(InternalActionContext context, string payload, string receipt, CancellationToken token)
        {
            Interlocked.Increment(ref ReadinessReads);
            if (BeforeRead is not null) await BeforeRead(token);
            var intent = DeploymentIntents.Decode(payload); var saved = DeploymentIntents.DecodeReceipt(receipt);
            var obj = DeploymentObjects.Read(DeploymentObjects.Create(intent));
            obj["metadata"]!["uid"] = saved.Uid; obj["metadata"]!["resourceVersion"] = "current"; obj["metadata"]!["generation"] = saved.KubernetesGeneration;
            var count = Pending ? 0 : intent.Target.Replicas;
            obj["status"] = new System.Text.Json.Nodes.JsonObject { ["observedGeneration"] = saved.KubernetesGeneration,
                ["replicas"] = count, ["updatedReplicas"] = count, ["readyReplicas"] = count, ["availableReplicas"] = count };
            return DeploymentReadiness.Evaluate(obj, intent, saved);
        }
        public InternalActionContract Contract => adapter.Contract;
        public bool ObservationRecovery => true;
        public bool RequiresRuntimePublication => true;
        public InternalManagedTarget? ManagedTarget(InternalActionContext context) => adapter.ManagedTarget(context);
        public IReadOnlyList<ManifestLocalId> PreparationDocuments(InternalActionContext context) => adapter.PreparationDocuments(context);
        public string Prepare(InternalActionContext context) => adapter.Prepare(context);
        public bool Validate(InternalActionContext context, string payload) => adapter.Validate(context, payload);
        public bool ValidateReceipt(InternalActionContext context, string payload, string receipt, bool observation) => adapter.ValidateReceipt(context, payload, receipt, observation);
        public InternalObjectConfirmation? ObjectConfirmation(InternalActionContext context, string payload, string receipt) => adapter.ObjectConfirmation(context, payload, receipt);
        private static InternalActionResult Confirm(string payload, bool observation)
        {
            var i = DeploymentIntents.Decode(payload);
            return new(InternalActionOutcome.Confirmed, "fixture-confirmed", DeploymentIntents.Encode(new DeploymentReceipt(3,
                i.Binding, i.Namespace, i.Name, i.Owner, i.ManagedWorkload, i.TargetGeneration, i.Operation, "deployment-uid", 1, i.Target,
                observation ? ConfirmationSource.Observation : ConfirmationSource.Response)));
        }
        public ValueTask<InternalActionResult> ExecuteAsync(InternalActionContext context, string payload, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Writes++; return ValueTask.FromResult(LoseResponse ? new(InternalActionOutcome.Uncertain, "fixture-response-lost") : Confirm(payload, false)); }
        public ValueTask<InternalActionResult> ObserveAsync(InternalActionContext context, string payload, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Reads++; return ValueTask.FromResult(Confirm(payload, true)); }
    }
    private static InternalDurableActions ConfiguredActions(ServiceProvider services, ConfiguredSubmissionFixture deployment, IdentityFixture identity,
        Action<InternalActionFault>? fault = null) => new(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), [deployment, identity],
            services.GetServices<IProviderConfigurationCodec>(), services.GetServices<IExternalOperationOutputContract>(), services.GetRequiredService<DurableActionAdmission>(),
            services.GetRequiredService<DurableOperationSerialization>(), services.GetRequiredService<TimeProvider>(), fault, ConfigurationPreparation(services).Preparation);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Configured_submission_consumes_exact_preparation_and_restores_same_intent_inventory_without_Applied(bool initial)
    {
        var scenario = await InternalScenarioAsync(initialOnly: initial, configureManifest: e =>
        {
            ConfiguredDocuments(e, secondDocument: true);
            if (!initial) e["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["overrides"] =
                System.Text.Json.Nodes.JsonNode.Parse("""{"resources":{"requests":{"memory":"64Mi"},"limits":{"memory":"256Mi"}}}"""u8);
        }, resourcesJson: """{"requests":{"cpu":"250m","memory":"128Mi"},"limits":{"cpu":"1","memory":"512Mi"}}""");
        var identity = new IdentityFixture(); var deployment = new ConfiguredSubmissionFixture(); DurableOperationKey key; string payload;
        Guid binding;
        await using (var services = InternalServices(scenario.Connection, scenario.Time))
        {
            Assert.Equal(2, (await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default)).Prepared.Count);
            binding = (await IdentityPreparation(services, identity).PrepareAsync(scenario.Commit, default)).Single().Binding!.Value;
            var published = await new InternalRuntimePublicationPreparation(Publications(services, identity)).PrepareAsync(scenario.Commit, "tester", default);
            Assert.Null(published.Reason); Assert.Equal(2, published.Completed.Count);
            var actions = ConfiguredActions(services, deployment, identity);
            key = await actions.AcceptAsync(scenario.Commit, RuntimeSubject, deployment.Contract.Action, "configured", default);
            payload = (await actions.HistoryAsync(key, default))!.Intent.Payload;
            var projection = DeploymentIntents.Decode(payload).Target;
            Assert.Equal(initial ? "250m" : null, projection.Resources!.Requests!.Cpu);
            Assert.Equal(initial ? "128Mi" : "64Mi", projection.Resources.Requests.Memory);
            var runtime = projection.Runtime!;
            var bootstrap = Haby.Configuration.Contracts.RuntimeBootstrapCodec.Decode(runtime.Bootstrap);
            Assert.Equal(binding.ToString("D"), bootstrap.BindingId);
            Assert.Equal(new[] { "settings", "z-last" }, bootstrap.Documents.Keys);
            Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(key, default)).Outcome);
            var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            var codec = new Haby.Kubernetes.Contracts.KubernetesPlacementCodec();
            var later = codec.Decode("""{"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"later","revision":1},"spec":{"namespace":"test","runtimeConfiguration":{"endpoint":"https://changed.example/","tls":{"mode":"system"}}}}"""u8.ToArray());
            var alias = ProviderAlias.Create("kubernetes.primary");
            var reference = new PlacementRevisionKey(scenario.Environment, alias, later.Name, later.Revision);
            await new PostgreSqlPlacementRevisionStore(contexts, codec).CompareAndCreateAsync(reference, later, new("tester", DateTimeOffset.UtcNow), default);
            var selections = new PostgreSqlPlacementSelectionStore(contexts, codec); var target = PlacementSelectionTarget.Create("environment", "", "", "");
            var old = (await selections.FindAsync(scenario.Environment, alias, target, default))!;
            await selections.ApplyAsync(target, reference, new PlacementSelectionPrecondition.ExpectedToken(old.Token), new("tester", DateTimeOffset.UtcNow, null), default);
        }
        var restored = await RestoreRuntimeCatalog(scenario.Connection);
        await using var restarted = InternalServices(restored, scenario.Time);
        var replay = ConfiguredActions(restarted, deployment, identity);
        Assert.Equal(key, await replay.AcceptAsync(scenario.Commit, RuntimeSubject, deployment.Contract.Action, "configured", default));
        Assert.Equal(payload, (await replay.HistoryAsync(key, default))!.Intent.Payload);
        var confirmed = DeploymentIntents.DecodeReceipt((await replay.HistoryAsync(key, default))!.Output!.Payload);
        Assert.Equal(DeploymentIntents.Decode(payload).Target.Resources, confirmed.Projection.Resources);
        Assert.Equal(InternalActionOutcome.Confirmed, (await replay.ExecuteAsync(key, default)).Outcome);
        Assert.Equal(1, deployment.Writes); Assert.Equal(1, deployment.Reads); Assert.Equal(1, identity.Writes);
        await using var verify = await restarted.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(binding, (await verify.RuntimeIdentityBindings.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(new[] { "deployment", "runtime-identity" }, await verify.OwnedObjects.AsNoTracking().OrderBy(x => x.Role).Select(x => x.Role).ToArrayAsync());
        Assert.Equal(2, await verify.RuntimePublicationReceipts.CountAsync());
        Assert.Equal(initial ? null : (long?)1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("before-intent")]
    [InlineData("before-intent-first")]
    [InlineData("before-intent-binding")]
    [InlineData("before-marker")]
    [InlineData("before-marker-binding")]
    [InlineData("after-marker")]
    [InlineData("after-result")]
    public async Task Required_authority_blocks_new_submission_but_cannot_erase_an_already_dispatched_receipt(string boundary)
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e, secondDocument: true));
        var identity = new IdentityFixture(); var deployment = new ConfiguredSubmissionFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        await IdentityPreparation(services, identity).PrepareAsync(scenario.Commit, default);
        var publication = Publications(services, identity);
        var requests = await publication.PreparationRequestsAsync(scenario.Commit, "tester", default);
        if (boundary != "missing") await new InternalRuntimePublicationPreparation(publication).PrepareAsync(scenario.Commit, "tester", default);
        var requiredIndex = boundary.EndsWith("-first", StringComparison.Ordinal) ? 0 : 1;
        var bindingRevoke = boundary.EndsWith("-binding", StringComparison.Ordinal);
        async Task RevokeRequired()
        {
            var current = (await publication.ReadAsync(requests[requiredIndex].Key, default))!;
            if (bindingRevoke)
                await publication.ApplyAsync(requests[requiredIndex] with { Key = "binding-revoke", Operation = RuntimePublicationOperation.RevokeBinding,
                    Revision = null, ExpectedToken = current.CurrentBinding.Token, Origin = null }, default);
            else await publication.ApplyAsync(Revoke(requests[requiredIndex], current.CurrentPermission!.Token), default);
        }
        if (boundary.StartsWith("before-intent", StringComparison.Ordinal)) await RevokeRequired();
        var actions = ConfiguredActions(services, deployment, identity, point =>
        {
            if (point == (boundary == "after-marker" ? InternalActionFault.AfterMarkerCommit : InternalActionFault.AfterExternalResult) &&
                boundary is "after-marker" or "after-result") RevokeRequired().GetAwaiter().GetResult();
        });
        if (boundary == "missing" || boundary.StartsWith("before-intent", StringComparison.Ordinal))
            await Assert.ThrowsAsync<InvalidDataException>(async () => await actions.AcceptAsync(scenario.Commit, RuntimeSubject, deployment.Contract.Action, "configured", default));
        else
        {
            var key = await actions.AcceptAsync(scenario.Commit, RuntimeSubject, deployment.Contract.Action, "configured", default);
            if (boundary.StartsWith("before-marker", StringComparison.Ordinal))
            {
                await RevokeRequired();
                await Assert.ThrowsAsync<InvalidDataException>(async () => await actions.ExecuteAsync(key, default));
                Assert.Empty((await actions.HistoryAsync(key, default))!.Attempts);
            }
            else
            {
                Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(key, default)).Outcome);
                Assert.Equal(InternalActionOutcome.Confirmed, (await ConfiguredActions(services, deployment, identity).ExecuteAsync(key, default)).Outcome);
                Assert.Equal(1, deployment.Writes);
            }
        }
        if (boundary == "missing" || boundary.StartsWith("before-", StringComparison.Ordinal)) Assert.Equal(0, deployment.Writes);
        if (boundary != "missing")
        {
            Assert.Null(await publication.AuthorizeMetadataAsync(requests[requiredIndex].Binding, requests[requiredIndex].Revision!.Value, default));
            var replay = await publication.ApplyAsync(requests[requiredIndex], default);
            Assert.Equal("Revoked", bindingRevoke ? replay.CurrentBinding.Status : replay.CurrentPermission!.Status);
        }
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Fact]
    public async Task Configured_submission_consumes_original_ApplicationLogin_evidence_without_serializing_material()
    {
        using var baseline = new RuntimeAuthenticationLoginScenario(fixture);
        var scenario = await baseline.PrepareMixedAsync();
        await using var services = ApplicationLoginScenario.Services(scenario.Connection, full: true, access: true, internalActions: true);
        await ConfigurationPreparation(services, rendering: Haby.PostgreSql.PostgreSqlConnectionRendering.CreateCatalog(),
            formatters: Haby.PostgreSql.PostgreSqlConnectionRendering.FormatterCatalog()).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        var identity = new IdentityFixture();
        await IdentityPreparation(services, identity).PrepareAsync(scenario.Commit, default);
        await new InternalRuntimePublicationPreparation(Publications(services, identity)).PrepareAsync(scenario.Commit, "tester", default);
        var deployment = new ConfiguredSubmissionFixture(); var actions = ConfiguredActions(services, deployment, identity);
        var key = await actions.AcceptAsync(scenario.Commit, RuntimeSubject, deployment.Contract.Action, "configured", default);
        Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(key, default)).Outcome);
        var history = (await actions.HistoryAsync(key, default))!;
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        var version = await verify.GeneratedCredentialSlots.Select(x => x.VersionId).SingleAsync();
        var material = await verify.SecretPayloads.AsNoTracking().Where(x => x.SecretVersionId == version).Select(x => x.Bytes).SingleAsync();
        try
        {
            var forbidden = System.Text.Encoding.UTF8.GetString(material);
            var source = await verify.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == scenario.Commit.Generation.Value);
            foreach (var payload in new[] { history.Intent.Payload, history.Output!.Payload, source.ProviderInputsJson, source.ConfigurationJson, source.GraphJson,
                DeploymentObjects.Read(DeploymentObjects.Create(DeploymentIntents.Decode(history.Intent.Payload))).ToJsonString() })
                Assert.DoesNotContain(forbidden, payload, StringComparison.Ordinal);
            Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(material); }
    }

    [Fact]
    public async Task Configured_lost_response_restarts_observation_only_even_after_permission_revoke()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e, secondDocument: true));
        var identity = new IdentityFixture(); var deployment = new ConfiguredSubmissionFixture { LoseResponse = true }; DurableOperationKey key;
        await using (var services = InternalServices(scenario.Connection, scenario.Time))
        {
            await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
            await IdentityPreparation(services, identity).PrepareAsync(scenario.Commit, default);
            var publication = Publications(services, identity);
            var batch = await new InternalRuntimePublicationPreparation(publication).PrepareAsync(scenario.Commit, "tester", default);
            var actions = ConfiguredActions(services, deployment, identity);
            key = await actions.AcceptAsync(scenario.Commit, RuntimeSubject, deployment.Contract.Action, "configured", default);
            Assert.Equal(InternalActionOutcome.Uncertain, (await actions.ExecuteAsync(key, default)).Outcome);
            var first = batch.Completed[0];
            await publication.ApplyAsync(Revoke(first.Receipt.Request, first.CurrentPermission!.Token), default);
        }
        await using var restarted = InternalServices(scenario.Connection, scenario.Time);
        var recovered = ConfiguredActions(restarted, deployment, identity);
        Assert.Equal(InternalActionOutcome.Confirmed, (await recovered.RecoverByObservationAsync(key, default)).Outcome);
        var history = (await recovered.HistoryAsync(key, default))!;
        Assert.Equal(1, deployment.Writes); Assert.Equal(1, deployment.Reads);
        Assert.Equal(nameof(DurableAttemptOutcome.Uncertain), Assert.Single(history.Attempts).Outcome);
        Assert.True(Assert.Single(history.Observations).Confirmed);
    }
}
