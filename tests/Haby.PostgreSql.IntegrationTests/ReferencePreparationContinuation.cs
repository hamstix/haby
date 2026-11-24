using Haby.Operator.Contracts;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

internal sealed record ReferencePreparationReplies(WorkloadSubmissionReply First, WorkloadSubmissionReply Final);

// This acceptance's two configured Workloads only; no production retry policy or new authority.
internal static class ReferencePreparationContinuation
{
    internal static async Task<ReferencePreparationReplies> RunAsync(GenerationRequest exact, DurableCommitSummary accepted,
        RevisionReference revision, long previousGeneration, Func<GenerationRequest, Task<WorkloadSubmissionReply>> submit,
        Func<GenerationRequest, Task<CommitReply>> inspect, Action<WorkloadSubmissionReply, int> observe)
    {
        var request = exact.Clone(); var target = accepted.Clone(); var reference = revision.Clone();
        Assert.Equal(target.DeploymentId, request.DeploymentId); Assert.Equal(target.Generation, request.Generation);
        Assert.False(string.IsNullOrWhiteSpace(target.TechnicalActorId));
        Assert.True(previousGeneration > 0 && previousGeneration < target.Generation);
        var first = (await submit(request.Clone())).Clone();
        observe(first.Clone(), 1);
        if (first.Stage == "Deployment")
        {
            RequireCompletedBoundary(first, target, previousGeneration);
            return new(first, first.Clone());
        }
        Assert.Equal("cancelled", first.Outcome); Assert.Equal("cancelled", first.Reason);
        Assert.True(first.RequiresInspection);
        Assert.Contains(first.Stage, new[] { "Documents", "Identity", "Publication" });
        var current = (await inspect(request.Clone())).Clone();
        RequireCompatiblePrefix(current, target, reference, previousGeneration);
        // #229: inspect exact progress, then one explicit same-generation continuation; Submit revalidates current rights.
        // Never a loop or a new acceptance.
        var second = (await submit(request.Clone())).Clone();
        observe(second.Clone(), 2);
        RequireCompletedBoundary(second, target, previousGeneration);
        return new(first, second);
    }

    private static void RequireCompletedBoundary(WorkloadSubmissionReply reply, DurableCommitSummary accepted, long previous)
    {
        Assert.Equal("Deployment", reply.Stage); Assert.Contains(reply.Outcome, new[] { "stopped", "cancelled" });
        var progress = Assert.IsType<WorkloadSubmissionProgress>(reply.Progress);
        Assert.Equal(accepted.DeploymentId, progress.DeploymentId); Assert.Equal(accepted.Generation, progress.Generation);
        Assert.Equal(accepted.Generation, progress.DesiredGeneration);
        Assert.True(progress.HasAppliedGeneration); Assert.Equal(previous, progress.AppliedGeneration);
        Assert.True(progress.HasResourcesReadyId && Guid.TryParse(progress.ResourcesReadyId, out var ready) && ready != Guid.Empty);
        Assert.Equal("partial", progress.Submission); Assert.False(progress.HasSubmittedId);
        Assert.Equal(2, progress.Total); Assert.Equal(0, progress.Completed);
        Assert.Equal(new[] { "api/main", "worker/main" }, progress.Members.Select(x => x.Component + "/" + x.Workload).Order(StringComparer.Ordinal).ToArray());
        Assert.All(progress.Members, member =>
        {
            Assert.Equal("prepared", member.Documents); Assert.Equal("confirmed", member.Identity);
            Assert.Equal("published", member.Publication); Assert.Equal("available", member.RuntimeAuthority);
            Assert.Contains(member.Submission, new[] { "notAttempted", "pending", "uncertain" });
        });
    }

