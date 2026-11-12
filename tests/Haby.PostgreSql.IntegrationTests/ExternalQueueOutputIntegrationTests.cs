using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ExactActionAdmissionTests
{
    private static readonly OperationId QueueEvidenceProducerId = OperationId.Create("10-external");
    private static readonly OperationId QueueEvidenceConsumerId = OperationId.Create("20-subscribe");
    private static readonly DurableActionExecutionKey QueueSubscribeKey = new(Key.Admission, OperationContractId.Create("sample.queue.subscribe/v1"));
    private static OperationOutputRef QueueEvidenceSelector => new(QueueEvidenceProducerId, QueueEvidenceContract.Contract, "receipt");
    public sealed record QueueEvidence(string QueueName, string BrokerTarget, string Receipt, string? CredentialSecretId = null, string? CredentialVersionId = null);
    public sealed record QueueSubscribeInput(string QueueName);

    private sealed class QueueEvidenceContract(IProviderConfigurationStore configurations, QueueBoundary boundary) : IExternalOperationOutputContract
    {
        public const string Contract = "sample.queue-evidence/v1";
        public DurableActionExecutionKey Producer => Key;
        public ExternalOperationOutputIdentity Identity => new(boundary.OutputIdentityOverride ?? Contract, "receipt");
        public static ExternalOperationOutput Encode(QueueEvidence evidence) => new(Contract, "receipt",
            JsonSerializer.Serialize(evidence, QueueEvidenceJsonContext.Default.QueueEvidence));
        public static QueueEvidence? Decode(ExternalOperationOutput output)
        {
            if (output.Contract != Contract || output.Key != "receipt") return null;
            try { return JsonSerializer.Deserialize(output.Payload, QueueEvidenceJsonContext.Default.QueueEvidence); }
            catch (JsonException) { return null; }
        }
        public bool Validate(DurableOperationNode node, ExternalOperationOutput output) => node.Action == Producer &&
            node.Input.Format == "sample.queue-create/v1" && Decode(output) is { QueueName: "jobs", BrokerTarget: "queue-broker", Receipt: { Length: > 0 } } &&
            JsonSerializer.Deserialize(node.Input.Json, QueueJsonContext.Default.QueueCreateInput)?.QueueName == "jobs";
        public bool ValidateReuse(DurableOperationKey key, AcceptedOperation accepted, ExternalOperationOutput output,
            IReadOnlyList<ConfirmedOperationOutput> sources, PreparedProviderConfiguration configuration,
            IReadOnlyCollection<ProviderResourceReservation> reservations) =>
            !boundary.RejectAcceptedOutput && Validate(accepted.Node, output) && configuration.ImplementationId == "sample.impl/v1" &&
                Decode(output)?.CredentialSecretId == boundary.DeliveryCredential?.SecretId.ToString() &&
                Decode(output)?.CredentialVersionId == boundary.DeliveryCredential?.SecretVersionId.ToString();
        public async ValueTask<bool> ValidateAcceptedAsync(DurableOperationKey key, AcceptedOperation accepted,
            ExternalOperationOutput output, CancellationToken cancellationToken)
        {
            var configuration = await configurations.FindRevisionAsync<SampleConfiguration>(accepted.Inputs.Environment,
                accepted.Inputs.Providers.RequireUniformResourceProvider().Instance, accepted.Inputs.Providers.RequireUniformResourceProvider().Configuration, cancellationToken);
            return !boundary.RejectAcceptedOutput && Validate(accepted.Node, output) &&
                accepted.Spec.Resources[accepted.Node.ResourceId].Type == QueueContract &&
                Decode(output)?.BrokerTarget == configuration?.Configuration.Target &&
                Decode(output)?.CredentialSecretId == boundary.DeliveryCredential?.SecretId.ToString() &&
                Decode(output)?.CredentialVersionId == boundary.DeliveryCredential?.SecretVersionId.ToString();
        }
    }

    // Test-only adapter boundary: counted queue effects, never PostgreSQL database evidence or a production broker.
    private sealed class QueueEvidenceProducer(IDurableOperationJournal journal, IExternalOperationOutputs outputs,
        IProviderConfigurationStore configurations, QueueBoundary boundary)
        : DurableActionHandler<QueueCreateInput>, IDurableResourcePlanComposer, IDurableResourceReusePolicy, IDurableResourceAdditionPolicy, IDurableResourceRetentionPolicy
    {
        public bool CanReuse(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource,
            DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
            Validate(accepted, resource, inputs, configuration) && Validate(target, resource, inputs, configuration) &&
            accepted.Resources[resource].Properties.Equals(target.Resources[resource].Properties) &&
            accepted.Resources[resource].DeletionPolicy == target.Resources[resource].DeletionPolicy &&
            (!boundary.DependentResources || RelatedClosure(accepted, target, resource, retain: false));

        private static bool RelatedClosure(NormalizedManifestSpec original, NormalizedManifestSpec target, ManifestLocalId resource, bool retain)
        {
            if (resource.Value == "jobs") return true;
            var parent = ManifestLocalId.Create("extra"); var peer = ManifestLocalId.Create("extra-peer");
            if (!original.Resources.ContainsKey(parent) || !original.Resources.ContainsKey(peer)) return false;
            return retain ? !target.Resources.ContainsKey(parent) && !target.Resources.ContainsKey(peer) &&
                original.Resources[parent].DeletionPolicy == ResourceDeletionPolicy.Retain && original.Resources[peer].DeletionPolicy == ResourceDeletionPolicy.Retain :
                target.Resources.ContainsKey(parent) && target.Resources.ContainsKey(peer) &&
                Same(parent) && Same(peer);

            bool Same(ManifestLocalId key) => original.Resources[key].Type == target.Resources[key].Type &&
                original.Resources[key].ProviderRef == target.Resources[key].ProviderRef &&
                original.Resources[key].Properties.Equals(target.Resources[key].Properties) &&
                original.Resources[key].DeletionPolicy == target.Resources[key].DeletionPolicy;
        }

        public bool CanRetain(NormalizedManifestSpec original, NormalizedManifestSpec target, ManifestLocalId resource,
            DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => (resource.Value == "extra" || boundary.DependentResources && resource.Value == "extra-peer") &&
            original.Resources[resource].DeletionPolicy == ResourceDeletionPolicy.Retain &&
            ValidShape(original, resource, configuration) && !target.Resources.ContainsKey(resource) &&
            target.Resources.ContainsKey(ManifestLocalId.Create("jobs")) &&
            (!boundary.DependentResources || RelatedClosure(original, target, resource, retain: true));

        public bool CanAdd(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource,
            DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => (resource.Value == "extra" || boundary.DependentResources && resource.Value == "extra-peer") &&
            !accepted.Resources.ContainsKey(resource) && Validate(target, resource, inputs, configuration) &&
            (!boundary.DependentResources || RelatedClosure(target, target, resource, retain: false));

        public override bool RequiresExternalOutput => true;
        public override ExternalOperationOutputIdentity OutputIdentity => new(QueueEvidenceContract.Contract, "receipt");
        public override DurableActionExecutionKey Key => ExactActionAdmissionTests.Key;
        public override string InputFormat => "sample.queue-create/v1";
        public override JsonTypeInfo<QueueCreateInput> InputTypeInfo => QueueJsonContext.Default.QueueCreateInput;
        public override QueueCreateInput? Prepare(NormalizedResource resource, DurableActionInputReferences inputs) =>
            resource.Type == QueueContract && resource.Properties.Members.Count == 1 &&
            resource.Properties.Members.TryGetValue("queueName", out var name) && name is ConfigurationString { Value: "jobs" }
                ? new("jobs") : null;
        public override IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs)
        {
            var producer = PrepareNode(resourceId.Value == "jobs" ? QueueEvidenceProducerId : OperationId.Create(resourceId.Value + "-create"), resourceId, resource, inputs);
            if (resourceId.Value != "jobs" || boundary.ExternalEvidenceOnly) return [producer];
            return [producer, producer with
            {
                Id = QueueEvidenceConsumerId, Action = QueueSubscribeKey,
                RequiresExternalOutput = false,
                RequiredOutput = null,
                Input = new("sample.queue-subscribe/v1", JsonSerializer.Serialize(new QueueSubscribeInput("jobs"), QueueEvidenceJsonContext.Default.QueueSubscribeInput)),
                Dependencies = [producer.Id], OutputReferences = [QueueEvidenceSelector]
            }];
        }
        public bool Validate(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) =>
            ValidShape(spec, resource, configuration) &&
            (boundary.DependentResources && resource.Value == "extra-peer" || Validate(resource, spec.Resources[resource], inputs));

        // Retention checks the original declaration's shape. A removed Resource has no
        // entry in the target set; production continuity validates its original inputs.
        private bool ValidShape(NormalizedManifestSpec spec, ManifestLocalId resource, PreparedProviderConfiguration configuration) =>
            spec.Resources.Count > 0 && spec.Resources.Count <= (boundary.DependentResources ? 3 : 2) && spec.Components.Values.All(x => x.Workloads.Count == 0) && spec.Resources.TryGetValue(resource, out var declared) && declared.Type == QueueContract &&
            (boundary.DependentResources && resource.Value == "extra-peer"
                ? declared.Properties.Members.Count == 1 && declared.Properties.Members.GetValueOrDefault("parentRef") is ConfigurationString { Value: "extra" } && spec.Resources.ContainsKey(ManifestLocalId.Create("extra"))
                : declared.Properties.Members.Count == 1 && declared.Properties.Members.GetValueOrDefault("queueName") is ConfigurationString { Value: "jobs" }) &&
            configuration.ProviderKindId == "sample/v1" && configuration.ShapeId == "sample.configuration/v1" && configuration.ImplementationId == "sample.impl/v1";
        public IReadOnlyList<DurableOperationNode> Plan(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs,
            PreparedProviderConfiguration configuration) => boundary.DependentResources && resource.Value == "extra-peer"
                ? [new(OperationId.Create("extra-peer-subscribe"), resource, QueueSubscribeKey,
                    new(inputs.Environment, inputs.Providers.RequireUniformResourceProvider().Instance, inputs.Providers.RequireUniformResourceProvider().Configuration, inputs.Providers.RequireUniformResourceProvider().Implementation.Value),
                    new("sample.queue-subscribe/v1", JsonSerializer.Serialize(new QueueSubscribeInput("jobs"), QueueEvidenceJsonContext.Default.QueueSubscribeInput)),
                    [OperationId.Create("extra-create")]) { OutputReferences = [new(OperationId.Create("extra-create"), QueueEvidenceContract.Contract, "receipt")] }]
                : Plan(resource, spec.Resources[resource], inputs);
        public bool ValidateGraph(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs,
            PreparedProviderConfiguration configuration, DurableOperationGraph graph) =>
            Validate(spec, resource, inputs, configuration) && (boundary.DependentResources && resource.Value == "extra-peer"
                ? graph.Nodes.SingleOrDefault(n => n.ResourceId == resource) is { } node &&
                    node.Action == QueueSubscribeKey && node.Dependencies.SequenceEqual([OperationId.Create("extra-create")]) &&
                    node.OutputReferences.SequenceEqual([new(OperationId.Create("extra-create"), QueueEvidenceContract.Contract, "receipt")]) &&
                    node.Input == Plan(spec, resource, inputs, configuration).Single().Input
                : new DurableOperationGraph(Plan(spec, resource, inputs, configuration)).Equals(new DurableOperationGraph(graph.Nodes.Where(n => n.ResourceId == resource))));

        public override async ValueTask<DurableExecutionResult> ExecuteTypedAsync(DurableOperation operation, QueueCreateInput? input, CancellationToken token)
        {
            var config = (await configurations.FindRevisionAsync<SampleConfiguration>(operation.Environment,
                operation.ProviderInstance, operation.ConfigurationRevision, token))!.Configuration;
            Assert.Equal("queue-broker", config.Target); Assert.NotNull(input);
            var started = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(operation.Key, DateTimeOffset.UtcNow, token));
            Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(operation.Key, started.AttemptId, DateTimeOffset.UtcNow, token));
            // A registered mandatory-output producer cannot acknowledge ordinary journal success.
            if (boundary.OutputContractRegistrations == 1)
                Assert.False(await journal.TryRecordSuccessAsync(operation.Key, started.AttemptId, DateTimeOffset.UtcNow, token));
            boundary.Create(input);
            var evidence = new QueueEvidence(input.QueueName, config.Target, "accepted-" + boundary.Calls,
                boundary.DeliveryCredential?.SecretId.ToString(), boundary.DeliveryCredential?.SecretVersionId.ToString());
            boundary.Effects.Add("create:" + evidence.Receipt);
            var completed = false;
            try { completed = await outputs.CompleteAsync(operation.Key, started.AttemptId, QueueEvidenceContract.Encode(evidence), DateTimeOffset.UtcNow, token); }
            catch (InvalidOperationException) { /* Ambiguous evidence registration cannot acknowledge the effect. */ }
            if (!completed)
                await journal.TryFinalizeFailureAsync(operation.Key, started.AttemptId, DurableOperationState.NeedsAttention,
                    DurableOperationReason.UncertainAfterDispatch, DateTimeOffset.UtcNow, CancellationToken.None);
            return new(completed ? DurableExecutionOutcome.Succeeded : DurableExecutionOutcome.NeedsAttention, await journal.FindAsync(operation.Key, default));
        }
    }

    private sealed class QueueEvidenceConsumer(IDurableOperationJournal journal, IExternalOperationOutputs outputs,
        QueueBoundary boundary) : IDurableActionHandler
    {
        public DurableActionExecutionKey Key => QueueSubscribeKey;
        public bool Supports(DurableOperationNode node) => node.Action == Key && node.Input.Format == "sample.queue-subscribe/v1" &&
            JsonSerializer.Deserialize(node.Input.Json, QueueEvidenceJsonContext.Default.QueueSubscribeInput)?.QueueName == "jobs" &&
            (node.Dependencies.SequenceEqual([QueueEvidenceProducerId]) && node.OutputReferences.SequenceEqual([QueueEvidenceSelector]) ||
                boundary.DependentResources && node.ResourceId.Value == "extra-peer" && node.Dependencies.SequenceEqual([OperationId.Create("extra-create")]) &&
                node.OutputReferences.SequenceEqual([new(OperationId.Create("extra-create"), QueueEvidenceContract.Contract, "receipt")]));
        public async ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken token)
        {
            ExternalOperationOutput? output;
            try { output = await outputs.ConsumeAsync(operation.Key, operation.Node.OutputReferences.Single(), token); }
            catch (InvalidOperationException) { output = null; }
            if (output is null) return new(DurableExecutionOutcome.InputUnavailable, operation);
            var evidence = QueueEvidenceContract.Decode(output);
            Assert.Equal(new QueueEvidence("jobs", "queue-broker", operation.Node.ResourceId.Value == "extra-peer" ? "accepted-2" : "accepted-1", boundary.DeliveryCredential?.SecretId.ToString(), boundary.DeliveryCredential?.SecretVersionId.ToString()), evidence);
            var started = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(operation.Key, DateTimeOffset.UtcNow, token));
            Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(operation.Key, started.AttemptId, DateTimeOffset.UtcNow, token));
            boundary.Effects.Add("subscribe:" + evidence!.Receipt);
            Assert.True(await journal.TryRecordSuccessAsync(operation.Key, started.AttemptId, DateTimeOffset.UtcNow, token));
            return new(DurableExecutionOutcome.Succeeded, await journal.FindAsync(operation.Key, token));
        }
    }

    [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
    [JsonSerializable(typeof(QueueEvidence))]
    [JsonSerializable(typeof(QueueSubscribeInput))]
    internal partial class QueueEvidenceJsonContext : JsonSerializerContext;

    [Fact]
    public async Task Non_database_adapter_context_validation_can_refuse_publication_and_consumption_without_common_provider_branches()
    {
        var boundary = new QueueBoundary { ExternalEvidence = true, RejectAcceptedOutput = true };
        var scenario = await GraphCase(boundary);
        var producer = new DurableOperationKey(scenario.Commit.DeploymentId, scenario.Commit.Generation, QueueEvidenceProducerId);
        await using (var act = Services(scenario.Connection, boundary))
            Assert.Equal(DurableExecutionOutcome.NeedsAttention, (await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(producer, default)).Outcome);
        boundary.RejectAcceptedOutput = false;
        await using (var restart = Services(scenario.Connection, boundary))
            Assert.Equal(DurableExecutionOutcome.NeedsAttention, (await restart.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(producer, default)).Outcome);
        Assert.Equal(1, boundary.Calls);
        await using (var verify = OutputContext(scenario.Connection))
        {
            Assert.Empty(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        var acceptedBoundary = new QueueBoundary { ExternalEvidence = true };
        var acceptedScenario = await GraphCase(acceptedBoundary);
        producer = new(acceptedScenario.Commit.DeploymentId, acceptedScenario.Commit.Generation, QueueEvidenceProducerId);
        var consumer = producer with { OperationId = QueueEvidenceConsumerId };
        await using (var act = Services(acceptedScenario.Connection, acceptedBoundary))
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(producer, default)).Outcome);
        acceptedBoundary.RejectAcceptedOutput = true;
        await using (var restart = Services(acceptedScenario.Connection, acceptedBoundary))
            Assert.Null(await restart.GetRequiredService<IExternalOperationOutputs>().ConsumeAsync(consumer, QueueEvidenceSelector, default));
        Assert.Equal(1, acceptedBoundary.Calls);
    }

    [Fact]
    public async Task Non_database_external_output_crosses_generic_dispatch_publication_consumption_and_restart()
    {
        var boundary = new QueueBoundary { ExternalEvidence = true };
        var scenario = await GraphCase(boundary);
        var producer = new DurableOperationKey(scenario.Commit.DeploymentId, scenario.Commit.Generation, QueueEvidenceProducerId);
        var consumer = producer with { OperationId = QueueEvidenceConsumerId };
        var expected = QueueEvidenceContract.Encode(new("jobs", "queue-broker", "accepted-1"));
        Guid attempt;
        await using (var act = Services(scenario.Connection, boundary))
        {
            Assert.Empty(act.GetServices<IGeneratedCredentialOperations>());
            var executor = Assert.IsType<ExactDurableOperationExecutor>(act.GetRequiredService<IDurableOperationExecutor>());
            Assert.Equal(DurableExecutionOutcome.Blocked, (await executor.ExecuteAsync(consumer, default)).Outcome);
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await executor.ExecuteAsync(producer, default)).Outcome);
            var saved = (await executor.ObserveAsync(producer, default))!;
            attempt = Assert.Single(saved.Attempts).AttemptId;
            Assert.Equal(DurableAttemptOutcome.Succeeded, saved.Attempts[0].Outcome);
            Assert.NotNull(saved.Attempts[0].DispatchedAt); Assert.NotNull(saved.Attempts[0].FinishedAt);
            var outputs = act.GetRequiredService<IExternalOperationOutputs>();
            Assert.IsType<PostgreSqlExternalOperationOutputs>(outputs);
            Assert.Equal(expected, await outputs.ConsumeAsync(consumer, QueueEvidenceSelector, default));
            Assert.Null(await outputs.ConsumeAsync(consumer, QueueEvidenceSelector with { OutputKey = "other" }, default));
            Assert.Null(await outputs.ConsumeAsync(consumer with { Generation = DeploymentGeneration.Create(2) }, QueueEvidenceSelector, default));
            Assert.True(await outputs.CompleteAsync(producer, attempt, expected, DateTimeOffset.UtcNow, default));
            Assert.False(await outputs.CompleteAsync(producer, attempt, QueueEvidenceContract.Encode(new("jobs", "queue-broker", "conflict")), DateTimeOffset.UtcNow, default));
        }
        await using (var verify = OutputContext(scenario.Connection))
        {
            var output = await verify.OperationOutputs.AsNoTracking().SingleAsync();
            Assert.Equal(expected, new(output.Contract, output.Key, output.Payload));
            Assert.Equal(QueueEvidenceProducerId.Value, output.OperationId);
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Empty(await verify.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
        }
        await using (var restart = Services(scenario.Connection, boundary))
        {
            var executor = restart.GetRequiredService<IDurableOperationExecutor>();
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await executor.ExecuteAsync(producer, default)).Outcome);
            Assert.Equal(expected, await restart.GetRequiredService<IExternalOperationOutputs>().ConsumeAsync(consumer, QueueEvidenceSelector, default));
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await executor.ExecuteAsync(consumer, default)).Outcome);
        }
        await using (var repeat = Services(scenario.Connection, boundary))
        {
            var executor = repeat.GetRequiredService<IDurableOperationExecutor>();
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await executor.ExecuteAsync(producer, default)).Outcome);
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await executor.ExecuteAsync(consumer, default)).Outcome);
            Assert.Equal(attempt, Assert.Single((await executor.ObserveAsync(producer, default))!.Attempts).AttemptId);
        }
        Assert.Equal(1, boundary.Calls); Assert.Equal(["create:accepted-1", "subscribe:accepted-1"], boundary.Effects);
        await using var final = OutputContext(scenario.Connection);
        Assert.Equal(1, (await final.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Equal(2, await final.DurableOperationAttempts.CountAsync());
        Assert.Equal(1, await final.OperationOutputs.CountAsync());
        Assert.Equal(scenario.Plan.Fingerprint, (await final.DeploymentGenerations.AsNoTracking().SingleAsync()).Fingerprint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Non_database_external_output_missing_or_ambiguous_publication_contract_cannot_acknowledge_or_redispatch(int registrations)
    {
        var boundary = new QueueBoundary { ExternalEvidence = true };
        var scenario = await GraphCase(boundary);
        boundary.OutputContractRegistrations = registrations;
        var producer = new DurableOperationKey(scenario.Commit.DeploymentId, scenario.Commit.Generation, QueueEvidenceProducerId);
        for (var restart = 0; restart < 2; restart++)
        {
            await using var act = Services(scenario.Connection, boundary);
            if (registrations == 2)
                Assert.Throws<InvalidOperationException>(() => act.GetRequiredService<IDurableOperationExecutor>());
            else
            {
                var executor = act.GetRequiredService<IDurableOperationExecutor>();
                Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await executor.ExecuteAsync(producer, default)).Outcome);
                Assert.Equal(DurableExecutionOutcome.Blocked, (await executor.ExecuteAsync(producer with { OperationId = QueueEvidenceConsumerId }, default)).Outcome);
            }
        }
        Assert.Equal(0, boundary.Calls); Assert.Empty(boundary.Effects);
        await using var verify = OutputContext(scenario.Connection);
        Assert.Empty(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Non_database_external_output_missing_or_ambiguous_consumption_contract_refuses_before_consumer_dispatch(int registrations)
    {
        var boundary = new QueueBoundary { ExternalEvidence = true };
        var scenario = await GraphCase(boundary);
        var producer = new DurableOperationKey(scenario.Commit.DeploymentId, scenario.Commit.Generation, QueueEvidenceProducerId);
        await using (var publish = Services(scenario.Connection, boundary))
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await publish.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(producer, default)).Outcome);
        boundary.OutputContractRegistrations = registrations;
        await using (var act = Services(scenario.Connection, boundary))
        {
            if (registrations == 2)
                Assert.Throws<InvalidOperationException>(() => act.GetRequiredService<IDurableOperationExecutor>());
            else
                Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await act.GetRequiredService<IDurableOperationExecutor>()
                    .ExecuteAsync(producer with { OperationId = QueueEvidenceConsumerId }, default)).Outcome);
        }
        Assert.Equal(["create:accepted-1"], boundary.Effects);
        await using var verify = OutputContext(scenario.Connection);
        Assert.Equal(1, await verify.OperationOutputs.CountAsync());
        Assert.Equal(1, await verify.DurableOperationAttempts.CountAsync());
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }
}
