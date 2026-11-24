using Haby.Operator.Contracts;
using Haby.Application;
using WorkloadSubmissionProgress = Haby.Operator.Contracts.WorkloadSubmissionProgress;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

internal sealed record ReferencePreparationReplies(WorkloadSubmissionReply First, WorkloadSubmissionReply Final);

// This acceptance's two configured Workloads only; no production retry policy or new authority.
internal static class ReferencePreparationContinuation
{
    internal static async Task<ReferencePreparationReplies> RunAsync(GenerationRequest exact, DurableCommitSummary accepted,
        RevisionReference revision, long previousGeneration, Func<GenerationRequest, Task<WorkloadSubmissionReply>> submit,
        Func<GenerationRequest, Task<CommitReply>> inspect, Action<WorkloadSubmissionReply, int> observe, PublicReferenceFailureDiagnostics? diagnostic = null,
        Func<GenerationRequest, Task<ReferencePreparationCatalogProof>>? readResources = null)
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
        diagnostic?.Predicate(ReferencePreparationPredicate.CatalogProofPresent, flag: readResources is not null);
        Assert.NotNull(readResources);
        var catalog = await readResources(request.Clone());
        RequireCompatiblePrefix(current, target, reference, previousGeneration, diagnostic, catalog);
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

    private static void RequireCompatiblePrefix(CommitReply current, DurableCommitSummary accepted, RevisionReference revision, long previous, PublicReferenceFailureDiagnostics? diagnostic, ReferencePreparationCatalogProof catalog)
    {
        void At(ReferencePreparationPredicate predicate, string? status = null, int actual = -1, int expected = -1, bool? flag = null) =>
            diagnostic?.Predicate(predicate, status, actual, expected, flag);
        At(ReferencePreparationPredicate.Found, current.Status == CommitStatus.Found ? "found" : "other");
        Assert.Equal(CommitStatus.Found, current.Status);
        At(ReferencePreparationPredicate.DesiredPresent, flag: current.DesiredGeneration is not null);
        var desired = Assert.IsType<DesiredGenerationSummary>(current.DesiredGeneration);
        At(ReferencePreparationPredicate.DesiredDeployment, flag: accepted.DeploymentId == desired.DeploymentId);
        Assert.Equal(accepted.DeploymentId, desired.DeploymentId);
        At(ReferencePreparationPredicate.DesiredGeneration, flag: accepted.Generation == desired.Generation);
        Assert.Equal(accepted.Generation, desired.Generation);
        At(ReferencePreparationPredicate.DesiredChangeSet, flag: accepted.ChangeSetId == desired.ChangeSetId);
        Assert.Equal(accepted.ChangeSetId, desired.ChangeSetId);
        At(ReferencePreparationPredicate.DesiredPlan, flag: accepted.PlanId == desired.PlanId);
        Assert.Equal(accepted.PlanId, desired.PlanId);
        At(ReferencePreparationPredicate.DesiredActor, flag: accepted.TechnicalActorId == desired.TechnicalActorId);
        Assert.Equal(accepted.TechnicalActorId, desired.TechnicalActorId);
        At(ReferencePreparationPredicate.DesiredEnvironment, flag: desired.EnvironmentId == "pilot");
        Assert.Equal("pilot", desired.EnvironmentId);
        At(ReferencePreparationPredicate.DesiredRevision, flag: revision.Equals(desired.Reference));
        Assert.Equal(revision, desired.Reference);
        At(ReferencePreparationPredicate.DescriptionPresent, flag: desired.WorkloadDescription is not null);
        var description = Assert.IsType<WorkloadReview>(desired.WorkloadDescription);
        At(ReferencePreparationPredicate.DescriptionAvailable, description.Status);
        Assert.Equal("available", description.Status);
        var expected = new[] { "api/main", "worker/main" };
        At(ReferencePreparationPredicate.DescriptionMembers, actual: description.Items.Count, expected: 2);
        Assert.Equal(expected, description.Items.Select(x => x.ComponentId + "/" + x.WorkloadId).Order(StringComparer.Ordinal).ToArray());
        foreach (var item in description.Items)
        {
            At(ReferencePreparationPredicate.DescriptionDocuments, actual: item.Documents.Count, expected: 1);
            Assert.Equal(new[] { "settings" }, item.Documents.ToArray());
        }
        At(ReferencePreparationPredicate.ResourceDeployment, flag: accepted.DeploymentId == catalog.Deployment.ToString());
        Assert.Equal(accepted.DeploymentId, catalog.Deployment.ToString());
        At(ReferencePreparationPredicate.ResourceGeneration, flag: accepted.Generation == catalog.Generation);
        Assert.Equal(accepted.Generation, catalog.Generation);
        At(ReferencePreparationPredicate.ResourceDesired, flag: accepted.Generation == catalog.Desired);
        Assert.Equal(accepted.Generation, catalog.Desired);
        At(ReferencePreparationPredicate.ResourceNotSuperseded, flag: catalog.Superseded);
        Assert.False(catalog.Superseded);
        At(ReferencePreparationPredicate.ResourceAppliedPresent, flag: catalog.Applied.HasValue);
        Assert.True(catalog.Applied.HasValue);
        At(ReferencePreparationPredicate.ResourceApplied, flag: previous == catalog.Applied);
        Assert.Equal(previous, catalog.Applied);
        // Target resource graph has no new operations; read-only codec validation establishes completion of reused originals.
        At(ReferencePreparationPredicate.ResourceReused, actual: catalog.Reused.Count, expected: 2);
        Assert.Equal(2, catalog.Reused.Count);
        At(ReferencePreparationPredicate.ResourceRetained, actual: catalog.Retained.Count, expected: 0);
        Assert.Empty(catalog.Retained); Assert.Empty(desired.RetainedResources);
        At(ReferencePreparationPredicate.ResourceReintroduced, actual: catalog.Reused.Count(x => x.Reintroduced), expected: 0);
        Assert.All(catalog.Reused, x => Assert.False(x.Reintroduced));
        At(ReferencePreparationPredicate.ResourceReferenceIdentities);
        Assert.Equal(new[] { "left", "right" }, catalog.ReferenceResources);
        Assert.Equal(catalog.ReferenceResources, catalog.Reused.Select(x => x.Resource.Value).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(desired.Operations);
        At(ReferencePreparationPredicate.ResourceReuseMatches);
        Assert.Equal(catalog.Reused.Select(x => (x.Resource.Value, x.ManagementId.ToString(), x.SourceGeneration, x.Reintroduced)).OrderBy(x => x.Value, StringComparer.Ordinal).ToArray(),
            desired.ReusedResources.Select(x => (x.ResourceId, x.ManagementId, x.SourceGeneration, x.Reintroduced)).OrderBy(x => x.ResourceId, StringComparer.Ordinal).ToArray());
        Assert.All(catalog.Reused, x => { Assert.NotEqual(Guid.Empty, x.ManagementId); Assert.Equal(previous, x.SourceGeneration); });
        var ready = catalog.Ready;
        At(ReferencePreparationPredicate.ReadyIdentity, flag: ready.Id != Guid.Empty);
        Assert.NotEqual(Guid.Empty, ready.Id);
        At(ReferencePreparationPredicate.ReadyTarget, flag: ready.DeploymentId == catalog.Deployment && ready.Generation == catalog.Generation);
        Assert.Equal(catalog.Deployment, ready.DeploymentId); Assert.Equal(catalog.Generation, ready.Generation);
        At(ReferencePreparationPredicate.ReadyVersion, actual: catalog.FormatVersion, expected: ResourcesReadyCheckpoint.FormatVersion);
        Assert.Equal(ResourcesReadyCheckpoint.FormatVersion, catalog.FormatVersion);
        At(ReferencePreparationPredicate.ReadyFingerprint, flag: !string.IsNullOrWhiteSpace(catalog.Fingerprint) && ready.InputFingerprint == catalog.Fingerprint);
        Assert.False(string.IsNullOrWhiteSpace(catalog.Fingerprint)); Assert.Equal(catalog.Fingerprint, ready.InputFingerprint);
        At(ReferencePreparationPredicate.ReadyActive, actual: ready.Resources.Count, expected: 2);
        Assert.Equal(2, ready.Resources.Count); Assert.All(ready.Resources, x => { Assert.False(x.Retained); Assert.False(x.Reintroduced); });
        At(ReferencePreparationPredicate.ReadyOriginalResults, actual: ready.Resources.Sum(x => x.Operations.Count), expected: catalog.OriginalResources.Sum(x => x.Operations.Count));
        // Compare exact resource/source/management/fingerprint and every original operation/attempt/output reference, not Resource count.
        var original = new ResourcesReadyCheckpoint(ready.Id, ready.DeploymentId, ready.Generation, ready.InputFingerprint, ready.ConfirmedAt, catalog.OriginalResources);
        Assert.True(ResourcesReadyCheckpointCodec.Encode(original) == ResourcesReadyCheckpointCodec.Encode(ready), "Target checkpoint differs from original confirmed Resource results.");
        Assert.Equal(catalog.Reused.Select(x => (x.Resource.Value, x.ManagementId, x.SourceGeneration)).OrderBy(x => x.Value, StringComparer.Ordinal).ToArray(),
            ready.Resources.Select(x => (x.Resource, x.ManagementId, x.SourceGeneration)).OrderBy(x => x.Resource, StringComparer.Ordinal).ToArray());
        At(ReferencePreparationPredicate.SubmissionPresent, flag: current.WorkloadSubmission is not null);
        var progress = Assert.IsType<WorkloadSubmissionProgress>(current.WorkloadSubmission);
        At(ReferencePreparationPredicate.SubmissionDeployment, flag: accepted.DeploymentId == progress.DeploymentId);
        Assert.Equal(accepted.DeploymentId, progress.DeploymentId);
        At(ReferencePreparationPredicate.SubmissionGeneration, flag: accepted.Generation == progress.Generation);
        Assert.Equal(accepted.Generation, progress.Generation);
        At(ReferencePreparationPredicate.SubmissionDesired, flag: accepted.Generation == progress.DesiredGeneration);
        Assert.Equal(accepted.Generation, progress.DesiredGeneration);
        At(ReferencePreparationPredicate.SubmissionAppliedPresent, flag: progress.HasAppliedGeneration);
        Assert.True(progress.HasAppliedGeneration);
        At(ReferencePreparationPredicate.SubmissionApplied, flag: previous == progress.AppliedGeneration);
        Assert.Equal(previous, progress.AppliedGeneration);
        At(ReferencePreparationPredicate.SubmissionReadyPresent, flag: progress.HasResourcesReadyId);
        Assert.True(progress.HasResourcesReadyId);
        At(ReferencePreparationPredicate.SubmissionReadyMatches, flag: ready.Id.ToString() == progress.ResourcesReadyId);
        Assert.Equal(ready.Id.ToString(), progress.ResourcesReadyId);
        At(ReferencePreparationPredicate.SubmissionNotSubmitted, flag: progress.HasSubmittedId);
        Assert.False(progress.HasSubmittedId);
        At(ReferencePreparationPredicate.SubmissionState, progress.Submission);
        Assert.Contains(progress.Submission, new[] { "partial", "notSubmitted" });
        At(ReferencePreparationPredicate.SubmissionTotal, actual: progress.Total, expected: 2);
        Assert.Equal(2, progress.Total);
        At(ReferencePreparationPredicate.SubmissionCompleted, actual: progress.Completed, expected: 0);
        Assert.Equal(0, progress.Completed);
        At(ReferencePreparationPredicate.SubmissionMembers, actual: progress.Members.Count, expected: 2);
        Assert.Equal(expected, progress.Members.Select(x => x.Component + "/" + x.Workload).Order(StringComparer.Ordinal).ToArray());
        foreach (var member in progress.Members)
        {
            At(ReferencePreparationPredicate.MemberNotAttempted, member.Submission);
            Assert.Equal("notAttempted", member.Submission);
            At(ReferencePreparationPredicate.MemberNoOperation, flag: member.HasOperationId);
            Assert.False(member.HasOperationId);
            // With exactly one selected document per Workload, partial/uncertain/revoked is not a compatible prefix.
            // Selected documents without a binding/identity intent project unavailable, not notApplicable.
            // Each unavailable case remains tied to the exact proof and known identity/publication state.
            var compatible = (member.Documents, member.Identity, member.Publication, member.RuntimeAuthority) is
                ("notAttempted", "notAttempted", "notAttempted", "unavailable") or
                ("prepared", "notAttempted", "notAttempted", "unavailable") or
                ("prepared", "confirmed", "notAttempted", "unavailable") or
                ("prepared", "confirmed", "published", "available");
            At(ReferencePreparationPredicate.MemberDocuments, member.Documents);
            At(ReferencePreparationPredicate.MemberIdentity, member.Identity);
            At(ReferencePreparationPredicate.MemberPublication, member.Publication);
            At(ReferencePreparationPredicate.MemberAuthority, member.RuntimeAuthority);
            At(ReferencePreparationPredicate.MemberCompatible, flag: compatible);
            Assert.True(compatible, "Inspected preparation state lacks compatible current-authority proof.");
        }
    }
}
