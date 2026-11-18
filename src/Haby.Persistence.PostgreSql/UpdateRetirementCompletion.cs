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

/// <summary>Immutable update completion evidence. No current-rights inference from historical proof.</summary>
internal static class UpdateRetirementCompletion
{
    internal const string Started = "RetirementStarted";
    internal static string Key(DeploymentGenerationRow source, Guid permission) =>
        "retire-" + source.DeploymentId.ToString("N") + "-" + source.Generation + "-" + permission.ToString("N");

    internal static async Task<InitialCompletionProof> RequireProofAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        var inventory = DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate ?? throw new InvalidDataException("Frozen update required.");
        var row = await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Generation == source.Generation && x.Stage == InitialRetirementCompletion.Stage, token);
        if (row.FormatVersion != 1 || row.InputFingerprint != source.Fingerprint || row.EvidenceJson.Length > 2097152 ||
            row.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(row.EvidenceJson)) throw new InvalidDataException("Update completion corrupt.");
        var proof = JsonSerializer.Deserialize(row.EvidenceJson, InitialCompletionMetadata.Default.InitialCompletionProof)
            ?? throw new InvalidDataException("Update completion missing.");
        if (proof.Version != 1 || proof.Id != row.Id || proof.Deployment != source.DeploymentId || proof.Generation != source.Generation ||
            proof.Plan != source.PlanId || proof.ChangeSet != source.ChangeSetId || proof.InputFingerprint != source.Fingerprint ||
            proof.InventoryHash != inventory.Fingerprint || proof.PriorApplied != inventory.Document.BaselineGeneration ||
            proof.At != row.ConfirmedAt || proof.FreshReady.Submitted != proof.Submitted ||
            proof.Recipients is null || !proof.Recipients.Select(x => x.Subject).SequenceEqual(InitialRetirementCompletion.CompletionTargets(source).Select(x => x.Subject)) ||
            proof.Recipients.Any(x => !x.Observation.Absent || x.Observation.Evidence is null))
            throw new InvalidDataException("Update completion origin mismatch.");
        await InitialRetirementCompletion.RequireCheckpointAsync(db, source, "ResourcesReady", proof.ResourcesReady, proof.ResourcesHash, token);
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
        var inventory = DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate!;
        if (await db.ManagedObjectStates.Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation)
            .SumAsync(x => (long)x.Payload.Length, token) > 2097152) throw new InvalidDataException("Current-object evidence exceeds bound.");
        var objects = await db.ManagedObjectStates.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation)
            .OrderBy(x => x.ManagedWorkloadId).ThenBy(x => x.Role).Take(513).ToArrayAsync(token);
        if (objects.Length != inventory.Document.Members.Length || objects.Any(x => !inventory.Document.Members.Any(m => m.ManagedIdentity == x.ManagedWorkloadId &&
            x.OriginGeneration == inventory.Document.BaselineGeneration && x.OriginOperation == m.Operation)))
            throw new InvalidDataException("Complete update current-object evidence required.");
        foreach (var obj in objects)
        {
            var output = await db.OperationOutputs.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.OperationId == obj.OperationId, token);
            if (string.IsNullOrWhiteSpace(obj.Role) || obj.Payload != output.Payload || obj.Contract != output.Contract)
                throw new InvalidDataException("Current-object receipt mismatch.");
        }
        var start = await db.GenerationStageCheckpoints.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Generation == source.Generation && x.Stage == Started, token);
        if (inventory.Document.ObsoletePublications.Length > 0 && start is null || inventory.Document.ObsoletePublications.Length == 0 && start is not null)
            throw new InvalidDataException("Retirement-start evidence mismatch.");
        if (start is not null)
        {
            var value = JsonSerializer.Deserialize(start.EvidenceJson, UpdateCompletionMetadata.Default.RetirementStart);
            if (start.FormatVersion != 1 || start.InputFingerprint != source.Fingerprint || start.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(start.EvidenceJson) ||
                value is not { Version: 1 } || value.Deployment != source.DeploymentId || value.Generation != source.Generation || value.Inventory != inventory.Fingerprint ||
                value.Recipients.Length != inventory.Document.Members.Length || value.Recipients.Any(x => !x.Observation.Absent || x.Observation.Evidence is null) ||
                !value.Recipients.Select(x => x.Subject).SequenceEqual(InitialRetirementCompletion.CompletionTargets(source).Select(x => x.Subject)))
                throw new InvalidDataException("Retirement-start origin mismatch.");
            await InitialRetirementCompletion.RequireFreshSamplesAsync(db, source, value.Ready, value.Request, token);
        }
        var receipts = new List<RuntimePublicationReceipt>();
        foreach (var obsolete in inventory.Document.ObsoletePublications.OrderBy(x => x.Permission))
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
        return ResourcesReadyCheckpointCodec.Hash(JsonSerializer.Serialize(new RetirementCatalog(objects, receipts.ToArray(), start?.EvidenceFingerprint),
            UpdateCompletionMetadata.Default.RetirementCatalog));
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(RetirementStart))]
[JsonSerializable(typeof(RetirementCatalog))]
internal partial class UpdateCompletionMetadata : JsonSerializerContext;
