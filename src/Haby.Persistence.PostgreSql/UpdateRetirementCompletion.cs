using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed record RetirementRecipient(string Subject, RecipientObservation Observation);
internal sealed record RetirementStart(int Version, Guid Deployment, long Generation, string Inventory,
    ReadyProof Ready, string Request, RetirementRecipient[] Recipients);
internal sealed record RetirementCatalog(ManagedObjectStateRow[] Objects, RuntimePublicationReceipt[] Revocations,
    string? StartHash);
internal sealed record ObjectTransitionSnapshot(int Version, Guid Deployment, long Generation, string InputFingerprint,
    ManagedObjectStateRow[] Objects);
internal sealed record WorkloadRetirementInputs(string Fingerprint, long? PriorApplied, int MemberCount,
    WorkloadUpdatePublication[] ObsoletePublications);

/// <summary>Immutable update completion evidence. No current-rights inference from historical proof.</summary>
internal static partial class UpdateRetirementCompletion
{
    internal static WorkloadRetirementInputs Inputs(DeploymentGenerationRow source)
    {
        var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        if (graph.WorkloadUpdate is { } update)
            return new(update.Fingerprint, update.Document.BaselineGeneration, update.Document.Members.Length, update.Document.ObsoletePublications);
        var correction = graph.Correction ?? throw new InvalidDataException("Frozen Workload transition inventory required.");
        var document = correction.Document;
        return new(correction.Fingerprint, document.LastApplied, document.Members.Length,
            document.Publications.Where(x => x.Status == "Published").Select(x => new WorkloadUpdatePublication(x.Permission, x.Binding, x.Revision, x.Token,
                document.Bindings.Single(binding => binding.Id == x.Binding).Token, x.LastRequest, x.Receipt, x.ReceiptHash)).ToArray());
    }
    internal const string Started = "RetirementStarted";
    private const string ObjectTransitions = "ObjectTransitions";
    internal static string Key(DeploymentGenerationRow source, Guid permission) =>
        "retire-" + source.DeploymentId.ToString("N") + "-" + source.Generation + "-" + permission.ToString("N");

