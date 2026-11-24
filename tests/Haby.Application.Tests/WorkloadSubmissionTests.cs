using Haby.Application;
using Xunit;

namespace Haby.Application.Tests;

public sealed class WorkloadSubmissionTests
{
    [Theory]
    [InlineData("Inspection")]
    [InlineData("Recovery")]
    [InlineData("Documents")]
    [InlineData("Identity")]
    [InlineData("Publication")]
    [InlineData("Deployment")]
    public async Task Stops_before_any_later_stage_and_preserves_exact_failed_subject(string failure)
    {
        var steps = new Steps(failure);
        var result = await new SubmitWorkloadGeneration(steps).ExecuteAsync(default);
        Assert.Equal("stopped", result.Outcome);
        Assert.Equal(failure, result.Stage);
        Assert.Equal("api/main", result.Subject);
        Assert.Equal(failure, steps.Calls.Last());
    }
    [Fact]
    public async Task Complete_historical_submission_skips_all_new_preparation_even_if_current_rights_are_revoked()
    {
        var steps = new Steps(null, true);
        var result = await new SubmitWorkloadGeneration(steps).ExecuteAsync(default);
        Assert.Equal("submitted", result.Outcome);
        Assert.Equal(new[] { "Inspection", "Recovery" }, steps.Calls);
        Assert.Equal("revoked", result.Progress!.Members.Single().RuntimeAuthority);
        Assert.Null(result.Progress.AppliedGeneration);
    }
    [Fact]
    public async Task Cancellation_starts_no_later_stage_and_does_not_claim_rollback()
    {
        using var cancellation = new CancellationTokenSource();
        var steps = new Steps(null, cancel: cancellation);
        var result = await new SubmitWorkloadGeneration(steps).ExecuteAsync(cancellation.Token);
        Assert.Equal("cancelled", result.Outcome);
        Assert.True(result.RequiresInspection);
        Assert.Equal("Documents", steps.Calls.Last());
    }
    [Fact]
    public async Task Unknown_saved_proof_stops_before_any_new_preparation_after_dispatched_recovery()
    {
        var steps = new Steps(null, submission: "unknown");
        var result = await new SubmitWorkloadGeneration(steps).ExecuteAsync(default);
        Assert.Equal(new[] { "Inspection", "Recovery" }, steps.Calls);
        Assert.Equal("stopped", result.Outcome);
        Assert.Equal("unknown", result.Progress!.Submission);
        Assert.True(result.RequiresInspection);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_at_Identity_Publication_boundary_never_starts_Deployment(bool insidePublication)
    {
        using var cancellation = new CancellationTokenSource();
        var steps = new PublicationBoundarySteps(cancellation, insidePublication);
        var result = await new SubmitWorkloadGeneration(steps).ExecuteAsync(cancellation.Token);
        Assert.Equal("cancelled", result.Outcome); Assert.Equal("Publication", result.Stage);
        Assert.True(result.RequiresInspection);
        Assert.DoesNotContain("Deployment", steps.Calls);
        Assert.Equal(insidePublication, steps.Calls.Contains("Publication"));
    }
    [Fact]
    public async Task Completed_preparation_reaches_deliberate_Deployment_boundary()
    {
        var steps = new PublicationBoundarySteps(null, false);
        var result = await new SubmitWorkloadGeneration(steps).ExecuteAsync(default);
        Assert.Equal("stopped", result.Outcome); Assert.Equal("Deployment", result.Stage);
        Assert.Equal(new[] { "Inspection", "Recovery", "Documents", "Identity", "Publication", "Deployment" }, steps.Calls);
    }
    private sealed class PublicationBoundarySteps(CancellationTokenSource? cancellation, bool insidePublication) : IWorkloadSubmissionSteps
    {
        public List<string> Calls { get; } = [];
        public ValueTask<WorkloadSubmissionStep> InspectAsync(CancellationToken token) { Calls.Add("Inspection"); return ValueTask.FromResult(new WorkloadSubmissionStep()); }
        public ValueTask<WorkloadSubmissionStep> RecoverAsync(CancellationToken token) { Calls.Add("Recovery"); return ValueTask.FromResult(new WorkloadSubmissionStep()); }
        public ValueTask<WorkloadSubmissionStep> PrepareAsync(WorkloadSubmissionStage stage, CancellationToken token)
        {
            Calls.Add(stage.ToString());
            if (cancellation is not null && stage == (insidePublication ? WorkloadSubmissionStage.Publication : WorkloadSubmissionStage.Identity))
            { cancellation.Cancel(); if (insidePublication) token.ThrowIfCancellationRequested(); }
            return ValueTask.FromResult(stage == WorkloadSubmissionStage.Deployment ? new WorkloadSubmissionStep("deliberate-boundary") : new());
        }
        public ValueTask<WorkloadSubmissionProgress> ReadAsync(CancellationToken token) => ValueTask.FromResult(new WorkloadSubmissionProgress(
            "deployment", 2, 2, 1, "partial", Guid.NewGuid(), null, []));
    }
    private sealed class Steps(string? failure, bool complete = false, CancellationTokenSource? cancel = null, string? submission = null) : IWorkloadSubmissionSteps
    {
        public List<string> Calls { get; } = [];
        private ValueTask<WorkloadSubmissionStep> Call(string stage)
        {
            Calls.Add(stage);
            if (stage == "Documents") cancel?.Cancel();
            return ValueTask.FromResult(stage == failure ? new WorkloadSubmissionStep("blocked", "api/main") : new());
        }
        public ValueTask<WorkloadSubmissionStep> InspectAsync(CancellationToken token) => Call("Inspection");
        public ValueTask<WorkloadSubmissionStep> RecoverAsync(CancellationToken token) => Call("Recovery");
        public ValueTask<WorkloadSubmissionStep> PrepareAsync(WorkloadSubmissionStage stage, CancellationToken token) => Call(stage.ToString());
        public ValueTask<WorkloadSubmissionProgress> ReadAsync(CancellationToken token) => ValueTask.FromResult(new WorkloadSubmissionProgress(
            Guid.NewGuid().ToString(), 1, 1, null, submission ?? (complete ? "submitted" : "partial"), null, complete ? Guid.NewGuid() : null,
            [new("api", "main", "prepared", "confirmed", "published", complete ? "confirmed" : "notAttempted", "revoked")]));
    }
}
