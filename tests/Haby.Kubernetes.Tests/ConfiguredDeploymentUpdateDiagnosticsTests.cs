using Haby.Application;
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
    }
}
