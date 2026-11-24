using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

// Catalog identity, not stage counts or a caller-provided receipt, authorizes internal work.
internal static partial class InternalGenerationAuthority
{
    internal static async Task<DeploymentGenerationRow> RequireAsync(ManifestCatalogDbContext db, DurableChangeSetCommit accepted, CancellationToken token)
        => await RequireCoreAsync(db, accepted, false, token);

    // Preparation/submission capability only. Initial retirement and public lifecycle
    // callers deliberately keep RequireAsync, which does not admit successors.
    internal static Task<DeploymentGenerationRow> RequireStageAsync(ManifestCatalogDbContext db, DurableChangeSetCommit accepted, CancellationToken token) =>
        WithConnectionAsync(db, () => RequireCoreAsync(db, accepted, false, token, true), token);

    // Linked proof reads share one bounded catalog connection. No transaction or
    // connection scope crosses adapter I/O; nested callers balance their own opens.
    private static async Task<DeploymentGenerationRow> WithConnectionAsync(ManifestCatalogDbContext db,
        Func<Task<DeploymentGenerationRow>> read, CancellationToken token)
    {
        await db.Database.OpenConnectionAsync(token);
        try { return await read(); }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    internal static Task<DeploymentGenerationRow> RequireStageReadAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token) =>
        WithConnectionAsync(db, () => DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate is null
            ? RequireReadAsync(db, source, token) : RequireUpdateReadAsync(db, source, token), token);

