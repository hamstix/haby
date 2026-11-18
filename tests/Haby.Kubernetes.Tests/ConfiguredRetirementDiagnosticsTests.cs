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

    [Theory]
    [InlineData("empty")]
    [InlineData("not-submitted")]
    [InlineData("expired")]
    [InlineData("invalid")]
    [InlineData("superseded")]
    [InlineData("refused")]
    [InlineData("unavailable")]
    public void Complete_returned_members_distinguish_nonready_branches_without_exposing_identity(string branch)
    {
        var observation = branch switch
        {
            "not-submitted" => new RolloutObservation(RolloutOutcome.NotSubmitted, "not-submitted"),
            "invalid" => new RolloutObservation(RolloutOutcome.Pending, "status-invalid"),
            "superseded" => new RolloutObservation(RolloutOutcome.Refused, "attempt-superseded"),
            "refused" => new RolloutObservation(RolloutOutcome.Refused, "runtime-authority-unavailable"),
            "unavailable" => new RolloutObservation(RolloutOutcome.Unavailable, "api-access-denied"),
            _ => new RolloutObservation(RolloutOutcome.Pending, "rollout-pending")
        };
        var id = Guid.NewGuid();
        var member = new RolloutMember("protected-subject", branch == "not-submitted" ? null : id, DateTimeOffset.MaxValue,
            branch == "expired" ? RolloutBudget.Expired : RolloutBudget.Active, observation with { Evidence = "protected-bootstrap" });
        var result = new RolloutResult(false, null, null, RolloutStop.Complete, branch == "empty" ? [] : [member], Replayed: true);
        var records = ConfiguredRetirementDiagnostics.Members(result);
        Assert.Equal("Q243 configured retirement WaitReturned false false false not-ready Complete", ConfiguredRetirementDiagnostics.Returned(result, 2));
        Console.WriteLine(ConfiguredRetirementDiagnostics.Returned(result, 2));
        Assert.Equal(branch == "empty" ? 1 : 2, records.Length);
        Assert.Equal("Q243 configured retirement WaitMembers " + (branch == "empty" ? "0 0 0" : branch == "not-submitted" ? "1 0 0" : branch == "expired" ? "1 1 1" : "1 1 0") + " true Unknown", records[0]);
        if (branch != "empty") Assert.Equal("Q243 configured retirement Member 0 " + observation.Outcome + " " + observation.Code + " " +
            (branch == "expired" ? "Expired" : "Active") + " " + (branch == "not-submitted" ? "false" : "true") + " false Unknown", records[1]);
        foreach (var record in records) Console.WriteLine(record);
        Assert.False(result.Ready); Assert.False(result.Applied);
        Assert.All(records, record => { Assert.DoesNotContain("protected", record); Assert.DoesNotContain(id.ToString(), record); Assert.True(record.Length <= 160); });
    }
    [Fact]
    public async Task Pending_then_Ready_diagnostics_preserve_the_same_attempt_deadline_and_result()
    {
        var attempt = Guid.NewGuid(); var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        var pending = new RolloutResult(false, null, null, RolloutStop.CallLimit,
            [new("protected", attempt, deadline, RolloutBudget.Active, new(RolloutOutcome.Pending, "rollout-pending"))]);
        var ready = pending with
        {
            Ready = true,
            Stop = RolloutStop.Complete,
            Members = [pending.Members[0] with { Observation = new(RolloutOutcome.Ready, "ready") }]
        };
        Assert.Same(pending, await ConfiguredRetirementDiagnostics.WaitAsync(() => ValueTask.FromResult(pending), 2));
        Assert.Same(ready, await ConfiguredRetirementDiagnostics.WaitAsync(() => ValueTask.FromResult(ready), 2));
        Assert.Equal(pending.Members[0].Attempt, ready.Members[0].Attempt); Assert.Equal(deadline, ready.Members[0].Deadline);
        Assert.Contains("Member 0 Pending rollout-pending Active true false Unknown", ConfiguredRetirementDiagnostics.Members(pending)[1]);
        Assert.Contains("Member 0 Ready ready Active true false Unknown", ConfiguredRetirementDiagnostics.Members(ready)[1]);
        var hostile = pending with
        {
            Members = [pending.Members[0] with { Budget = (RolloutBudget)99,
            Observation = new((RolloutOutcome)99, "protected-code", true) }]
        };
        Assert.EndsWith("Member 0 Unknown Unknown Unknown true true Unknown", ConfiguredRetirementDiagnostics.Members(hostile)[1]);
        foreach (var record in ConfiguredRetirementDiagnostics.Members(hostile)) Console.WriteLine(record);
        Console.WriteLine("Q243 configured retirement MembersOverflow");
        Assert.Equal(["Q243 configured retirement MembersOverflow"], ConfiguredRetirementDiagnostics.Members(pending with { Members = Enumerable.Repeat(pending.Members[0], 17).ToArray() }));
    }

}
