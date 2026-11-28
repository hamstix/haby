using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal static partial class InternalGenerationAuthority
{
    private static async Task RequireCompletionOrCorrectiveDescendantAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        var owner = await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == source.DeploymentId, token);
        if (await db.GenerationStageCheckpoints.AsNoTracking().AnyAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation &&
            x.Stage == InitialRetirementCompletion.Stage, token))
        {
            if (owner.AppliedGeneration is null || owner.AppliedGeneration < source.Generation) throw new InvalidDataException("Completion has no Applied authority.");
            _ = await InitialRetirementCompletion.RequireProofAsync(db, source, token);
        }
        else if (owner.DesiredGeneration > source.Generation)
            await RequireAcceptedWorkloadDescendantAsync(db, source, owner.DesiredGeneration, token);
        else if (owner.AppliedGeneration >= source.Generation)
            throw new InvalidDataException("Applied high-water mark cannot fabricate source completion.");
    }

    private static async Task RequireCorrectiveBaselineAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        ApplicationDeploymentRow owner, DurableOperationGraph graph, CorrectiveDocument correction, CancellationToken token, bool readOnly)
    {
        if (correction.Deployment != source.DeploymentId || correction.ExpectedDesired + 1 != source.Generation ||
            graph.Retirement is not null || graph.WorkloadUpdate is not null || graph.Nodes.Count != 0 || graph.RetainedResources.Count != 0 ||
            graph.Baseline?.Generation != correction.ExpectedDesired || graph.Baseline.Fingerprint != correction.OriginalFingerprint ||
            graph.ReusedResources.Any(x => x.Reintroduced) || graph.ReusedResources.Count != source.Inputs().Configuration!.Manifest.Spec.Resources.Count ||
            (!readOnly && owner.AppliedGeneration != correction.LastApplied) ||
            readOnly && correction.LastApplied is { } priorApplied && owner.AppliedGeneration < priorApplied)
            throw new InvalidDataException("Corrective baseline authority mismatch.");
        var original = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == correction.ExpectedDesired, token);
        await RequireAcceptedWorkloadDescendantAsync(db, original, source.Generation, token);
        if (graph.ReusedResources.Count > 0 && !await ResourceContinuity.ValidateAsync(db, source.DeploymentId, graph, token, referenceHistory: readOnly))
            throw new InvalidDataException("Corrective original Resource authority mismatch.");
    }

    // An accepted descendant preserves proof readability, never the ancestor's
    // permission to prepare, dispatch, publish, revoke or complete anything.
    internal static async Task RequireSupersededPreparationAsync(ManifestCatalogDbContext db, DeploymentGenerationRow current, long originalGeneration, CancellationToken token)
    {
        if (originalGeneration >= current.Generation || DurableOperationGraphCodec.Decode(current.GraphJson).Correction is null)
            throw new InvalidDataException("Obsolete publication has an active live reference.");
        // Caller is the owner-locked retirement closure, which validates exact
        // current grants plus each acknowledged retirement receipt. Preparation's
        // pre-retirement permission tokens deliberately no longer match after progress.
        if (!await db.ApplicationDeployments.AsNoTracking().AnyAsync(x => x.Id == current.DeploymentId && x.DesiredGeneration == current.Generation, token))
            throw new InvalidDataException("Superseded generation cannot authorize current effects.");
        var original = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == current.DeploymentId && x.Generation == originalGeneration, token);
        await RequireAcceptedWorkloadDescendantAsync(db, original, current.Generation, token);
    }

    private static async Task RequireAcceptedWorkloadDescendantAsync(ManifestCatalogDbContext db, DeploymentGenerationRow original,
        long desired, CancellationToken token)
    {
        if (desired <= original.Generation || desired - original.Generation > 256)
            throw new InvalidDataException("Bounded corrective descendant required.");
        var previous = original;
        for (var number = original.Generation + 1; number <= desired; number++)
        {
            var next = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == original.DeploymentId && x.Generation == number, token);
            var graph = DurableOperationGraphCodec.Decode(next.GraphJson);
            var plan = await db.DurablePlans.AsNoTracking().SingleAsync(x => x.Id == next.PlanId, token);
            var commit = await db.ChangeSetCommits.AsNoTracking().SingleAsync(x => x.ChangeSetId == next.ChangeSetId, token);
            var change = await db.DurableChangeSets.AsNoTracking().SingleAsync(x => x.Id == next.ChangeSetId, token);
            if (!next.InternalEffects || next.EnvironmentRecordId != original.EnvironmentRecordId || next.ApplicationRecordId != original.ApplicationRecordId ||
                graph.Nodes.Count != 0 || graph.RetainedResources.Count != 0 || graph.Baseline?.Generation != previous.Generation ||
                graph.Baseline.Fingerprint != previous.Fingerprint || graph.Baseline.Deployment.ToString() != original.DeploymentId.ToString() ||
                change.ExpectedAbsence || change.ExpectedGeneration != previous.Generation || change.ImportIntentJson is not null ||
                change.EnvironmentRecordId != next.EnvironmentRecordId || change.ApplicationRecordId != next.ApplicationRecordId || change.RevisionNumber != next.RevisionNumber ||
                plan.ChangeSetId != next.ChangeSetId || !plan.InternalEffects || plan.GraphJson != next.GraphJson || plan.Fingerprint != next.Fingerprint ||
                plan.EnvironmentRecordId != next.EnvironmentRecordId || plan.ApplicationRecordId != next.ApplicationRecordId || plan.RevisionNumber != next.RevisionNumber ||
                plan.ProviderInputsJson != next.ProviderInputsJson || plan.ConfigurationJson != next.ConfigurationJson ||
                commit.PlanId != next.PlanId || commit.DeploymentId != next.DeploymentId || commit.Generation != next.Generation ||
                commit.CommittedAt != next.AcceptedAt || commit.ActorId != next.ActorId ||
                DurableOperationGraphCodec.Fingerprint(next.Inputs(), graph) != next.Fingerprint)
                throw new InvalidDataException("Accepted Workload descendant provenance mismatch.");
            if (graph.Correction?.Document is { } correction)
            {
                var intent = CorrectiveIntentCodec.Decode(change.CorrectiveIntentJson);
                var previousCorrection = DurableOperationGraphCodec.Decode(previous.GraphJson).Correction?.Document;
                var retained = previousCorrection is null ? null : ResourcesReadyCheckpointCodec.Decode(previousCorrection.ResourceProof);
                var proof = retained is null
                    ? await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.DeploymentId == previous.DeploymentId && x.Generation == previous.Generation && x.Stage == "ResourcesReady", token)
                    : await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.DeploymentId == previous.DeploymentId && x.Id == retained.Id && x.Generation == retained.Generation && x.Stage == "ResourcesReady", token);
                var proofOrigin = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == previous.DeploymentId && x.Generation == proof.Generation, token);
                if (proof.Generation > previous.Generation || proof.InputFingerprint != proofOrigin.Fingerprint ||
                    retained is not null && (previousCorrection!.ResourcesReady != retained.Id || previousCorrection.ResourceProof != proof.EvidenceJson) ||
                    retained is null && proof.InputFingerprint != previous.Fingerprint)
                    throw new InvalidDataException("Corrective original Resource checkpoint origin mismatch.");
                if (correction.Deployment != previous.DeploymentId || correction.ExpectedDesired != previous.Generation ||
                    correction.OriginalPlan != previous.PlanId || correction.OriginalChangeSet != previous.ChangeSetId || correction.OriginalFingerprint != previous.Fingerprint ||
                    correction.ResourceProof != proof.EvidenceJson || correction.ResourcesReady != proof.Id ||
                    proof.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(proof.EvidenceJson) ||
                    intent?.Deployment.ToString() != original.DeploymentId.ToString() || intent.ExpectedIncompleteDesiredGeneration.Value != previous.Generation)
                    throw new InvalidDataException("Corrective descendant link mismatch.");
            }
            else if (graph.WorkloadUpdate?.Document is { } update)
            {
                // An ordinary successor can continue the chain only through an exact
                // completed predecessor, never by treating incomplete history as Applied.
                var completion = await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.DeploymentId == previous.DeploymentId &&
                    x.Generation == previous.Generation && x.Stage == InitialRetirementCompletion.Stage, token);
                if (change.CorrectiveIntentJson is not null || update.Deployment != previous.DeploymentId || update.BaselineGeneration != previous.Generation ||
                    update.TargetGeneration != next.Generation || update.Plan != previous.PlanId || update.ChangeSet != previous.ChangeSetId ||
                    update.Fingerprint != previous.Fingerprint || update.Completion != completion.Id || update.CompletionHash != completion.EvidenceFingerprint ||
                    completion.InputFingerprint != previous.Fingerprint || completion.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(completion.EvidenceJson))
                    throw new InvalidDataException("Applied UPDATE descendant link mismatch.");
            }
            else throw new InvalidDataException("Accepted corrective or Applied UPDATE descendant link required.");
            previous = next;
        }
    }
}