    internal static async Task<DeploymentGenerationRow> RequireStageReadAsync(ManifestCatalogDbContext db, DurableChangeSetCommit accepted, CancellationToken token)
    {
        var id = Guid.Parse(accepted.DeploymentId.ToString());
        var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == id && x.Generation == accepted.Generation.Value, token)
            ?? throw new InvalidDataException("Accepted internal generation unavailable.");
        if (source.PlanId.ToString() != accepted.PlanId.ToString() || source.ChangeSetId.ToString() != accepted.ChangeSetId.ToString() ||
            source.AcceptedAt != accepted.CommittedAt || source.ActorId != accepted.ActorId) throw new InvalidDataException("Stage read receipt mismatch.");
        return await WithConnectionAsync(db, () => DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate is null
            ? RequireReadAsync(db, accepted, token) : RequireUpdateReadAsync(db, source, token), token);
    }

    internal static async Task<DeploymentGenerationRow> RequireStageAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        var commit = await db.ChangeSetCommits.AsNoTracking().SingleAsync(x => x.ChangeSetId == source.ChangeSetId, token);
        var verified = await RequireStageAsync(db, new DurableChangeSetCommit(DeploymentChangeSetId.Parse(commit.ChangeSetId.ToString()),
            DeploymentPlanId.Parse(commit.PlanId.ToString()), ApplicationDeploymentId.Parse(commit.DeploymentId.ToString()),
            DeploymentGeneration.Create(commit.Generation), commit.CommittedAt, commit.ActorId), token);
        if (verified.PlanId != source.PlanId || verified.Fingerprint != source.Fingerprint || verified.GraphJson != source.GraphJson ||
            verified.ConfigurationJson != source.ConfigurationJson || verified.ProviderInputsJson != source.ProviderInputsJson || !source.InternalEffects)
            throw new InvalidDataException("Stage source mismatch.");
        return verified;
    }

    internal static async Task<DeploymentGenerationRow> RequireReadAsync(ManifestCatalogDbContext db, DurableChangeSetCommit accepted, CancellationToken token)
    {
        var id = Guid.Parse(accepted.DeploymentId.ToString());
        // Cache immutable proof validation only within this catalog transaction. Current
        // permission and attempt checks remain separate and are never cached here.
        if (db.Database.CurrentTransaction is not null && db.CompletedInitialReads.TryGetValue((db.Database.CurrentTransaction.TransactionId, id, accepted.Generation.Value), out var verified))
        {
            if (verified.PlanId.ToString() != accepted.PlanId.ToString() || verified.ChangeSetId.ToString() != accepted.ChangeSetId.ToString() ||
                verified.AcceptedAt != accepted.CommittedAt || verified.ActorId != accepted.ActorId)
                throw new InvalidDataException("Conflicting completed read authority.");
            return verified;
        }
        var applied = await db.ApplicationDeployments.AsNoTracking().Where(x => x.Id == id).Select(x => x.AppliedGeneration).SingleOrDefaultAsync(token);
        if (applied < accepted.Generation.Value || applied is null) return await RequireAsync(db, accepted, token);
        var row = await RequireCoreAsync(db, accepted, true, token);
        _ = await InitialRetirementCompletion.RequireProofAsync(db, row, token);
        if (db.Database.CurrentTransaction is not null) db.CompletedInitialReads[(db.Database.CurrentTransaction.TransactionId, id, accepted.Generation.Value)] = row;
        return row;
    }

    // Authentication validates immutable identity provenance. Current Reference use is
    // authorized separately for the exact document before any application material read.
    internal static async Task<DeploymentGenerationRow> RequireAuthenticationStageAsync(ManifestCatalogDbContext db,
        DurableChangeSetCommit accepted, CancellationToken token)
    {
        var verified = await RequireCoreAsync(db, accepted, true, token, true, referenceHistory: true);
        var applied = await db.ApplicationDeployments.Where(x => x.Id == verified.DeploymentId).Select(x => x.AppliedGeneration).SingleAsync(token);
        if (applied >= verified.Generation) _ = await InitialRetirementCompletion.RequireProofAsync(db, verified, token);
        return verified;
    }

    internal static async Task<DeploymentGenerationRow> RequireAuthenticationStageAsync(ManifestCatalogDbContext db,
        DeploymentGenerationRow source, CancellationToken token)
    {
        var commit = await db.ChangeSetCommits.AsNoTracking().SingleAsync(x => x.ChangeSetId == source.ChangeSetId, token);
        var verified = await RequireAuthenticationStageAsync(db, new DurableChangeSetCommit(Haby.Domain.DeploymentChangeSetId.Parse(commit.ChangeSetId.ToString()),
            Haby.Domain.DeploymentPlanId.Parse(commit.PlanId.ToString()), Haby.Domain.ApplicationDeploymentId.Parse(commit.DeploymentId.ToString()),
            Haby.Domain.DeploymentGeneration.Create(commit.Generation), commit.CommittedAt, commit.ActorId), token);
        if (verified.GraphJson != source.GraphJson || verified.Fingerprint != source.Fingerprint || verified.ConfigurationJson != source.ConfigurationJson ||
            verified.ProviderInputsJson != source.ProviderInputsJson || verified.PlanId != source.PlanId) throw new InvalidDataException("Authentication source mismatch.");
        return verified;
    }

    private static async Task<DeploymentGenerationRow> RequireCoreAsync(ManifestCatalogDbContext db, DurableChangeSetCommit accepted, bool readOnly, CancellationToken token,
        bool stage = false, bool referenceHistory = false)
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
        if (DurableOperationGraphCodec.Fingerprint(inputs, graph) != row.Fingerprint) throw new InvalidDataException("Accepted generation fingerprint mismatch.");
        var update = stage || readOnly ? graph.WorkloadUpdate?.Document : null;
        var retirement = graph.Retirement?.Document;
        if (retirement is null && update is null) throw new InvalidDataException("Frozen stage authority required.");
        var priorApplied = readOnly ? retirement?.PriorApplied ?? update?.BaselineGeneration : owner?.AppliedGeneration;
        var initial = InternalResourceStage.IsEmpty(inputs, graph) && row.Generation == 1 && priorApplied is null &&
            change is { ExpectedAbsence: true, ExpectedGeneration: null };
        var mixed = graph.Baseline is not null && priorApplied is not null && priorApplied == graph.Baseline.Generation &&
            priorApplied < row.Generation && graph.Nodes.Count == 0 && graph.RetainedResources.Count == 0 &&
            graph.ReusedResources.Count == inputs.Configuration!.Manifest.Spec.Resources.Count && graph.ReusedResources.Count > 0 &&
            change is { ExpectedAbsence: false } && change.ExpectedGeneration == graph.Baseline.Generation;
        if ((!initial && !mixed && update is null) || !row.InternalEffects || row.Presence != nameof(DesiredPresence.Present) || owner is null || (readOnly ? owner.DesiredGeneration < row.Generation : owner.DesiredGeneration != row.Generation) ||
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
        if (update is not null) await RequireUpdateBaselineAsync(db, row, owner, graph, update, token, readOnly);
        var revision = await db.Revisions.AsNoTracking().SingleOrDefaultAsync(x => x.ApplicationRecordId == row.ApplicationRecordId && x.LocalRevisionNumber == row.RevisionNumber, token)
            ?? throw new InvalidDataException("Accepted declaration unavailable.");
        var manifest = NormalizedSnapshotCodec.Decode(revision.Snapshot, revision.InputApiVersion, revision.NormalizedModelVersion, revision.ApplicationId, revision.SnapshotFormatVersion);
        if (!manifest.Equals(inputs.Configuration!.Manifest) || mixed && !await ResourceContinuity.ValidateAsync(db, deployment, graph, token, referenceHistory) ||
            initial && await db.CurrentResourceManagement.AsNoTracking().AnyAsync(x => x.Environment == row.EnvironmentRecordId && x.Application == row.ApplicationRecordId, token))
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

    internal static Task<DeploymentGenerationRow> RequireAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token) => RequireRowAsync(db, source, false, token);
    internal static Task<DeploymentGenerationRow> RequireReadAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token) => RequireRowAsync(db, source, true, token);
    private static async Task<DeploymentGenerationRow> RequireRowAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, bool readOnly, CancellationToken token)
    {
        var commit = await db.ChangeSetCommits.AsNoTracking().SingleOrDefaultAsync(x => x.ChangeSetId == source.ChangeSetId, token)
            ?? throw new InvalidDataException("Accepted internal commit unavailable.");
        var receipt = new DurableChangeSetCommit(DeploymentChangeSetId.Parse(commit.ChangeSetId.ToString()),
            DeploymentPlanId.Parse(commit.PlanId.ToString()), ApplicationDeploymentId.Parse(commit.DeploymentId.ToString()),
            DeploymentGeneration.Create(commit.Generation), commit.CommittedAt, commit.ActorId);
        var accepted = readOnly ? await RequireReadAsync(db, receipt, token) : await RequireAsync(db, receipt, token);
        if (accepted.DeploymentId != source.DeploymentId || accepted.Generation != source.Generation || accepted.PlanId != source.PlanId ||
            accepted.Fingerprint != source.Fingerprint || accepted.ProviderInputsJson != source.ProviderInputsJson ||
            accepted.ConfigurationJson != source.ConfigurationJson || accepted.GraphJson != source.GraphJson || !source.InternalEffects)
            throw new InvalidDataException("Internal source identity mismatch.");
        return accepted;
    }
}

public sealed partial class ManifestCatalogDbContext
{
    internal Dictionary<(Guid Transaction, Guid Deployment, long Generation), DeploymentGenerationRow> CompletedInitialReads { get; } = [];
}
