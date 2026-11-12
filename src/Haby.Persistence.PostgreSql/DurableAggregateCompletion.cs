using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal static partial class DurableAggregateCompletion
{
    // Caller owns the transaction. All completion writers lock the aggregate BEFORE
    // changing operations. No Provider I/O, SecretStore call or second connection here.
    internal static Task LockAsync(ManifestCatalogDbContext context, Guid deployment, CancellationToken token) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM application_deployments WHERE id = {deployment} FOR UPDATE", token);

    internal static async Task<bool> TryAdvanceAsync(ManifestCatalogDbContext context, Guid deployment,
        long generation, CancellationToken token, IEnumerable<IExternalOperationOutputContract>? contracts = null,
        DurableActionAdmission? admission = null, IEnumerable<IProviderConfigurationCodec>? codecs = null)
    {
        await LockAsync(context, deployment, token);
        var owner = await context.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(r => r.Id == deployment, token);
        var source = await context.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(r => r.DeploymentId == deployment && r.Generation == generation, token);
        if (owner is null || source is null || owner.DesiredGeneration < generation) return false;
        try
        {
            var inputs = source.Inputs();
            var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
            if (source.Presence != nameof(DesiredPresence.Present) || inputs.Configuration is not { } configuration ||
                configuration.Manifest.Spec.Components.Values.Any(c => c.Workloads.Count != 0) ||
                !ResourceReadinessCoverage.IsComplete(configuration.Manifest.Spec.Resources.Keys.Select(x => x.Value),
                    graph.Nodes.Select(x => x.ResourceId.Value), graph.ReusedResources.Select(x => x.Resource.Value)) ||
                graph.Nodes.Count + graph.ReusedResources.Count == 0) return false;
            var stored = await ReadAsync(context, source, token);
            if (stored is not null) return owner.AppliedGeneration >= generation;
            // Applied is never an authority for backfilling evidence.
            if (owner.DesiredGeneration != generation || owner.AppliedGeneration >= generation ||
                graph.Baseline?.Generation != owner.AppliedGeneration) return false;
            if (graph.Baseline is not null)
            {
                if (admission is null) return false;
                await ResourceContinuity.LockAsync(context, deployment, token);
                if (!await ResourceContinuity.ValidateAsync(context, deployment, graph, token) ||
                    !await ResourceContinuity.ValidateOutputsAsync(context, deployment, graph, contracts ?? [], codecs ?? [],
                        admission, inputs, token, creationGeneration: graph.Nodes.Count > 0 ? generation : null)) return false;
            }
            var results = await BuildResultsAsync(context, source, graph, contracts?.ToArray() ?? [], codecs?.ToArray() ?? [], token, checkCredential: true);
            if (results is null) return false;
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
            return await context.ApplicationDeployments.Where(r => r.Id == deployment && r.DesiredGeneration == generation && r.AppliedGeneration == owner.AppliedGeneration)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.AppliedGeneration, generation)
                    .SetProperty(r => r.ConcurrencyToken, r => r.ConcurrencyToken + 1), token) == 1;
        }
        catch (InvalidDataException) { return false; }
    }

    internal static async Task<ResourcesReadyCheckpoint?> ReadAsync(ManifestCatalogDbContext context, DeploymentGenerationRow source, CancellationToken token)
    {
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
        if (inputs.Configuration!.Manifest.Spec.Components.Values.Any(c => c.Workloads.Count != 0) ||
            !ResourceReadinessCoverage.IsComplete(inputs.Configuration.Manifest.Spec.Resources.Keys.Select(x => x.Value),
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
