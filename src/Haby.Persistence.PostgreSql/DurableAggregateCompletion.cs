using Haby.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

internal static partial class DurableAggregateCompletion
{
    // Caller owns the transaction. All completion writers lock the aggregate BEFORE
    // changing operations. No Provider I/O, SecretStore call or second connection here.
    internal static Task LockAsync(ManifestCatalogDbContext context, Guid deployment, CancellationToken token) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM application_deployments WHERE id = {deployment} FOR UPDATE", token);

    // Confirmed operation/output/attempt writes precede this savepoint. Only the
    // checkpoint and Applied transition may roll back independently of that evidence.
    // Recovery decisions deliberately use TryAdvanceAsync directly instead.
    internal static async Task<bool> TryAdvanceConfirmedAsync(ManifestCatalogDbContext context, Guid deployment,
        long generation, CancellationToken token, IEnumerable<IExternalOperationOutputContract>? contracts = null,
        IEnumerable<IProviderConfigurationCodec>? codecs = null, ReadinessPreparation? prepared = null)
    {
        var transaction = context.Database.CurrentTransaction ?? throw new InvalidOperationException("Completion requires a transaction.");
        const string savepoint = "haby_resources_ready";
        var original = context.ChangeTracker.Entries<GenerationStageCheckpointRow>().Select(x => x.Entity).ToHashSet();
        await transaction.CreateSavepointAsync(savepoint, token);
        bool advanced;
        try
        {
            advanced = await TryAdvanceAsync(context, deployment, generation, token, contracts, codecs: codecs, prepared: prepared);
        }
        catch (Exception error) when (error is PostgresException or DbUpdateException { InnerException: PostgresException })
        {
            // Only a server-reported database error can be recovered this way. A
            // connection, cancellation, rollback or commit failure remains explicit.
            advanced = false;
        }
        if (!advanced)
        {
            await transaction.RollbackToSavepointAsync(savepoint, token);
            // SaveChanges may have accepted the inserted receipt before a later
            // ExecuteUpdate failed; rollback does not restore EF's tracked state.
            foreach (var entry in context.ChangeTracker.Entries<GenerationStageCheckpointRow>().Where(x => !original.Contains(x.Entity)).ToArray())
                entry.State = EntityState.Detached;
        }
        await transaction.ReleaseSavepointAsync(savepoint, token);
        return advanced;
    }

