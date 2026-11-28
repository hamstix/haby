using System.Text.Json;
using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed record CorrectiveObjectDescriptor(string Role, string Contract, int Format);
internal sealed record CorrectiveMemberReceipt(Guid ManagedIdentity, string Component, string Workload, string Operation, Guid Attempt,
    string IntentHash, string ReceiptHash, CorrectiveConfirmedObject Confirmation, CorrectiveObjectDescriptor? Object);
internal sealed record CorrectiveObjectTransitionSnapshot(int Version, Guid Deployment, long Generation, string InputFingerprint,
    CorrectiveMemberReceipt[] Members, ManagedObjectStateRow[] Updates, OwnedObjectRow[] Creates);
internal sealed record CorrectiveRetirementCatalog(CorrectiveObjectTransitionSnapshot Objects, RuntimePublicationReceipt[] Revocations, string? StartHash);

internal static partial class UpdateRetirementCompletion
{
    internal static async Task CaptureCorrectiveObjectsAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        CorrectiveObjectTransitionSnapshot snapshot, DateTimeOffset at, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null || !await db.ApplicationDeployments.AsNoTracking().AnyAsync(x =>
            x.Id == source.DeploymentId && x.DesiredGeneration == source.Generation, token))
            throw new InvalidDataException("Current locked corrective object owner required.");
        await ValidateCorrectiveObjectsAsync(db, source, snapshot, token);
        var actual = await db.ManagedObjectStates.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation)
            .OrderBy(x => x.ManagedWorkloadId).ThenBy(x => x.Role).Take(513).ToArrayAsync(token);
        if (!actual.Select(State).SequenceEqual(snapshot.Updates.Select(State))) throw new InvalidDataException("Corrective snapshot is not the actual current projection.");
        var json = JsonSerializer.Serialize(snapshot, UpdateCompletionMetadata.Default.CorrectiveObjectTransitionSnapshot);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 2097152) throw new InvalidDataException("Corrective object snapshot exceeds bound.");
        var existing = await db.GenerationStageCheckpoints.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Generation == source.Generation && x.Stage == ObjectTransitions, token);
        if (existing is not null)
        {
            if (existing.InputFingerprint != source.Fingerprint || existing.FormatVersion != 1 || existing.EvidenceJson != json ||
                existing.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(json)) throw new InvalidDataException("Conflicting corrective object snapshot.");
            return;
        }
        db.GenerationStageCheckpoints.Add(new()
        {
            Id = Guid.CreateVersion7(),
            DeploymentId = source.DeploymentId,
            Generation = source.Generation,
            Stage = ObjectTransitions,
            FormatVersion = 1,
            InputFingerprint = source.Fingerprint,
            EvidenceJson = json,
            EvidenceFingerprint = ResourcesReadyCheckpointCodec.Hash(json),
            ConfirmedAt = at
        });
        await db.SaveChangesAsync(token);
    }

    private static (Guid, string, Guid, long, string, long, string, string, int, string) State(ManagedObjectStateRow row) =>
        (row.ManagedWorkloadId, row.Role, row.DeploymentId, row.OriginGeneration, row.OriginOperation, row.Generation, row.OperationId, row.Contract, row.Format, row.Payload);

    private static async Task<string> CorrectiveCatalogHashAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        var snapshot = await ReadCorrectiveSnapshotAsync(db, source, token);
        var retirement = await RetirementEvidenceAsync(db, source, Inputs(source), token);
        return ResourcesReadyCheckpointCodec.Hash(JsonSerializer.Serialize(new CorrectiveRetirementCatalog(snapshot, retirement.Revocations, retirement.StartHash),
            UpdateCompletionMetadata.Default.CorrectiveRetirementCatalog));
    }

    private static async Task<CorrectiveObjectTransitionSnapshot> ReadCorrectiveSnapshotAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        var row = await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Generation == source.Generation && x.Stage == ObjectTransitions, token);
        if (row.FormatVersion != 1 || row.InputFingerprint != source.Fingerprint || row.EvidenceJson.Length > 2097152 ||
            row.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(row.EvidenceJson)) throw new InvalidDataException("Corrective object snapshot corrupt.");
        var snapshot = JsonSerializer.Deserialize(row.EvidenceJson, UpdateCompletionMetadata.Default.CorrectiveObjectTransitionSnapshot)
            ?? throw new InvalidDataException("Corrective object snapshot unavailable.");
        await ValidateCorrectiveObjectsAsync(db, source, snapshot, token);
        return snapshot;
    }

    private static async Task ValidateCorrectiveObjectsAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        CorrectiveObjectTransitionSnapshot snapshot, CancellationToken token)
    {
        var correction = DurableOperationGraphCodec.Decode(source.GraphJson).Correction?.Document ?? throw new InvalidDataException("Corrective inventory required.");
        if (snapshot.Version != 1 || snapshot.Deployment != source.DeploymentId || snapshot.Generation != source.Generation || snapshot.InputFingerprint != source.Fingerprint ||
            snapshot.Members is null || snapshot.Updates is null || snapshot.Creates is null || snapshot.Members.Length != correction.Members.Length ||
            snapshot.Members.Select(x => (x.Component, x.Workload)).Distinct().Count() != snapshot.Members.Length ||
            snapshot.Members.Select(x => x.ManagedIdentity).Distinct().Count() != snapshot.Members.Length ||
            snapshot.Members.Any(x => !correction.Members.Any(m => m.Component == x.Component && m.Workload == x.Workload)) ||
            snapshot.Updates.Length != correction.Members.Count(x => x.State == "ConfirmedOwned") || snapshot.Creates.Length > snapshot.Members.Length ||
            snapshot.Updates.Select(x => x.ManagedWorkloadId).Distinct().Count() != snapshot.Updates.Length ||
            snapshot.Updates.Any(x => !snapshot.Members.Any(m => m.ManagedIdentity == x.ManagedWorkloadId)) ||
            snapshot.Creates.Select(x => x.ManagedWorkloadId).Distinct().Count() != snapshot.Creates.Length)
            throw new InvalidDataException("Complete corrective object snapshot required.");
        foreach (var member in snapshot.Members)
        {
            var frozen = correction.Members.Single(x => x.Component == member.Component && x.Workload == member.Workload);
            var intent = await db.ActionIntents.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.OperationId == member.Operation, token);
            var output = await db.OperationOutputs.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.OperationId == member.Operation, token);
            var operation = await db.DurableOperations.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.OperationId == member.Operation, token);
            var attempt = await db.DurableOperationAttempts.AsNoTracking().SingleAsync(x => x.AttemptId == member.Attempt, token);
            if (member.ManagedIdentity == Guid.Empty || intent.ManagedIdentity != member.ManagedIdentity || intent.Component != member.Component || intent.LocalId != member.Workload ||
                intent.InputFingerprint != source.Fingerprint || intent.OutputContract != output.Contract || output.Key != "receipt" ||
                ResourcesReadyCheckpointCodec.Hash(intent.Payload) != member.IntentHash || ResourcesReadyCheckpointCodec.Hash(output.Payload) != member.ReceiptHash ||
                operation.State != "Succeeded" || operation.CurrentAttemptId != member.Attempt || attempt.DeploymentId != source.DeploymentId ||
                attempt.Generation != source.Generation || attempt.OperationId != member.Operation || attempt.AttemptNumber != operation.AttemptNumber ||
                attempt.FinishedAt is null || attempt.DispatchedAt is null || attempt.FinishedAt != operation.FinalizedAt || attempt.DispatchedAt != operation.DispatchedAt ||
                attempt.Outcome is not ("Succeeded" or "Uncertain") || member.Confirmation is not { Identity.Length: > 0 and <= 256 } ||
                member.Confirmation.Image != frozen.Image.TargetImage)
                throw new InvalidDataException("Corrective immutable receipt mismatch.");
            if (frozen.State == "ConfirmedOwned")
            {
                var state = snapshot.Updates.Single(x => x.ManagedWorkloadId == member.ManagedIdentity);
                if (frozen.ManagedIdentity != member.ManagedIdentity || member.Confirmation.Identity != frozen.Confirmation!.Identity || member.Object is null ||
                    state.DeploymentId != source.DeploymentId || state.Generation != source.Generation || state.OperationId != member.Operation ||
                    state.OriginGeneration != frozen.OriginGeneration || state.OriginOperation != frozen.Operation || state.Role != member.Object.Role ||
                    state.Contract != member.Object.Contract || state.Format != member.Object.Format || state.Contract != output.Contract || state.Payload != output.Payload)
                    throw new InvalidDataException("Corrective UPDATE snapshot mismatch.");
            }
            else
            {
                if (snapshot.Updates.Any(x => x.ManagedWorkloadId == member.ManagedIdentity)) throw new InvalidDataException("Initial CREATE cannot fabricate an UPDATE projection.");
                var objects = snapshot.Creates.Where(x => x.ManagedWorkloadId == member.ManagedIdentity).ToArray();
                if (member.Object is null)
                {
                    if (objects.Length != 0) throw new InvalidDataException("Document-free CREATE cannot invent object inventory.");
                }
                else
                {
                    var created = objects.Single();
                    var stored = await db.OwnedObjects.AsNoTracking().SingleAsync(x => x.ManagedWorkloadId == member.ManagedIdentity && x.Role == created.Role, token);
                    if (created.DeploymentId != source.DeploymentId || created.Generation != source.Generation || created.OperationId != member.Operation ||
                        created.Role != member.Object.Role || created.Contract != member.Object.Contract || created.Format != member.Object.Format ||
                        created.Contract != output.Contract || created.Payload != output.Payload || stored.DeploymentId != created.DeploymentId || stored.Generation != created.Generation ||
                        stored.OperationId != created.OperationId || stored.Contract != created.Contract || stored.Format != created.Format || stored.Payload != created.Payload)
                        throw new InvalidDataException("Corrective initial CREATE inventory mismatch.");
                }
            }
        }
        if (snapshot.Creates.Any(x => !snapshot.Members.Any(m => m.ManagedIdentity == x.ManagedWorkloadId && m.Object is not null &&
            correction.Members.Single(c => c.Component == m.Component && c.Workload == m.Workload).State == "NeverConfirmedInitialCreate")))
            throw new InvalidDataException("Unexplained corrective CREATE inventory.");
    }
}

