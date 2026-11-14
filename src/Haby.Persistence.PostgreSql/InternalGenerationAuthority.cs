using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

// Catalog identity, not stage counts or a caller-provided receipt, authorizes internal work.
internal static class InternalGenerationAuthority
{
    internal static async Task<DeploymentGenerationRow> RequireAsync(ManifestCatalogDbContext db, DurableChangeSetCommit accepted, CancellationToken token)
    {
        var deployment = Guid.Parse(accepted.DeploymentId.ToString());
        var row = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == accepted.Generation.Value, token)
            ?? throw new InvalidDataException("Accepted internal generation unavailable.");
        var owner = await db.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deployment, token);
        var plan = await db.DurablePlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.PlanId, token);
        var commit = await db.ChangeSetCommits.AsNoTracking().SingleOrDefaultAsync(x => x.ChangeSetId == row.ChangeSetId, token);
        var change = await db.DurableChangeSets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.ChangeSetId, token);
        var inputs = row.Inputs();
        var graph = DurableOperationGraphCodec.Decode(row.GraphJson); graph.ValidateReferences(inputs);
        var initial = InternalResourceStage.IsEmpty(inputs, graph) && row.Generation == 1 && owner?.AppliedGeneration is null &&
            change is { ExpectedAbsence: true, ExpectedGeneration: null };
        var mixed = graph.Baseline is not null && owner?.AppliedGeneration is not null && owner.AppliedGeneration == graph.Baseline.Generation &&
            owner.AppliedGeneration < row.Generation && graph.Nodes.Count == 0 && graph.RetainedResources.Count == 0 &&
            graph.ReusedResources.Count == inputs.Configuration!.Manifest.Spec.Resources.Count && graph.ReusedResources.Count > 0 &&
            change is { ExpectedAbsence: false } && change.ExpectedGeneration == graph.Baseline.Generation;
        if ((!initial && !mixed) || !row.InternalEffects || row.Presence != nameof(DesiredPresence.Present) || owner is null || owner.DesiredGeneration != row.Generation ||
            owner.EnvironmentRecordId != row.EnvironmentRecordId || owner.ApplicationRecordId != row.ApplicationRecordId ||
            plan is null || !plan.InternalEffects || plan.ChangeSetId != row.ChangeSetId || plan.EnvironmentRecordId != row.EnvironmentRecordId ||
            plan.ApplicationRecordId != row.ApplicationRecordId || plan.RevisionNumber != row.RevisionNumber || plan.EnvironmentKey != row.EnvironmentKey ||
            plan.SnapshotFormatVersion != row.SnapshotFormatVersion || plan.Fingerprint != row.Fingerprint || plan.ProviderInputsJson != row.ProviderInputsJson || plan.ConfigurationJson != row.ConfigurationJson || plan.GraphJson != row.GraphJson ||
            change is null || change.RevisionNumber != row.RevisionNumber ||
            change.EnvironmentRecordId != row.EnvironmentRecordId || change.ApplicationRecordId != row.ApplicationRecordId ||
            commit is null || commit.PlanId != row.PlanId || commit.DeploymentId != row.DeploymentId || commit.Generation != row.Generation ||
            commit.PlanId != Guid.Parse(accepted.PlanId.ToString()) || commit.ChangeSetId != Guid.Parse(accepted.ChangeSetId.ToString()) ||
            commit.ActorId != accepted.ActorId || commit.CommittedAt != accepted.CommittedAt || row.AcceptedAt != commit.CommittedAt || row.ActorId != commit.ActorId)
            throw new InvalidDataException("Accepted internal authority mismatch.");
        var revision = await db.Revisions.AsNoTracking().SingleOrDefaultAsync(x => x.ApplicationRecordId == row.ApplicationRecordId && x.LocalRevisionNumber == row.RevisionNumber, token)
            ?? throw new InvalidDataException("Accepted declaration unavailable.");
        var manifest = NormalizedSnapshotCodec.Decode(revision.Snapshot, revision.InputApiVersion, revision.NormalizedModelVersion, revision.ApplicationId, revision.SnapshotFormatVersion);
        if (!manifest.Equals(inputs.Configuration!.Manifest) || mixed && !await ResourceContinuity.ValidateAsync(db, deployment, graph, token) ||
            initial && await db.ResourceManagement.AsNoTracking().AnyAsync(x => x.Environment == row.EnvironmentRecordId && x.Application == row.ApplicationRecordId, token))
            throw new InvalidDataException("Accepted declaration or original Resource provenance mismatch.");
        var pins = await db.InternalPlanInputs.AsNoTracking().Where(x => x.PlanId == row.PlanId).ToArrayAsync(token);
        if (pins.Length != inputs.Providers.Entries.Count || inputs.Providers.Entries.Values.Any(exact =>
            !pins.Any(pin => pin.Subject == (exact.Input is ProviderInput.Workload w ? "Workload/" + w.Component.Value + "/" + w.Id.Value :
                "Resource/" + ((ProviderInput.Resource)exact.Input).Id.Value) && pin.ProviderId.ToString() == exact.Instance.ToString() &&
                pin.ConfigurationRevision == exact.Configuration.Value && pin.PlacementId == (exact.Placement is { } p ? InternalPlacementInput.Decode(p).Id : (Guid?)null))))
            throw new InvalidDataException("Accepted administrative pins mismatch.");
        // Only explicitly accepted internal actions may coexist with the empty
        // Resource graph. Unattributed journal rows cannot disappear in projection.
        if (initial && await db.DurableOperations.AsNoTracking().AnyAsync(operation => operation.DeploymentId == deployment && operation.Generation == row.Generation &&
            !db.ActionIntents.Any(intent => intent.DeploymentId == deployment && intent.Generation == row.Generation && intent.OperationId == operation.OperationId), token))
            throw new InvalidDataException("Unexpected empty-stage operation.");
        if (initial)
        {
            var intents = await db.ActionIntents.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == row.Generation).ToArrayAsync(token);
            if (intents.Any(intent => intent.SubjectKind != "Workload" || !inputs.Providers.Entries.Values.Any(exact =>
                exact.Input is ProviderInput.Workload w && w.Component.Value == intent.Component && w.Id.Value == intent.LocalId &&
                exact.Instance.ToString() == intent.ProviderId.ToString() && exact.Configuration.Value == intent.ConfigurationRevision &&
                exact.Placement is { } placement && intent.PlacementId == InternalPlacementInput.Decode(placement).Id &&
                intent.SubjectContract == manifest.Spec.Components[w.Component].Workloads[w.Id].Type.Value &&
                intent.InputFingerprint == row.Fingerprint)))
                throw new InvalidDataException("Empty stage contains undeclared or Resource action evidence.");
        }
        return row;
    }

    internal static async Task<DeploymentGenerationRow> RequireAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        var commit = await db.ChangeSetCommits.AsNoTracking().SingleOrDefaultAsync(x => x.ChangeSetId == source.ChangeSetId, token)
            ?? throw new InvalidDataException("Accepted internal commit unavailable.");
        var accepted = await RequireAsync(db, new DurableChangeSetCommit(DeploymentChangeSetId.Parse(commit.ChangeSetId.ToString()),
            DeploymentPlanId.Parse(commit.PlanId.ToString()), ApplicationDeploymentId.Parse(commit.DeploymentId.ToString()),
            DeploymentGeneration.Create(commit.Generation), commit.CommittedAt, commit.ActorId), token);
        if (accepted.DeploymentId != source.DeploymentId || accepted.Generation != source.Generation || accepted.PlanId != source.PlanId ||
            accepted.Fingerprint != source.Fingerprint || accepted.ProviderInputsJson != source.ProviderInputsJson ||
            accepted.ConfigurationJson != source.ConfigurationJson || accepted.GraphJson != source.GraphJson || !source.InternalEffects)
            throw new InvalidDataException("Internal source identity mismatch.");
        return accepted;
    }
}
