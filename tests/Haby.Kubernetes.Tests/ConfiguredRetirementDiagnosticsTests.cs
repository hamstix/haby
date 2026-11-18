using Haby.Application;
using Haby.Kubernetes.Contracts;
using Xunit;
namespace Haby.Kubernetes.Tests;

public sealed class ConfiguredRetirementDiagnosticsTests
{
    [Fact]
    public void Returned_and_thrown_wait_records_are_closed_and_do_not_publish_arbitrary_values()
    {
        var result = new RolloutResult(true, null, null, RolloutStop.Complete, [], AppliedGeneration: 2,
            CompletionReason: "prerequisite-incomplete");
        Assert.Equal("Q243 configured retirement WaitReturned true false true prerequisite-incomplete Complete", ConfiguredRetirementDiagnostics.Returned(result, 2));
        Assert.Equal("Q243 configured retirement WaitReturned true false false unknown Unknown", ConfiguredRetirementDiagnostics.Returned(result with { CompletionReason = "protected\nbootstrap", Stop = (RolloutStop)999 }, 3));
        Assert.Equal("Q243 configured retirement Closure Unknown Unknown", ConfiguredRetirementDiagnostics.Closure("protected", "secret"));
        Assert.Equal("Q243 configured retirement WaitThrew Data", ConfiguredRetirementDiagnostics.Failure("WaitThrew", new InvalidDataException("protected")));
        Assert.Equal("Q243 configured retirement NamespaceCleanup Canceled", ConfiguredRetirementDiagnostics.Failure("NamespaceCleanup", new OperationCanceledException("protected")));
        Assert.Equal("Q243 configured retirement OwnedFinalizer true", ConfiguredRetirementDiagnostics.Finalizer(true));
        var primary = new InvalidOperationException("protected-primary"); var cleanup = new IOException("protected-cleanup");
        var combined = ConfiguredRetirementDiagnostics.Combine(primary, cleanup);
        Assert.Same(primary, combined.InnerExceptions[0]); Assert.Same(cleanup, combined.InnerExceptions[1]);
    }
    [Fact]
    public async Task Wait_wrapper_preserves_returned_reference_and_original_exception()
    {
        var value = new RolloutResult(false, null, null, RolloutStop.Interrupted, []);
        Assert.Same(value, await ConfiguredRetirementDiagnostics.WaitAsync(() => ValueTask.FromResult(value), 2));
        var error = new IOException("protected-message");
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => ConfiguredRetirementDiagnostics.WaitAsync(() => ValueTask.FromException<RolloutResult>(error), 2).AsTask()));
    }
    [Fact]
    public void Recipient_counts_and_projection_codes_are_bounded_and_value_free()
    {
        Assert.Equal(RecipientProjectionFailure.RuntimeMount, DeploymentRecipientDiagnostics.Failure(new WorkloadContractException("runtime-mount-drift")));
        Assert.Equal(RecipientProjectionFailure.Unknown, DeploymentRecipientDiagnostics.Failure(new WorkloadContractException("runtime-mount-drift\nprotected")));
        Assert.Equal("Q243 configured retirement Recipients unknown unknown 0 Unknown None", ConfiguredRetirementDiagnostics.Recipients(new(-1, 16385, 0, (RecipientProjectionFailure)999, RecipientProjectionFailure.None)));
    }
    [Fact]
    public async Task Held_read_and_U09_diagnostics_preserve_results_exceptions_and_closed_booleans()
    {
        var pod = new k8s.Models.V1Pod();
        Assert.Same(pod, await ConfiguredRetirementDiagnostics.ReadHeldAsync(() => Task.FromResult(pod)));
        var error = new IOException("protected-message");
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => ConfiguredRetirementDiagnostics.ReadHeldAsync(() => Task.FromException<k8s.Models.V1Pod>(error))));
        foreach (var condition in new[] { "U09Ready", "U09NotApplied", "U09OldRecipients", "U09HeldUid", "U09Terminating" })
            Assert.Equal("Q243 configured retirement Condition " + condition + " false", ConfiguredRetirementDiagnostics.Condition(condition, false));
        Assert.Equal("Q243 configured retirement Condition Unknown true", ConfiguredRetirementDiagnostics.Condition("U09Terminating protected", true));
        Assert.Equal("Q243 configured retirement HeldReadThrew Canceled", ConfiguredRetirementDiagnostics.Failure("HeldReadThrew", new OperationCanceledException("protected")));
    }

}
