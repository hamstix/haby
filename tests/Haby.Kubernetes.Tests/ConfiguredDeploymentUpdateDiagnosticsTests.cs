using Haby.Application;
using Haby.Kubernetes.Contracts;
using Xunit;
using Stage = ConfiguredDeploymentUpdateDiagnostics.Stage;
using State = ConfiguredDeploymentUpdateDiagnostics.State;

namespace Haby.Kubernetes.Tests;

public sealed class ConfiguredDeploymentUpdateDiagnosticsTests
{
    [Fact]
    public void Records_distinguish_expected_loss_normal_return_and_unknown_failure_without_payloads()
    {
        Assert.Equal("Q243 configured update Execute Returned", ConfiguredDeploymentUpdateDiagnostics.Progress(Stage.Execute, State.Returned));
        Assert.Equal("Q243 configured update ExpectedLoss Observed", ConfiguredDeploymentUpdateDiagnostics.Progress(Stage.ExpectedLoss, State.Observed));
        Assert.Equal("Q243 configured update Recover failure unknown 1", ConfiguredDeploymentUpdateDiagnostics.Failure(Stage.Recover, 1));
        Assert.Equal("Q243 configured update Execute failure unknown unknown", ConfiguredDeploymentUpdateDiagnostics.Failure(Stage.Execute, 4));
        Assert.Equal("Q243 configured update Execute failure unknown unknown", ConfiguredDeploymentUpdateDiagnostics.Failure(Stage.Execute, -1));
        Assert.Equal("Q243 configured update Execute result Refused unknown 0", ConfiguredDeploymentUpdateDiagnostics.Result(Stage.Execute,
            new(InternalActionOutcome.Refused, "protected-code\nQ243 configured update Execute Completed", "protected-bootstrap-and-response-body"), 0));
        Assert.Equal("Q243 configured update Recover result Confirmed confirmed 1", ConfiguredDeploymentUpdateDiagnostics.Result(Stage.Recover,
            new(InternalActionOutcome.Confirmed, "confirmed", "protected-configuration-and-credentials"), 1));
        Assert.Equal("Q243 configured update Execute result unknown unknown unknown", ConfiguredDeploymentUpdateDiagnostics.Result(Stage.Execute,
            new((InternalActionOutcome)100, "protected-exception-message", "protected-response"), 100));
        Assert.Equal("Q243 configured update DelegateExecute result Refused unknown 1 receipt-present false", ConfiguredDeploymentUpdateDiagnostics.Delegated(
            Stage.DelegateExecute, new(InternalActionOutcome.Refused, "protected-response-body"), 1));
        Assert.Equal("Q243 configured update DelegateObserve result Confirmed observation-confirmed 1 receipt-present true", ConfiguredDeploymentUpdateDiagnostics.Delegated(
            Stage.DelegateObserve, new(InternalActionOutcome.Confirmed, "observation-confirmed", "protected-bootstrap"), 1));
        Assert.Equal("Q243 configured update wire status 422 1 true", ConfiguredDeploymentUpdateDiagnostics.Wire(422, 1));
        Assert.Equal("Q243 configured update wire status no-response 1 false", ConfiguredDeploymentUpdateDiagnostics.Wire(null, 1));
        Assert.Equal("Q243 configured update wire status unknown unknown true", ConfiguredDeploymentUpdateDiagnostics.Wire(1000, 100));
        Assert.Equal("Q243 configured update proof Receipt failed unknown", ConfiguredDeploymentUpdateDiagnostics.Proof(
            ConfiguredDeploymentUpdateDiagnostics.Predicate.Receipt, false, new("protected-code\nprotected-bootstrap")));
    }
    [Fact]
    public void Existing_validation_helpers_identify_failed_proofs_without_emitting_observed_values()
    {
        var intent = KernelTests.Intent();
        var current = KernelTests.Object(intent);
        Assert.Equal("Q243 configured update proof Receipt passed confirmed", ConfiguredDeploymentUpdateDiagnostics.Inspect(
            ConfiguredDeploymentUpdateDiagnostics.Predicate.Receipt, () => DeploymentObjects.Receipt(current, intent, ConfirmationSource.Observation)));
        current["spec"]!["template"]!["spec"]!["containers"]![0]!["image"] = "protected-image-and-credentials";
        Assert.Equal("Q243 configured update proof Target failed container-drift", ConfiguredDeploymentUpdateDiagnostics.Inspect(
            ConfiguredDeploymentUpdateDiagnostics.Predicate.Target, () => DeploymentObjects.Matches(current, intent.Target)));
        current["metadata"]!["annotations"]![DeploymentIntents.OperationKey] = "protected-marker";
        Assert.Equal("Q243 configured update proof Marker failed operation-marker", ConfiguredDeploymentUpdateDiagnostics.Inspect(
            ConfiguredDeploymentUpdateDiagnostics.Predicate.Marker, () => DeploymentIntents.Need(DeploymentObjects.HasMarkers(current, intent), "operation-marker")));
        Assert.Equal("Q243 configured update proof Readback failed unknown", ConfiguredDeploymentUpdateDiagnostics.Inspect(
            ConfiguredDeploymentUpdateDiagnostics.Predicate.Readback, () => throw new IOException("protected-response-body")));
    }
    [Fact]
    public async Task Delegated_result_is_captured_before_caller_loss_without_changing_result_or_exception()
    {
        var events = new List<string>();
        var expected = new InternalActionResult(InternalActionOutcome.Uncertain, "response-unconfirmed", "protected-receipt");
        var inner = new ReturningHandler(expected, events);
        var wrapper = new ConfiguredDeploymentUpdateHandler(inner, (stage, result) =>
        {
            Assert.Same(expected, result);
            events.Add(stage.ToString());
        });
        Assert.Same(inner.Contract, wrapper.Contract);
        Assert.True(wrapper.UpdatesObject);
        Assert.True(wrapper.RequiresRuntimePublication);
        Assert.Same(expected, await wrapper.ExecuteAsync(null!, "protected-intent", CancellationToken.None));
        events.Add("caller-loss");
        Assert.Equal(new[] { "execute", "DelegateExecute", "caller-loss" }, events);
        events.Clear();
        Assert.Same(expected, await wrapper.ObserveAsync(null!, "protected-intent", CancellationToken.None));
        Assert.Equal(new[] { "observe", "DelegateObserve" }, events);
        inner.Error = new InvalidOperationException("protected-exception-message");
        events.Clear();
        Assert.Same(inner.Error, await Assert.ThrowsAsync<InvalidOperationException>(() => wrapper.ExecuteAsync(null!, "protected-intent", CancellationToken.None).AsTask()));
        Assert.Equal(new[] { "execute" }, events);
    }
    private sealed class ReturningHandler(InternalActionResult result, List<string> events) : IInternalActionHandler
    {
        internal InvalidOperationException? Error;
        public InternalActionContract Contract { get; } = new("Workload", "fixture", "fixture", "fixture", "fixture", "fixture", "fixture");
        public bool ObservationRecovery => true;
        public bool UpdatesObject => true;
        public bool RequiresRuntimePublication => true;
        public string Prepare(InternalActionContext context) => "fixture";
        public bool Validate(InternalActionContext context, string payload) => true;
        public bool ValidateReceipt(InternalActionContext context, string payload, string receipt, bool observation) => true;
        public ValueTask<InternalActionResult> ExecuteAsync(InternalActionContext context, string payload, CancellationToken token)
        { events.Add("execute"); if (Error is not null) throw Error; return ValueTask.FromResult(result); }
        public ValueTask<InternalActionResult> ObserveAsync(InternalActionContext context, string payload, CancellationToken token)
        { events.Add("observe"); return ValueTask.FromResult(result); }
    }
}