    private static void RequireCompatiblePrefix(CommitReply current, DurableCommitSummary accepted, RevisionReference revision, long previous)
    {
        Assert.Equal(CommitStatus.Found, current.Status);
        var desired = Assert.IsType<DesiredGenerationSummary>(current.DesiredGeneration);
        Assert.Equal(accepted.DeploymentId, desired.DeploymentId); Assert.Equal(accepted.Generation, desired.Generation);
        Assert.Equal(accepted.ChangeSetId, desired.ChangeSetId); Assert.Equal(accepted.PlanId, desired.PlanId);
        Assert.Equal(accepted.TechnicalActorId, desired.TechnicalActorId); Assert.Equal("pilot", desired.EnvironmentId);
        Assert.Equal(revision, desired.Reference);
        var description = Assert.IsType<WorkloadReview>(desired.WorkloadDescription);
        Assert.Equal("available", description.Status);
        var expected = new[] { "api/main", "worker/main" };
        Assert.Equal(expected, description.Items.Select(x => x.ComponentId + "/" + x.WorkloadId).Order(StringComparer.Ordinal).ToArray());
        Assert.All(description.Items, item => Assert.Equal(new[] { "settings" }, item.Documents.ToArray()));
        var resources = Assert.IsType<GenerationProgress>(current.Progress);
        Assert.Equal(accepted.DeploymentId, resources.DeploymentId); Assert.Equal(accepted.Generation, resources.Generation);
        Assert.Equal(accepted.Generation, resources.DesiredGeneration); Assert.False(resources.HasSupersededByGeneration);
        Assert.True(resources.HasAppliedGeneration); Assert.Equal(previous, resources.AppliedGeneration);
        Assert.Equal(resources.Total, resources.Completed); Assert.False(resources.HasStoppedOperationId);
        Assert.Equal(2, resources.ReusedResources); Assert.Equal(0, resources.RetainedResources); Assert.Equal(0, resources.ReintroducedResources);
        var ready = Assert.IsType<ResourceStageCheckpointSummary>(resources.ResourcesReady);
        Assert.True(Guid.TryParse(ready.CheckpointId, out var checkpoint) && checkpoint != Guid.Empty);
        Assert.Equal(Haby.Application.ResourcesReadyCheckpoint.FormatVersion, ready.FormatVersion);
        Assert.False(string.IsNullOrWhiteSpace(ready.InputFingerprint));
        Assert.Equal(2, ready.ActiveResources); Assert.Equal(0, ready.RetainedResources); Assert.Equal(2, ready.OriginalResults);
        var progress = Assert.IsType<WorkloadSubmissionProgress>(current.WorkloadSubmission);
        Assert.Equal(accepted.DeploymentId, progress.DeploymentId); Assert.Equal(accepted.Generation, progress.Generation);
        Assert.Equal(accepted.Generation, progress.DesiredGeneration);
        Assert.True(progress.HasAppliedGeneration); Assert.Equal(previous, progress.AppliedGeneration);
        Assert.True(progress.HasResourcesReadyId); Assert.Equal(ready.CheckpointId, progress.ResourcesReadyId);
        Assert.False(progress.HasSubmittedId); Assert.Contains(progress.Submission, new[] { "partial", "notSubmitted" });
        Assert.Equal(2, progress.Total); Assert.Equal(0, progress.Completed);
        Assert.Equal(expected, progress.Members.Select(x => x.Component + "/" + x.Workload).Order(StringComparer.Ordinal).ToArray());
        Assert.All(progress.Members, member =>
        {
            Assert.Equal("notAttempted", member.Submission); Assert.False(member.HasOperationId);
            // With exactly one selected document per Workload, partial/uncertain/revoked is not a compatible prefix.
            // Confirmed identity + unpublished permission may be unavailable; this is not generic unavailable authority.
            var compatible = (member.Documents, member.Identity, member.Publication, member.RuntimeAuthority) is
                ("notAttempted", "notAttempted", "notAttempted", "notApplicable") or
                ("prepared", "notAttempted", "notAttempted", "notApplicable") or
                ("prepared", "confirmed", "notAttempted", "unavailable") or
                ("prepared", "confirmed", "published", "available");
            Assert.True(compatible, "Inspected preparation state lacks compatible current-authority proof.");
        });
    }
}
