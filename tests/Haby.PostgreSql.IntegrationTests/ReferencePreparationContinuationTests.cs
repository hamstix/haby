using Haby.Operator.Contracts;
using Haby.Application;
using WorkloadSubmissionMember = Haby.Operator.Contracts.WorkloadSubmissionMember;
using Haby.Domain;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class HostFailureDiagnosticsTests
{
    [Fact]
    public async Task Reference_preparation_continuation_inspects_once_preserves_first_and_exact_generation()
    {
        foreach (var prefix in new[] { "before-documents", "mixed-documents", "after-documents", "mixed-identities", "confirmed-identities" })
        {
            var stage = prefix is "before-documents" or "mixed-documents" ? "Documents" : prefix == "confirmed-identities" ? "Publication" : "Identity";
            var accepted = ContinuationCommit(); var revision = ContinuationRevision();
            var request = new GenerationRequest { DeploymentId = accepted.DeploymentId, Generation = accepted.Generation };
            var current = ContinuationProgress(accepted, revision);
            foreach (var member in current.WorkloadSubmission.Members)
            {
                if (prefix != "confirmed-identities" && !(prefix == "mixed-identities" && member.Component == "api"))
                    member.Identity = "notAttempted";
                if (prefix == "before-documents" || prefix == "mixed-documents" && member.Component == "worker")
                    member.Documents = "notAttempted";
                // Real configured-Workload projection: selected settings + no binding/identity intent => unavailable.
                member.RuntimeAuthority = "unavailable";
            }
            if (prefix == "before-documents") current.WorkloadSubmission.Submission = "notSubmitted";
            var firstWrites = prefix == "confirmed-identities" ? 2 : prefix == "mixed-identities" ? 1 : 0;
            var first = new WorkloadSubmissionReply { Outcome = "cancelled", Stage = stage, Reason = "cancelled", RequiresInspection = true, Progress = current.WorkloadSubmission.Clone() };
            var sequence = new List<string>(); var identities = new List<GenerationRequest>(); var observations = new List<WorkloadSubmissionReply>();
            var evidence = new List<string>(); var diagnostic = new PublicReferenceFailureDiagnostics(evidence.Add);
            var replies = await ReferencePreparationContinuation.RunAsync(request, accepted, revision, 1,
                input =>
                {
                    identities.Add(input.Clone()); sequence.Add("submit:same-principal");
                    input.Generation = 999; // Delegates cannot rewrite the immutable request used for continuation.
                    return Task.FromResult(identities.Count == 1 ? first : ContinuationCompleted(accepted, revision));
                }, input =>
                {
                    identities.Add(input.Clone()); sequence.Add("inspect:same-principal");
                    first.Outcome = "unavailable"; first.Stage = "unknown"; // First snapshot must remain independent.
                    first.Progress.Members[0].Documents = "unknown";
                    return Task.FromResult(current);
                }, (reply, attempt) =>
                {
                    observations.Add(reply.Clone()); sequence.Add("observe:" + attempt);
                    diagnostic.At(attempt == 1 ? PublicReferenceCheckpoint.PreparationFirstResult : PublicReferenceCheckpoint.PreparationContinuationResult);
                    diagnostic.Observe(reply, attempt == 1 ? firstWrites : 2);
                }, readResources: _ => Task.FromResult(ContinuationCatalog(accepted)));
            Assert.Equal(new[] { "submit:same-principal", "observe:1", "inspect:same-principal", "submit:same-principal", "observe:2" }, sequence);
            Assert.Equal(3, identities.Count);
            Assert.All(identities, identity => Assert.Equal(request, identity));
            Assert.Equal("cancelled", replies.First.Outcome); Assert.Equal(stage, replies.First.Stage);
            Assert.Equal("cancelled", observations[0].Outcome); Assert.Equal(stage, observations[0].Stage);
            Assert.Equal(current.WorkloadSubmission.Members[0].Documents, replies.First.Progress.Members[0].Documents);
            Assert.Equal(current.WorkloadSubmission.Members[0].Documents, observations[0].Progress.Members[0].Documents);
            Assert.Equal("Deployment", replies.Final.Stage);
            Assert.Equal(2, evidence.Count);
            Assert.Contains(",PreparationFirstResult,None,Cancelled," + stage + ",", evidence[0]);
            Assert.Contains(",PreparationContinuationResult,None,Stopped,Deployment,", evidence[1]);
        }
        var direct = ContinuationCommit(); var observed = 0;
        var done = await ReferencePreparationContinuation.RunAsync(new() { DeploymentId = direct.DeploymentId, Generation = direct.Generation },
            direct, ContinuationRevision(), 1, _ => Task.FromResult(ContinuationCompleted(direct, ContinuationRevision())),
            _ => throw new InvalidOperationException("Inspection must not run after Deployment."), (_, _) => observed++);
        Assert.Equal(1, observed); Assert.Equal("Deployment", done.Final.Stage);
    }

    [Fact]
    public async Task Reference_preparation_continuation_refuses_unknown_authority_proof_and_second_partial_result()
    {
        var accepted = ContinuationCommit(); var revision = ContinuationRevision();
        async Task Verify(WorkloadSubmissionReply first, Action<CommitReply>? fault, WorkloadSubmissionReply? second, int expectedCalls, int expectedReads, Func<ReferencePreparationCatalogProof, ReferencePreparationCatalogProof>? proofFault = null)
        {
            var calls = 0; var reads = 0;
            var error = await Record.ExceptionAsync(() => ReferencePreparationContinuation.RunAsync(
                new() { DeploymentId = accepted.DeploymentId, Generation = accepted.Generation }, accepted, revision, 1,
                _ => { calls++; Assert.InRange(calls, 1, 2); return Task.FromResult(calls == 1 ? first : second ?? ContinuationCompleted(accepted, revision)); },
                _ => { reads++; Assert.Equal(1, reads); var progress = ContinuationProgress(accepted, revision); fault?.Invoke(progress); return Task.FromResult(progress); }, (_, _) => { }, readResources: _ => Task.FromResult(proofFault?.Invoke(ContinuationCatalog(accepted)) ?? ContinuationCatalog(accepted))));
            if (proofFault is null) Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(error);
            else Assert.True(error is Xunit.Sdk.XunitException or InvalidDataException);
            Assert.Equal(expectedCalls, calls); Assert.Equal(expectedReads, reads);
        }
        // A primary failure names the exact assertion and only closed status/count/boolean evidence.
        var predicateLines = new List<string>();
        var predicateDiagnostic = new PublicReferenceFailureDiagnostics(predicateLines.Add);
        predicateDiagnostic.At(PublicReferenceCheckpoint.PreparationInspection);
        var missingProgress = ContinuationProgress(accepted, revision); missingProgress.WorkloadSubmission.ResourcesReadyId = Guid.NewGuid().ToString();
        var predicateCalls = 0;
        var predicateError = await Record.ExceptionAsync(() => predicateDiagnostic.RunAsync(_ => ReferencePreparationContinuation.RunAsync(
            new() { DeploymentId = accepted.DeploymentId, Generation = accepted.Generation }, accepted, revision, 1,
            _ => { predicateCalls++; return Task.FromResult(new WorkloadSubmissionReply { Outcome = "cancelled", Stage = "Publication", Reason = "cancelled", RequiresInspection = true }); },
            _ => Task.FromResult(missingProgress), (_, _) => { }, predicateDiagnostic, _ => Task.FromResult(ContinuationCatalog(accepted)))));
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(predicateError);
        Assert.Equal(1, predicateCalls);
        Assert.EndsWith(",SubmissionReadyMatches,Missing,-1,-1,False", predicateLines.Last());
        Assert.Contains(",primary,", predicateLines.Last());
        foreach (var first in new[]
        {
            new WorkloadSubmissionReply { Outcome = "stopped", Stage = "Publication", Reason = "storage-unavailable" },
            new WorkloadSubmissionReply { Outcome = "unavailable", Stage = "Publication" },
            new WorkloadSubmissionReply { Outcome = "cancelled", Stage = "Recovery", Reason = "cancelled", RequiresInspection = true },
            new WorkloadSubmissionReply { Outcome = "cancelled", Stage = "unknown", Reason = "cancelled", RequiresInspection = true },
            new WorkloadSubmissionReply { Outcome = "cancelled", Stage = "Publication", Reason = "unrelated", RequiresInspection = true },
            new WorkloadSubmissionReply { Outcome = "cancelled", Stage = "Publication", Reason = "cancelled" }
        }) await Verify(first, null, null, 1, 0);
        var cancelled = new WorkloadSubmissionReply { Outcome = "cancelled", Stage = "Publication", Reason = "cancelled", RequiresInspection = true };
        Action<CommitReply>[] faults =
        [
            x => x.Status = CommitStatus.GenerationNotFound,
            x => x.DesiredGeneration.DeploymentId = Guid.NewGuid().ToString(),
            x => x.DesiredGeneration.Generation++,
            x => x.DesiredGeneration.PlanId = "foreign-plan",
            x => x.DesiredGeneration.ChangeSetId = "foreign-change",
            x => x.DesiredGeneration.Reference.RevisionNumber++,
            x => x.DesiredGeneration.TechnicalActorId = "different-principal",
            x => x.DesiredGeneration.WorkloadDescription.Status = "unavailable",
            x => x.DesiredGeneration.WorkloadDescription.Items[0].Documents.Clear(),
            x => x.WorkloadSubmission.DesiredGeneration++,
            x => x.WorkloadSubmission.AppliedGeneration = 2,
            x => x.WorkloadSubmission.Submission = "unknown",
            x => x.WorkloadSubmission.Members.RemoveAt(0),
            x => x.WorkloadSubmission.Members[1].Component = "api",
            x => x.WorkloadSubmission.Members[0].RuntimeAuthority = "revoked",
            x => x.WorkloadSubmission.Members[0].RuntimeAuthority = "unknown",
            x => x.WorkloadSubmission.Members[0].Identity = "uncertain",
            x => x.WorkloadSubmission.Members[0].Identity = "pending",
            x => { x.WorkloadSubmission.Members[0].Identity = "notAttempted"; x.WorkloadSubmission.Members[0].RuntimeAuthority = "notApplicable"; },
            x => { x.WorkloadSubmission.Members[0].Documents = "notAttempted"; x.WorkloadSubmission.Members[0].Identity = "notAttempted"; x.WorkloadSubmission.Members[0].RuntimeAuthority = "notApplicable"; },
            x => x.WorkloadSubmission.Members[0].Submission = "pending",
            x => x.WorkloadSubmission.Members[0].Publication = "partial",
            x => { x.WorkloadSubmission.Members[0].Publication = "published"; x.WorkloadSubmission.Members[0].RuntimeAuthority = "unavailable"; }
        ];
        foreach (var fault in faults) await Verify(cancelled, fault, null, 1, 1);
        foreach (var proofFault in new Func<ReferencePreparationCatalogProof, ReferencePreparationCatalogProof>[]
        {
            x => x with { Generation = 3 },
            x => x with { Deployment = Guid.NewGuid() },
            x => x with { Desired = 3 },
            x => x with { Applied = 2 },
            x => x with { Superseded = true },
            x => x with { Fingerprint = "v6:" + new string('1', 64) },
            x => x with { FormatVersion = 2 },
            x => x with { Ready = new(Guid.NewGuid(), x.Deployment, x.Generation, x.Fingerprint, x.Ready.ConfirmedAt, x.Ready.Resources) },
            x => x with { Ready = new(x.Ready.Id, x.Deployment, 1, x.Fingerprint, x.Ready.ConfirmedAt, x.Ready.Resources) },
            x => x with { Reused = x.Reused.Select((r, i) => i == 0 ? r with { SourceGeneration = 2 } : r).ToArray() },
            x => x with { Reused = x.Reused.Select((r, i) => i == 0 ? r with { ManagementId = Guid.NewGuid() } : r).ToArray() },
            x => x with { Reused = x.Reused.Select((r, i) => i == 0 ? r with { Reintroduced = true } : r).ToArray() },
            x => x with { ReferenceResources = ["left", "foreign"] },
            x => x with { OriginalResources = x.OriginalResources.Take(1).ToArray() },
            x => x with { OriginalResources = x.OriginalResources.Select(r => new ResourceReadyResult(r.Resource, r.ManagementId, r.SourceGeneration,
                r.SourceFingerprint, false, false, r.Operations.Skip(1))).ToArray() }
        }) await Verify(cancelled, null, null, 1, 1, proofFault);
        foreach (var second in new[]
        {
            new WorkloadSubmissionReply { Outcome = "cancelled", Stage = "Publication" },
            new WorkloadSubmissionReply { Outcome = "cancelled", Stage = "Identity" },
            new WorkloadSubmissionReply { Outcome = "stopped", Stage = "Publication" },
            new WorkloadSubmissionReply { Outcome = "unavailable", Stage = "Deployment" }
        }) await Verify(cancelled, null, second, 2, 1);
        foreach (var fault in new Action<WorkloadSubmissionReply>[]
        {
            x => x.Progress.Members[0].Documents = "partial",
            x => x.Progress.Members[0].Publication = "notAttempted",
            x => x.Progress.Members[0].RuntimeAuthority = "revoked",
            x => x.Progress.Members.Clear(),
            x => x.Progress.SubmittedId = Guid.NewGuid().ToString()
        })
        {
            var partial = ContinuationCompleted(accepted, revision); fault(partial);
            await Verify(cancelled, null, partial, 2, 1);
        }
    }

    private static WorkloadSubmissionReply ContinuationCompleted(DurableCommitSummary accepted, RevisionReference revision)
    {
        var reply = new WorkloadSubmissionReply { Outcome = "stopped", Stage = "Deployment", Progress = ContinuationProgress(accepted, revision).WorkloadSubmission.Clone() };
        foreach (var member in reply.Progress.Members) { member.Publication = "published"; member.RuntimeAuthority = "available"; }
        return reply;
    }

    private static DurableCommitSummary ContinuationCommit() => new()
    { DeploymentId = "11111111-1111-1111-1111-111111111111", Generation = 2, ChangeSetId = "change", PlanId = "plan", TechnicalActorId = "same-principal" };
    private static RevisionReference ContinuationRevision() => new() { ApplicationRecordId = "22222222-2222-2222-2222-222222222222", RevisionNumber = 2 };
    private static CommitReply ContinuationProgress(DurableCommitSummary accepted, RevisionReference revision)
    {
        const string checkpoint = "33333333-3333-3333-3333-333333333333";
        var reply = new CommitReply
        {
            Status = CommitStatus.Found,
            DesiredGeneration = new()
            {
                DeploymentId = accepted.DeploymentId,
                Generation = 2,
                ChangeSetId = accepted.ChangeSetId,
                PlanId = accepted.PlanId,
                TechnicalActorId = accepted.TechnicalActorId,
                EnvironmentId = "pilot",
                Reference = revision.Clone(),
                WorkloadDescription = new() { Status = "available" }
            },
            WorkloadSubmission = new()
            {
                DeploymentId = accepted.DeploymentId,
                Generation = 2,
                DesiredGeneration = 2,
                AppliedGeneration = 1,
                Submission = "partial",
                ResourcesReadyId = checkpoint,
                Total = 2
            }
        };
        foreach (var reuse in ContinuationCatalog(accepted).Reused)
            reply.DesiredGeneration.ReusedResources.Add(new ReusedResourceSummary() { ResourceId = reuse.Resource.Value, ManagementId = reuse.ManagementId.ToString(), SourceGeneration = reuse.SourceGeneration });
        foreach (var component in new[] { "api", "worker" })
        {
            var item = new WorkloadReviewItem { ComponentId = component, WorkloadId = "main" }; item.Documents.Add("settings");
            reply.DesiredGeneration.WorkloadDescription.Items.Add(item);
            reply.WorkloadSubmission.Members.Add(new WorkloadSubmissionMember
            {
                Component = component,
                Workload = "main",
                Documents = "prepared",
                Identity = "confirmed",
                Publication = "notAttempted",
                Submission = "notAttempted",
                RuntimeAuthority = "unavailable"
            });
        }
        return reply;
    }
    private static ReferencePreparationCatalogProof ContinuationCatalog(DurableCommitSummary accepted)
    {
        var deployment = Guid.Parse(accepted.DeploymentId); var fingerprint = "v6:" + new string('0', 64);
        var reused = new[] { "left", "right" }.Select((name, i) => new ResourceReuse(ManifestLocalId.Create(name),
            Guid.Parse(i == 0 ? "44444444-4444-4444-4444-444444444444" : "55555555-5555-5555-5555-555555555555"), 1, fingerprint)).ToArray();
        // Three original operations for two Resources deliberately disprove an operation-count == Resource-count assumption.
        var originals = reused.Select((r, i) => new ResourceReadyResult(r.Resource.Value, r.ManagementId, r.SourceGeneration, fingerprint, false, false,
            Enumerable.Range(0, i == 0 ? 2 : 1).Select(n => new ResourceOperationResult(r.Resource.Value + "-" + n, Guid.Parse(n == 0 ? "66666666-6666-6666-6666-666666666666" : "77777777-7777-7777-7777-777777777777"), 1, null, [])))).ToArray();
        var ready = new ResourcesReadyCheckpoint(Guid.Parse("33333333-3333-3333-3333-333333333333"u8), deployment, accepted.Generation,
            fingerprint, DateTimeOffset.UnixEpoch, originals);
        return new(deployment, accepted.Generation, accepted.Generation, 1, false, fingerprint, 1, ready, reused, [], ["left", "right"], originals);
    }

}