    internal static async Task<InitialCompletionProof> RequireProofAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        var inventory = Inputs(source);
        var row = await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Generation == source.Generation && x.Stage == InitialRetirementCompletion.Stage, token);
        if (row.FormatVersion != 1 || row.InputFingerprint != source.Fingerprint || row.EvidenceJson.Length > 2097152 ||
            row.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(row.EvidenceJson)) throw new InvalidDataException("Update completion corrupt.");
        var proof = JsonSerializer.Deserialize(row.EvidenceJson, InitialCompletionMetadata.Default.InitialCompletionProof)
            ?? throw new InvalidDataException("Update completion missing.");
        if (proof.Version != 1 || proof.Id != row.Id || proof.Deployment != source.DeploymentId || proof.Generation != source.Generation ||
            proof.Plan != source.PlanId || proof.ChangeSet != source.ChangeSetId || proof.InputFingerprint != source.Fingerprint ||
            proof.InventoryHash != inventory.Fingerprint || proof.PriorApplied != inventory.PriorApplied ||
            proof.At != row.ConfirmedAt || proof.FreshReady.Submitted != proof.Submitted ||
            proof.Recipients is null || !proof.Recipients.Select(x => x.Subject).SequenceEqual(InitialRetirementCompletion.CompletionTargets(source).Select(x => x.Subject)) ||
            proof.Recipients.Any(x => !x.Observation.Absent || x.Observation.Evidence is null))
            throw new InvalidDataException("Update completion origin mismatch.");
        await InitialRetirementCompletion.RequireResourcesAsync(db, source, proof.ResourcesReady, proof.ResourcesHash, token);
        await InitialRetirementCompletion.RequireCheckpointAsync(db, source, "WorkloadsSubmitted", proof.Submitted, proof.SubmittedHash, token);
        await InitialRetirementCompletion.RequireCheckpointAsync(db, source, "RolloutReady", proof.SavedReady, proof.SavedReadyHash, token);
        await InitialRetirementCompletion.RequireFreshSamplesAsync(db, source, proof.FreshReady, proof.Request, token);
        if (await InitialRetirementCompletion.SamplesHashAsync(db, proof.FreshReady, token) != proof.FreshSamplesHash ||
            await InitialRetirementCompletion.CatalogHashAsync(db, source, proof.Publications, token) != proof.CatalogHash)
            throw new InvalidDataException("Update completion prerequisites changed.");
        var request = await db.ObservationRecords.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Generation == source.Generation && x.Kind == "Request" && x.Request == proof.Request, token);
        var input = JsonSerializer.Deserialize(request.InputJson, RolloutMetadata.Default.RolloutRequest);
        var result = request.Payload is null ? null : RolloutJson.Decode(request.Payload);
        if (input?.Action != "Observe" || input.Actor != proof.Actor || request.FinishedAt is null || result is not { Ready: true, Applied: true } ||
            result.Retirement != proof.Id || result.Submitted != proof.Submitted || result.SavedReady != proof.SavedReady || result.AppliedGeneration != source.Generation)
            throw new InvalidDataException("Update completion request mismatch.");
        return proof;
    }

    internal static async Task<string> CatalogHashAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        if (DurableOperationGraphCodec.Decode(source.GraphJson).Correction is not null) return await CorrectiveCatalogHashAsync(db, source, token);
        var inventory = DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate!;
        var objects = await ReadOrdinaryObjectsAsync(db, source, token);
        return await CatalogHashCoreAsync(db, source, inventory, objects, token);
    }

    internal static async Task<ManagedObjectStateRow[]> FrozenCurrentObjectsAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        _ = await InitialRetirementCompletion.RequireProofAsync(db, source, token);
        var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        if (graph.Correction is not null) return (await ReadCorrectiveSnapshotAsync(db, source, token)).Updates;
        if (graph.WorkloadUpdate is null) return [];
        if (!await db.GenerationStageCheckpoints.AnyAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.Stage == ObjectTransitions, token))
            throw new InvalidDataException("Frozen baseline object projection required before successor effects.");
        return await ReadOrdinaryObjectsAsync(db, source, token);
    }

    private static async Task<ManagedObjectStateRow[]> ReadOrdinaryObjectsAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        var saved = await db.GenerationStageCheckpoints.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Generation == source.Generation && x.Stage == ObjectTransitions, token);
        ManagedObjectStateRow[] objects;
        if (saved is null) objects = await CurrentObjectsAsync(db, source, token); // Existing completion, before its first successor.
        else
        {
            if (saved.FormatVersion != 1 || saved.InputFingerprint != source.Fingerprint || saved.EvidenceJson.Length > 2097152 ||
                saved.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(saved.EvidenceJson)) throw new InvalidDataException("Object-transition checkpoint corrupt.");
            var snapshot = JsonSerializer.Deserialize(saved.EvidenceJson, UpdateCompletionMetadata.Default.ObjectTransitionSnapshot);
            if (snapshot is not { Version: 1 } || snapshot.Deployment != source.DeploymentId || snapshot.Generation != source.Generation || snapshot.InputFingerprint != source.Fingerprint || snapshot.Objects is null)
                throw new InvalidDataException("Object-transition checkpoint origin mismatch.");
            objects = snapshot.Objects;
        }
        await ValidateObjectsAsync(db, source, objects, token);
        return objects;
    }

    // Called with the owner row locked, in the same transaction as completion or
    // accepting its successor. Freeze actual projection rows, never synthesized receipts.
    internal static async Task CaptureObjectsAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, DateTimeOffset at, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null || !await db.ApplicationDeployments.AsNoTracking().AnyAsync(x =>
            x.Id == source.DeploymentId && x.DesiredGeneration == source.Generation, token))
            throw new InvalidDataException("Current locked object-transition owner required.");
        if (await db.GenerationStageCheckpoints.AnyAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.Stage == ObjectTransitions, token)) return;
        var objects = await CurrentObjectsAsync(db, source, token);
        await ValidateObjectsAsync(db, source, objects, token);
        var json = JsonSerializer.Serialize(new ObjectTransitionSnapshot(1, source.DeploymentId, source.Generation, source.Fingerprint, objects),
            UpdateCompletionMetadata.Default.ObjectTransitionSnapshot);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 2097152) throw new InvalidDataException("Object-transition checkpoint exceeds bound.");
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

    private static async Task<ManagedObjectStateRow[]> CurrentObjectsAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        if (await db.ManagedObjectStates.Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation)
            .SumAsync(x => (long)x.Payload.Length, token) > 2097152) throw new InvalidDataException("Current-object evidence exceeds bound.");
        return await db.ManagedObjectStates.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation)
            .OrderBy(x => x.ManagedWorkloadId).ThenBy(x => x.Role).Take(513).ToArrayAsync(token);
    }

    private static async Task ValidateObjectsAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, ManagedObjectStateRow[] objects, CancellationToken token)
    {
        var inventory = DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate!;
        if (objects.Length != inventory.Document.Members.Length || objects.Any(x => !inventory.Document.Members.Any(m => m.ManagedIdentity == x.ManagedWorkloadId &&
            x.OriginGeneration == inventory.Document.BaselineGeneration && x.OriginOperation == m.Operation)) ||
            objects.Select(x => (x.ManagedWorkloadId, x.Role)).Distinct().Count() != objects.Length ||
            objects.Any(x => x.DeploymentId != source.DeploymentId || x.Generation != source.Generation || x.Payload.Length > 1048576 || x.Format <= 0))
            throw new InvalidDataException("Complete update current-object evidence required.");
        foreach (var obj in objects)
        {
            var output = await db.OperationOutputs.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.OperationId == obj.OperationId, token);
            var intent = await db.ActionIntents.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.OperationId == obj.OperationId, token);
            var operation = await db.DurableOperations.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.OperationId == obj.OperationId, token);
            var attempt = await db.DurableOperationAttempts.AsNoTracking().SingleAsync(x => x.AttemptId == operation.CurrentAttemptId, token);
            if (string.IsNullOrWhiteSpace(obj.Role) || obj.Payload != output.Payload || obj.Contract != output.Contract || intent.OutputContract != output.Contract ||
                intent.ManagedIdentity != obj.ManagedWorkloadId || intent.InputFingerprint != source.Fingerprint || operation.State != "Succeeded" ||
                attempt.DeploymentId != source.DeploymentId || attempt.Generation != source.Generation || attempt.OperationId != obj.OperationId ||
                attempt.AttemptNumber != operation.AttemptNumber || attempt.FinishedAt != operation.FinalizedAt || attempt.DispatchedAt != operation.DispatchedAt ||
                attempt.FinishedAt is null || attempt.DispatchedAt is null || attempt.Outcome is not ("Succeeded" or "Uncertain"))
                throw new InvalidDataException("Current-object receipt mismatch.");
        }
    }

    private static async Task<string> CatalogHashCoreAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        WorkloadUpdateInventory inventory, ManagedObjectStateRow[] objects, CancellationToken token)
    {
        var evidence = await RetirementEvidenceAsync(db, source, Inputs(source), token);
        return ResourcesReadyCheckpointCodec.Hash(JsonSerializer.Serialize(new RetirementCatalog(objects, evidence.Revocations, evidence.StartHash),
            UpdateCompletionMetadata.Default.RetirementCatalog));
    }

    private static async Task<(RuntimePublicationReceipt[] Revocations, string? StartHash)> RetirementEvidenceAsync(ManifestCatalogDbContext db,
        DeploymentGenerationRow source, WorkloadRetirementInputs inventory, CancellationToken token)
    {
        var start = await db.GenerationStageCheckpoints.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Generation == source.Generation && x.Stage == Started, token);
        if (inventory.ObsoletePublications.Length > 0 && start is null || inventory.ObsoletePublications.Length == 0 && start is not null)
            throw new InvalidDataException("Retirement-start evidence mismatch.");
        if (start is not null)
        {
            var value = JsonSerializer.Deserialize(start.EvidenceJson, UpdateCompletionMetadata.Default.RetirementStart);
            if (start.FormatVersion != 1 || start.InputFingerprint != source.Fingerprint || start.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(start.EvidenceJson) ||
                value is not { Version: 1 } || value.Deployment != source.DeploymentId || value.Generation != source.Generation || value.Inventory != inventory.Fingerprint ||
                value.Recipients.Length != inventory.MemberCount || value.Recipients.Any(x => !x.Observation.Absent || x.Observation.Evidence is null) ||
                !value.Recipients.Select(x => x.Subject).SequenceEqual(InitialRetirementCompletion.CompletionTargets(source).Select(x => x.Subject)))
                throw new InvalidDataException("Retirement-start origin mismatch.");
            await InitialRetirementCompletion.RequireFreshSamplesAsync(db, source, value.Ready, value.Request, token);
        }
        var receipts = new List<RuntimePublicationReceipt>();
        foreach (var obsolete in inventory.ObsoletePublications.OrderBy(x => x.Permission))
        {
            var row = await db.RuntimePublicationReceipts.AsNoTracking().SingleAsync(x => x.RequestKey == Key(source, obsolete.Permission), token);
            var receipt = InternalRuntimePublication.ReadReceipt(row);
            var original = InternalRuntimePublication.ReadReceipt(await db.RuntimePublicationReceipts.AsNoTracking().SingleAsync(x => x.RequestKey == obsolete.Request, token));
            if (receipt.Request.Operation != RuntimePublicationOperation.RevokePermission || receipt.Request.Binding != obsolete.Binding || receipt.Request.Revision != obsolete.Revision ||
                receipt.Request.ExpectedToken != obsolete.PermissionToken || receipt.Outcome != RuntimePublicationOutcome.Revoked || receipt.Before != original.After ||
                receipt.After is not { Status: "Revoked" } || receipt.After.Id != obsolete.Permission || receipt.Binding.Token != obsolete.BindingToken ||
                original.Id != obsolete.Receipt || original.After?.Token != obsolete.PermissionToken ||
                obsolete.ReceiptHash != ResourcesReadyCheckpointCodec.Hash(RuntimePublicationCodec.Encode(original)))
                throw new InvalidDataException("Exact obsolete revocation proof mismatch.");
            receipts.Add(receipt);
        }
        return (receipts.ToArray(), start?.EvidenceFingerprint);
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(RetirementStart))]
[JsonSerializable(typeof(RetirementCatalog))]
[JsonSerializable(typeof(ObjectTransitionSnapshot))]
[JsonSerializable(typeof(CorrectiveObjectTransitionSnapshot))]
[JsonSerializable(typeof(CorrectiveRetirementCatalog))]
internal partial class UpdateCompletionMetadata : JsonSerializerContext;
