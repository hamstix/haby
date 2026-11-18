using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal static partial class InternalGenerationAuthority
{
    private static async Task<DeploymentGenerationRow> RequireUpdateReadAsync(ManifestCatalogDbContext db,
        DeploymentGenerationRow source, CancellationToken token)
    {
        var commit = await db.ChangeSetCommits.AsNoTracking().SingleAsync(x => x.ChangeSetId == source.ChangeSetId, token);
        var accepted = new DurableChangeSetCommit(Haby.Domain.DeploymentChangeSetId.Parse(commit.ChangeSetId.ToString()),
            Haby.Domain.DeploymentPlanId.Parse(commit.PlanId.ToString()), Haby.Domain.ApplicationDeploymentId.Parse(commit.DeploymentId.ToString()),
            Haby.Domain.DeploymentGeneration.Create(commit.Generation), commit.CommittedAt, commit.ActorId);
        var verified = await RequireCoreAsync(db, accepted, true, token, true);
        if (verified.GraphJson != source.GraphJson || verified.Fingerprint != source.Fingerprint || verified.PlanId != source.PlanId ||
            verified.ConfigurationJson != source.ConfigurationJson || verified.ProviderInputsJson != source.ProviderInputsJson || !source.InternalEffects)
            throw new InvalidDataException("Update read source mismatch.");
        var applied = await db.ApplicationDeployments.Where(x => x.Id == source.DeploymentId).Select(x => x.AppliedGeneration).SingleAsync(token);
        if (applied >= source.Generation) _ = await InitialRetirementCompletion.RequireProofAsync(db, verified, token);
        return verified;
    }

    internal static async Task RequireCurrentStageAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        var inventory = DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate?.Document;
        if (inventory is null) return;
        foreach (var retained in inventory.RetainedBindings)
        {
            var binding = await db.RuntimeIdentityBindings.AsNoTracking().SingleAsync(x => x.Id == retained.Id, token);
            if (InternalRuntimePublication.BindingState(binding).Status != "Prepared" || binding.ConcurrencyToken != retained.Token)
                throw new InvalidDataException("Retained runtime binding no longer authorizes preparation.");
        }
        foreach (var retained in inventory.ObsoletePublications)
        {
            var permission = await db.RuntimeConfigurationPermissions.AsNoTracking().SingleAsync(x => x.Id == retained.Permission, token);
            if (InternalRuntimePublication.PermissionState(permission) is not { Status: "Published" } || permission.ConcurrencyToken != retained.PermissionToken ||
                permission.BindingId != retained.Binding || permission.RevisionId != retained.Revision)
                throw new InvalidDataException("Retained runtime permission no longer authorizes preparation.");
        }
    }

    private static async Task RequireUpdateBaselineAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        ApplicationDeploymentRow owner, DurableOperationGraph graph, WorkloadUpdateDocument update, CancellationToken token, bool readOnly = false)
    {
        var inputs = source.Inputs();
        if (graph.Retirement is not null || update.Deployment != source.DeploymentId || update.TargetGeneration != source.Generation ||
            update.TargetGeneration != update.BaselineGeneration + 1 || (!readOnly && owner.AppliedGeneration != update.BaselineGeneration || readOnly && owner.AppliedGeneration != update.BaselineGeneration && owner.AppliedGeneration < source.Generation) ||
            graph.Baseline?.Generation != update.BaselineGeneration || graph.Baseline.Deployment.ToString() != source.DeploymentId.ToString() ||
            graph.Baseline.Fingerprint != update.Fingerprint || graph.Nodes.Count != 0 || graph.RetainedResources.Count != 0 ||
            graph.ReusedResources.Count != inputs.Providers.Declaration.Spec.Resources.Count || graph.ReusedResources.Any(x => x.Reintroduced) ||
            graph.ReusedResources.Count > 0 && !await ResourceContinuity.ValidateAsync(db, source.DeploymentId, graph, token))
            throw new InvalidDataException("Update baseline authority mismatch.");
        var baseline = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == update.BaselineGeneration, token);
        baseline = await RequireReadAsync(db, baseline, token);
        // RequireReadAsync already validated the complete initial completion proof.
        var completion = await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == baseline.Generation && x.Stage == InitialRetirementCompletion.Stage, token);
        var proof = System.Text.Json.JsonSerializer.Deserialize(completion.EvidenceJson, InitialCompletionMetadata.Default.InitialCompletionProof)!;
        if (baseline.PlanId != update.Plan || baseline.ChangeSetId != update.ChangeSet || baseline.Fingerprint != update.Fingerprint ||
            baseline.EnvironmentRecordId != source.EnvironmentRecordId || baseline.ApplicationRecordId != source.ApplicationRecordId ||
            proof.Id != update.Completion || completion.EvidenceFingerprint != update.CompletionHash || update.RetainedPublications.Length != 0)
            throw new InvalidDataException("Original update completion proof mismatch.");
        var managed = await db.ManagedWorkloads.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId).ToArrayAsync(token);
        var subjects = inputs.Providers.Entries.Keys.OfType<ProviderInput.Workload>().ToArray();
        if (managed.Length != update.Members.Length || subjects.Length != managed.Length)
            throw new InvalidDataException("Complete update member inventory required.");
        foreach (var member in update.Members)
        {
            var stable = managed.SingleOrDefault(x => x.Id == member.ManagedIdentity && x.Component == member.Component && x.Workload == member.Workload);
            var subject = subjects.SingleOrDefault(x => x.Component.Value == member.Component && x.Id.Value == member.Workload);
            if (stable is null || subject is null || stable.EnvironmentId != source.EnvironmentRecordId || stable.ApplicationId != source.ApplicationRecordId ||
                stable.TargetContract != member.TargetContract || stable.TargetFormat != member.TargetFormat || stable.TargetPayload != member.TargetPayload ||
                stable.ProviderId.ToString() != inputs.Providers.Entries[subject].Instance.ToString()) throw new InvalidDataException("Update managed target mismatch.");
            var intent = await db.ActionIntents.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == baseline.Generation && x.OperationId == member.Operation, token);
            var operation = await db.DurableOperations.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == baseline.Generation && x.OperationId == member.Operation, token);
            if (intent.ManagedIdentity != member.ManagedIdentity || intent.SubjectKind != "Workload" || intent.Component != member.Component || intent.LocalId != member.Workload ||
                operation.State != nameof(DurableOperationState.Succeeded) || operation.CurrentAttemptId != member.Attempt ||
                member.Observation != await db.ActionObservations.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == baseline.Generation && x.OperationId == member.Operation && x.Confirmed).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token))
                throw new InvalidDataException("Update original action proof mismatch.");
        }
        var objects = await db.OwnedObjects.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId).ToArrayAsync(token);
        if (objects.Length != update.RetainedObjects.Length || update.RetainedObjects.Any(x => !objects.Any(o => o.ManagedWorkloadId == x.ManagedIdentity && o.Role == x.Role &&
            o.Contract == x.Contract && o.Format == x.Format && o.Payload == x.Payload && o.Generation == x.OriginGeneration && o.OperationId == x.OriginOperation)))
            throw new InvalidDataException("Original owned object inventory mismatch.");
        var ids = managed.Select(x => x.Id).ToArray();
        var bindings = await db.RuntimeIdentityBindings.AsNoTracking().Where(x => ids.Contains(x.ManagedWorkloadId)).ToArrayAsync(token);
        if (bindings.Length != update.RetainedBindings.Length || update.RetainedBindings.Any(x => !bindings.Any(b => b.Id == x.Id && b.ManagedWorkloadId == x.ManagedIdentity &&
            b.Contract == x.Contract && b.Format == x.Format && b.ValidationPayload == x.Payload))) throw new InvalidDataException("Original binding inventory mismatch.");
        if (proof.Publications.Length != update.ObsoletePublications.Length || proof.Publications.Any(x => !update.ObsoletePublications.Any(p =>
            p.Permission == x.Permission && p.Binding == x.Binding && p.Revision == x.Revision && p.PermissionToken == x.PermissionToken && p.BindingToken == x.BindingToken &&
            p.Request == x.Request && p.Receipt == x.Receipt && p.ReceiptHash == x.ReceiptHash))) throw new InvalidDataException("Complete old publication inventory required.");
    }
}