    internal static async Task<bool> TryAdvanceAsync(ManifestCatalogDbContext context, Guid deployment,
        long generation, CancellationToken token, IEnumerable<IExternalOperationOutputContract>? contracts = null,
        DurableActionAdmission? admission = null, IEnumerable<IProviderConfigurationCodec>? codecs = null, ReadinessPreparation? prepared = null, bool internalResourceStage = false)
    {
        await LockAsync(context, deployment, token);
        var owner = await context.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(r => r.Id == deployment, token);
        var source = await context.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(r => r.DeploymentId == deployment && r.Generation == generation, token);
        if (owner is null || source is null || owner.DesiredGeneration < generation || source.InternalEffects != internalResourceStage) return false;
        try
        {
            var inputs = source.Inputs();
            var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
            if (source.Presence != nameof(DesiredPresence.Present) || inputs.Configuration is not { } configuration ||
                !internalResourceStage && configuration.Manifest.Spec.Components.Values.Any(c => c.Workloads.Count != 0) ||
                !ResourceReadinessCoverage.IsComplete(configuration.Manifest.Spec.Resources.Keys.Select(x => x.Value),
                    graph.Nodes.Select(x => x.ResourceId.Value), graph.ReusedResources.Select(x => x.Resource.Value)) ||
                graph.Nodes.Count + graph.ReusedResources.Count == 0) return false;
            var stored = await ReadAsync(context, source, token, internalResourceStage);
            if (stored is not null) return internalResourceStage || owner.AppliedGeneration >= generation;
            // Applied is never an authority for backfilling evidence.
            if (owner.DesiredGeneration != generation || owner.AppliedGeneration >= generation ||
                graph.Baseline?.Generation != owner.AppliedGeneration) return false;
            if (prepared is null && graph.Baseline is not null) return false;
            if (prepared is not null && await InspectMetadataAsync(context, deployment, generation, contracts ?? [], codecs ?? [], token, admission, internalResourceStage: internalResourceStage) is null) return false;
            if (graph.Baseline is not null)
            {
                if (admission is null) return false;
                await ResourceContinuity.LockAsync(context, deployment, token);
                if (!await ResourceContinuity.ValidateAsync(context, deployment, graph, token) ||
                    !await ResourceContinuity.ValidateOutputsAsync(context, deployment, graph, contracts ?? [], codecs ?? [],
                        admission, internalResourceStage ? InternalResourceStage.Project(inputs) : inputs, token, creationGeneration: graph.Nodes.Count > 0 ? generation : null)) return false;
            }
            var results = await BuildResultsAsync(context, source, graph, contracts?.ToArray() ?? [], codecs?.ToArray() ?? [], token, checkCredential: prepared is not null);
            if (results is null || (prepared is null
                ? !results.SelectMany(x => x.Operations).All(x => x.RecoveryDecisionId is not null)
                : !MatchesPreparation(prepared, deployment, generation, source.Fingerprint, results))) return false;
            var at = DateTimeOffset.UtcNow;
            at = at.AddTicks(-(at.Ticks % TimeSpan.TicksPerMicrosecond));
            var proof = new ResourcesReadyCheckpoint(Guid.CreateVersion7(), deployment, generation, source.Fingerprint, at, results);
            var json = ResourcesReadyCheckpointCodec.Encode(proof);
            context.GenerationStageCheckpoints.Add(new()
            {
                Id = proof.Id,
                DeploymentId = deployment,
                Generation = generation,
                Stage = ResourcesReadyCheckpoint.Stage,
                FormatVersion = ResourcesReadyCheckpoint.FormatVersion,
                InputFingerprint = source.Fingerprint,
                EvidenceJson = json,
                EvidenceFingerprint = ResourcesReadyCheckpointCodec.Hash(json),
                ConfirmedAt = at
            });
            await context.SaveChangesAsync(token);
            if (internalResourceStage) return true;
            return await context.ApplicationDeployments.Where(r => r.Id == deployment && r.DesiredGeneration == generation && r.AppliedGeneration == owner.AppliedGeneration)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.AppliedGeneration, generation)
                    .SetProperty(r => r.ConcurrencyToken, r => r.ConcurrencyToken + 1), token) == 1;
        }
        catch (InvalidDataException) { return false; }
    }

    internal static async Task<ResourcesReadyCheckpoint?> ReadAsync(ManifestCatalogDbContext context, DeploymentGenerationRow source, CancellationToken token, bool internalResourceStage = false)
    {
        if (source.InternalEffects && !internalResourceStage) throw new InvalidDataException("Internal generation is not publicly admitted.");
        var rows = await context.GenerationStageCheckpoints.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation).ToArrayAsync(token);
        if (rows.Length == 0) return null;
        if (rows is not [var row] || row.Stage != ResourcesReadyCheckpoint.Stage || row.FormatVersion != ResourcesReadyCheckpoint.FormatVersion ||
            row.InputFingerprint != source.Fingerprint || row.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(row.EvidenceJson))
            throw new InvalidDataException("Unsupported or conflicting generation checkpoint.");
        var proof = ResourcesReadyCheckpointCodec.Decode(row.EvidenceJson);
        if (proof.Id != row.Id || proof.DeploymentId != source.DeploymentId || proof.Generation != source.Generation ||
            proof.InputFingerprint != source.Fingerprint || proof.ConfirmedAt != row.ConfirmedAt)
            throw new InvalidDataException("Resource checkpoint identity mismatch.");
        var inputs = source.Inputs();
        var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        if (!internalResourceStage && inputs.Configuration!.Manifest.Spec.Components.Values.Any(c => c.Workloads.Count != 0) ||
            !ResourceReadinessCoverage.IsComplete(inputs.Configuration!.Manifest.Spec.Resources.Keys.Select(x => x.Value),
                graph.Nodes.Select(x => x.ResourceId.Value), graph.ReusedResources.Select(x => x.Resource.Value)))
            throw new InvalidDataException("Resource checkpoint declaration mismatch.");
        // Historical reads do not resolve credentials or require continued secret availability.
        var expected = await BuildResultsAsync(context, source, graph, null, [], token, checkCredential: false);
        if (expected is null || ResourcesReadyCheckpointCodec.Encode(new(proof.Id, proof.DeploymentId, proof.Generation,
                proof.InputFingerprint, proof.ConfirmedAt, expected)) != row.EvidenceJson)
            throw new InvalidDataException("Resource checkpoint evidence mismatch.");
        return proof;
    }

    internal static async Task<bool> HasProofAsync(ManifestCatalogDbContext db, Guid deployment, long generation, CancellationToken token)
    {
        try
        {
            var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == generation, token);
            return source is not null && await ReadAsync(db, source, token) is not null;
        }
        catch (InvalidDataException) { return false; }
    }
}
