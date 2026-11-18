using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed record CompletionPublication(Guid Permission, Guid Binding, Guid Revision, Guid PermissionToken,
    Guid BindingToken, string Request, Guid Receipt, string ReceiptHash);
internal sealed record InitialCompletionProof(int Version, Guid Id, Guid Deployment, long Generation, Guid Plan,
    Guid ChangeSet, string InputFingerprint, string InventoryHash, long? PriorApplied, Guid ResourcesReady,
    string ResourcesHash, Guid Submitted, string SubmittedHash, Guid SavedReady, string SavedReadyHash,
    ReadyProof FreshReady, string Request, string Actor, DateTimeOffset At, string CatalogHash, string FreshSamplesHash,
    CompletionPublication[] Publications, RetirementRecipient[]? Recipients = null);

// Closed, catalog-only proof validation. It never calls an action loader or generation read
// authority: completed authentication/observation readers may safely depend on it.
internal static class InitialRetirementCompletion
{
    internal static InitialRetirementTarget[] CompletionTargets(DeploymentGenerationRow source)
    {
        var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        if (graph.Retirement is { } initial) return initial.Document.Targets;
        var update = graph.WorkloadUpdate?.Document ?? throw new InvalidDataException("Frozen completion inventory required.");
        var inputs = source.Inputs();
        return update.Members.Select(member =>
        {
            var subject = new ProviderInput.Workload(Haby.Domain.ManifestLocalId.Create(member.Component), Haby.Domain.ManifestLocalId.Create(member.Workload));
            var exact = inputs.Providers.Entries[subject];
            return new InitialRetirementTarget("Workload/" + member.Component + "/" + member.Workload,
                inputs.Configuration!.Manifest.Spec.Components[subject.Component].Workloads[subject.Id].Type.Value,
                Guid.Parse(exact.Instance.ToString()), exact.Configuration.Value,
                ResourcesReadyCheckpointCodec.Hash(InternalPlacementInput.Decode(exact.Placement!).Declaration.Payload),
                member.PrepareDocuments, member.PrepareDocuments.Length > 0);
        }).OrderBy(x => x.Subject, StringComparer.Ordinal).ToArray();
    }

    internal const string Stage = "RetirementCompleted";
    internal static async Task<InitialCompletionProof> RequireProofAsync(ManifestCatalogDbContext db,
        DeploymentGenerationRow source, CancellationToken token)
    {
        if (DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate is not null)
            return await UpdateRetirementCompletion.RequireProofAsync(db, source, token);
        await InitialRetirementAuthority.ValidateAsync(db, source, token);
        var row = await db.GenerationStageCheckpoints.AsNoTracking().SingleOrDefaultAsync(x =>
            x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.Stage == Stage, token)
            ?? throw new InvalidDataException("Completion proof unavailable.");
        if (row.FormatVersion != 1 || row.InputFingerprint != source.Fingerprint || Encoding.UTF8.GetByteCount(row.EvidenceJson) > 2097152 ||
            row.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(row.EvidenceJson)) throw new InvalidDataException("Completion proof corrupt.");
        var proof = JsonSerializer.Deserialize(row.EvidenceJson, InitialCompletionMetadata.Default.InitialCompletionProof)
            ?? throw new InvalidDataException("Completion proof unavailable.");
        var inventory = DurableOperationGraphCodec.Decode(source.GraphJson).Retirement!;
        if (proof.Version != 1 || proof.Id != row.Id || proof.Deployment != source.DeploymentId || proof.Generation != source.Generation ||
            proof.Plan != source.PlanId || proof.ChangeSet != source.ChangeSetId || proof.InputFingerprint != source.Fingerprint ||
            proof.InventoryHash != inventory.Fingerprint || proof.PriorApplied != inventory.Document.PriorApplied || proof.At != row.ConfirmedAt || proof.FreshReady.Submitted != proof.Submitted ||
            proof.Publications is not { Length: <= 512 } || proof.Publications.Select(x => x.Permission).Distinct().Count() != proof.Publications.Length)
            throw new InvalidDataException("Completion proof origin mismatch.");
        await RequireCheckpointAsync(db, source, "ResourcesReady", proof.ResourcesReady, proof.ResourcesHash, token);
        await RequireCheckpointAsync(db, source, "WorkloadsSubmitted", proof.Submitted, proof.SubmittedHash, token);
        await RequireCheckpointAsync(db, source, "RolloutReady", proof.SavedReady, proof.SavedReadyHash, token);
        await RequireFreshSamplesAsync(db, source, proof.FreshReady, proof.Request, token);
        if (await SamplesHashAsync(db, proof.FreshReady, token) != proof.FreshSamplesHash) throw new InvalidDataException("Completion readiness evidence changed.");
        if (await CatalogHashAsync(db, source, proof.Publications, token) != proof.CatalogHash)
            throw new InvalidDataException("Completion prerequisite evidence mismatch.");
        var request = await db.ObservationRecords.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Generation == source.Generation && x.Request == proof.Request && x.Kind == "Request", token);
        if (request?.Payload is null || request.FinishedAt is null) throw new InvalidDataException("Completion request unavailable.");
        var original = JsonSerializer.Deserialize(request.InputJson, RolloutMetadata.Default.RolloutRequest);
        var result = RolloutJson.Decode(request.Payload);
        if (original?.Action != "Observe" || original.Actor != proof.Actor || !result.Ready || !result.Applied || result.Retirement != proof.Id || result.AppliedGeneration != source.Generation || result.SavedReady != proof.SavedReady || result.Submitted != proof.Submitted)
            throw new InvalidDataException("Completion request outcome mismatch.");
        return proof;
    }

    internal static async Task<string> SamplesHashAsync(ManifestCatalogDbContext db, ReadyProof proof, CancellationToken token)
    {
        var ids = proof.Members.Select(x => x.Observation).ToArray();
        var attempts = proof.Members.Select(x => x.Attempt).ToArray();
        if (await db.ObservationRecords.AsNoTracking().AnyAsync(x => ids.Contains(x.Id) && x.Payload != null && x.Payload.Length > 16384, token))
            throw new InvalidDataException("Readiness sample exceeds bound.");
        var samples = await db.ObservationRecords.AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id).Take(513).ToArrayAsync(token);
        var origins = await db.ObservationAttempts.AsNoTracking().Where(x => attempts.Contains(x.Id)).OrderBy(x => x.Id).Take(513).ToArrayAsync(token);
        if (samples.Length != ids.Length || origins.Length != attempts.Length || samples.Any(x => x.Payload?.Length > 16384)) throw new InvalidDataException("Readiness sample coverage mismatch.");
        return ResourcesReadyCheckpointCodec.Hash(JsonSerializer.Serialize(new CompletedSamplesEvidence(samples, origins), InitialCompletionMetadata.Default.CompletedSamplesEvidence));
    }

    internal static async Task RequireFreshSamplesAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        ReadyProof proof, string request, CancellationToken token)
    {
        var targets = CompletionTargets(source);
        if (proof.Format != 1 || proof.Stage != "RolloutReady" || proof.Deployment != source.DeploymentId || proof.Generation != source.Generation ||
            proof.InputFingerprint != source.Fingerprint || proof.Members.Length != targets.Length ||
            !proof.Members.Select(x => x.Origin.Subject).SequenceEqual(targets.Select(x => x.Subject)))
            throw new InvalidDataException("Fresh complete-set readiness required.");
        int? round = null;
        foreach (var member in proof.Members)
        {
            var sample = await db.ObservationRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == member.Observation, token);
            var attempt = await db.ObservationAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == member.Attempt, token);
            if (sample is null || attempt is null || sample.AttemptId != member.Attempt || sample.Kind != "Read" || sample.Payload is null ||
                sample.FinishedAt is null || sample.Request != request || sample.DeploymentId != source.DeploymentId || sample.Generation != source.Generation ||
                sample.Subject != member.Origin.Subject || attempt.Subject != member.Origin.Subject || attempt.DeploymentId != source.DeploymentId ||
                attempt.Generation != source.Generation || attempt.OriginJson != RolloutJson.Encode(member.Origin) || round is not null && sample.Ordinal != round ||
                RolloutJson.Observation(sample.Payload).Outcome != RolloutOutcome.Ready)
                throw new InvalidDataException("Fresh readiness sample origin mismatch.");
            round = sample.Ordinal;
        }
    }

    internal static async Task<GenerationStageCheckpointRow> RequireCheckpointAsync(ManifestCatalogDbContext db,
        DeploymentGenerationRow source, string stage, Guid id, string hash, CancellationToken token)
    {
        var row = await db.GenerationStageCheckpoints.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Generation == source.Generation && x.Stage == stage, token);
        if (row is null || row.Id != id || row.InputFingerprint != source.Fingerprint || row.FormatVersion != 1 ||
            row.EvidenceFingerprint != hash || hash != ResourcesReadyCheckpointCodec.Hash(row.EvidenceJson))
            throw new InvalidDataException("Completion checkpoint reference mismatch.");
        return row;
    }

    internal static async Task<CompletionPublication[]> PublicationsAsync(ManifestCatalogDbContext db,
        DeploymentGenerationRow source, CancellationToken token)
    {
        var bindings = await (from b in db.RuntimeIdentityBindings.AsNoTracking()
                              join w in db.ManagedWorkloads.AsNoTracking()
                              on b.ManagedWorkloadId equals w.Id
                              where w.DeploymentId == source.DeploymentId
                              select b).Take(513).ToArrayAsync(token);
        if (bindings.Length > 512) throw new InvalidDataException("Binding bound exceeded.");
        var ids = bindings.Select(x => x.Id).ToArray();
        var permissions = await db.RuntimeConfigurationPermissions.AsNoTracking().Where(x => ids.Contains(x.BindingId) && db.ConfigurationDocuments.Any(d => d.Id == x.RevisionId && d.DeploymentId == source.DeploymentId && d.Generation == source.Generation)).OrderBy(x => x.Id).Take(513).ToArrayAsync(token);
        if (permissions.Length > 512) throw new InvalidDataException("Publication bound exceeded.");
        var result = new List<CompletionPublication>();
        foreach (var permission in permissions)
        {
            var binding = bindings.Single(x => x.Id == permission.BindingId);
            var row = await db.RuntimePublicationReceipts.AsNoTracking().SingleAsync(x => x.RequestKey == permission.LastRequest, token);
            var receipt = InternalRuntimePublication.ReadReceipt(row);
            if (binding.Status != "Prepared" || permission.Status != "Published" || receipt.Outcome != RuntimePublicationOutcome.Published ||
                receipt.After != InternalRuntimePublication.PermissionState(permission) || receipt.Request.Origin is not { } origin ||
                origin.Deployment != source.DeploymentId || origin.Generation != source.Generation || origin.Plan != source.PlanId ||
                origin.Fingerprint != source.Fingerprint || receipt.Binding != InternalRuntimePublication.BindingState(binding))
                throw new InvalidDataException("Current publication prerequisite unavailable.");
            result.Add(new(permission.Id, binding.Id, permission.RevisionId, permission.ConcurrencyToken, binding.ConcurrencyToken,
                row.RequestKey, row.Id, ResourcesReadyCheckpointCodec.Hash(row.Payload)));
        }
        return result.ToArray();
    }

    internal static async Task<string> CatalogHashAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        CompletionPublication[] publications, CancellationToken token)
    {
        var deployment = source.DeploymentId;
        var generation = source.Generation;
        var characters = await db.ActionIntents.Where(x => x.DeploymentId == deployment && x.Generation == generation).SumAsync(x => (long)x.Payload.Length, token) +
            await db.OwnedObjects.Where(x => x.DeploymentId == deployment).SumAsync(x => (long)x.Payload.Length, token) +
            await db.OperationOutputs.Where(x => x.DeploymentId == deployment && x.Generation == generation).SumAsync(x => (long)x.Payload.Length, token) +
            await db.ManagedWorkloads.Where(x => x.DeploymentId == deployment).SumAsync(x => (long)x.TargetPayload.Length, token) +
            await (from b in db.RuntimeIdentityBindings
                   join w in db.ManagedWorkloads on b.ManagedWorkloadId equals w.Id
                   where w.DeploymentId == deployment
                   select (long)b.ValidationPayload.Length).SumAsync(token);
        if (characters > 2097152) throw new InvalidDataException("Completion prerequisite text exceeds bound.");
        // Completion proves this immutable generation. Accepted successor actions do
        // not rewrite or invalidate its original proof; current rights are separate.
        var intents = await db.ActionIntents.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == generation).OrderBy(x => x.OperationId).Take(1025).ToArrayAsync(token);
        if (intents.Length > 1024 || intents.Any(x => x.Generation != generation || x.SubjectKind != "Workload" || x.InputFingerprint != source.Fingerprint || x.Payload.Length > 1048576))
            throw new InvalidDataException("Unexpected action inventory.");
        var actions = new List<CompletedActionEvidence>();
        foreach (var intent in intents)
        {
            var operation = await db.DurableOperations.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment && x.Generation == generation && x.OperationId == intent.OperationId, token);
            var attempt = await db.DurableOperationAttempts.AsNoTracking().SingleAsync(x => x.AttemptId == operation.CurrentAttemptId, token);
            var confirmation = await db.ActionObservations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == generation && x.OperationId == intent.OperationId && x.Confirmed, token);
            var output = await db.OperationOutputs.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment && x.Generation == generation && x.OperationId == intent.OperationId, token);
            if (operation.State != nameof(DurableOperationState.Succeeded) || output.Payload.Length > 1048576) throw new InvalidDataException("Complete action evidence required.");
            actions.Add(new(intent, operation, attempt, confirmation, output));
        }
        var managed = await db.ManagedWorkloads.AsNoTracking().Where(x => x.EnvironmentId == source.EnvironmentRecordId && x.ApplicationId == source.ApplicationRecordId)
            .OrderBy(x => x.Id).Take(513).ToArrayAsync(token);
        if (managed.Length > 512 || managed.Any(x => x.DeploymentId != deployment)) throw new InvalidDataException("Managed inventory bound/scope mismatch.");
        var owned = await db.OwnedObjects.AsNoTracking().Where(x => x.DeploymentId == deployment).OrderBy(x => x.ManagedWorkloadId).ThenBy(x => x.Role).Take(1025).ToArrayAsync(token);
        var recipients = await db.WorkloadConfigurationRecipients.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == generation)
            .OrderBy(x => x.Component).ThenBy(x => x.Workload).ThenBy(x => x.Document).Take(513).ToArrayAsync(token);
        var documents = await db.ConfigurationDocuments.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == generation)
            .OrderBy(x => x.Id).Select(x => new CompletedDocumentEvidence(x.Id, x.DeploymentId, x.Generation, x.Component, x.Document, x.Fingerprint)).Take(513).ToArrayAsync(token);
        if (owned.Length > 1024 || recipients.Length > 512 || documents.Length > 512 || owned.Any(x => (DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate is null && x.Generation != generation) || x.Payload.Length > 1048576)) throw new InvalidDataException("Preparation inventory bound exceeded.");
        var managedIds = managed.Select(x => x.Id).ToArray();
        var bindings = await db.RuntimeIdentityBindings.AsNoTracking().Where(x => managedIds.Contains(x.ManagedWorkloadId)).OrderBy(x => x.Id)
            .Select(x => new CompletedBindingEvidence(x.Id, x.InstallationId, x.EnvironmentId, x.ManagedWorkloadId, x.ProviderId, x.ConfigurationRevision,
                x.Authenticator, x.Audience, x.Role, x.Contract, x.Format, x.ValidationPayload)).Take(513).ToArrayAsync(token);
        if (bindings.Length > 512) throw new InvalidDataException("Binding bound exceeded.");
        var targets = CompletionTargets(source);
        var required = targets.SelectMany(x => x.Documents.Select(document => (x.Subject, Document: document))).ToHashSet();
        if (publications.Length != required.Count || publications.Select(x => x.Permission).Distinct().Count() != publications.Length ||
            publications.Select(x => (x.Binding, x.Revision)).Distinct().Count() != publications.Length ||
            publications.Select(x => x.Receipt).Distinct().Count() != publications.Length ||
            publications.Select(x => x.Request).Distinct(StringComparer.Ordinal).Count() != publications.Length)
            throw new InvalidDataException("Completion publication coverage mismatch.");
        foreach (var publication in publications)
        {
            var permission = await db.RuntimeConfigurationPermissions.AsNoTracking().SingleAsync(x => x.Id == publication.Permission, token);
            var row = await db.RuntimePublicationReceipts.AsNoTracking().SingleAsync(x => x.RequestKey == publication.Request, token);
            var receipt = InternalRuntimePublication.ReadReceipt(row);
            var origin = receipt.Request.Origin ?? throw new InvalidDataException("Publication origin required.");
            var subject = "Workload/" + origin.Component + "/" + origin.Workload;
            var target = targets.SingleOrDefault(x => x.Subject == subject);
            var binding = bindings.SingleOrDefault(x => x.Id == publication.Binding);
            var workload = binding is null ? null : managed.SingleOrDefault(x => x.Id == binding.Managed);
            var recipient = recipients.SingleOrDefault(x => x.Component == origin.Component && x.Workload == origin.Workload && x.Document == origin.Document);
            if (!required.Remove((subject, origin.Document)) || target is null || binding is null || workload is null || recipient is null ||
                !target.RuntimeIdentity || target.Provider != binding.Provider || target.Configuration != binding.Configuration ||
                workload.Component != origin.Component || workload.Workload != origin.Workload || workload.Id != origin.ManagedWorkload ||
                origin.Deployment != source.DeploymentId || origin.Generation != source.Generation || origin.Plan != source.PlanId ||
                origin.ChangeSet != source.ChangeSetId || origin.Fingerprint != source.Fingerprint || origin.Environment != source.EnvironmentRecordId ||
                origin.Application != source.ApplicationRecordId || origin.CommittedAt != source.AcceptedAt || origin.CommitActor != source.ActorId ||
                recipient.RevisionId != publication.Revision || recipient.CheckpointId != origin.Checkpoint || recipient.InputFingerprint != source.Fingerprint ||
                receipt.Outcome != RuntimePublicationOutcome.Published || receipt.Request.Operation != RuntimePublicationOperation.PublishPermission ||
                receipt.After?.Id != publication.Permission || receipt.After.Binding != publication.Binding || receipt.After.Revision != publication.Revision ||
                receipt.After.Status != "Published" || receipt.After.LastRequest != publication.Request || receipt.Binding.Id != publication.Binding || receipt.Binding.Status != "Prepared" ||
                row.Id != publication.Receipt || ResourcesReadyCheckpointCodec.Hash(row.Payload) != publication.ReceiptHash ||
                permission.BindingId != publication.Binding || permission.RevisionId != publication.Revision || receipt.Request.Binding != publication.Binding ||
                receipt.Request.Revision != publication.Revision || receipt.After?.Token != publication.PermissionToken || receipt.Binding.Token != publication.BindingToken ||
                receipt.Request.Origin != RuntimePublicationCodec.Origin(permission.OriginPayload))
                throw new InvalidDataException("Retained publication evidence mismatch.");
        }
        if (required.Count != 0) throw new InvalidDataException("Completion publication coverage mismatch.");
        var evidence = new CompletedCatalogEvidence(actions.ToArray(), managed, owned, bindings, recipients,
            documents, publications.OrderBy(x => x.Permission).ToArray());
        var json = JsonSerializer.Serialize(evidence, InitialCompletionMetadata.Default.CompletedCatalogEvidence);
        if (Encoding.UTF8.GetByteCount(json) > 16777216) throw new InvalidDataException("Completion evidence exceeds bound.");
        return ResourcesReadyCheckpointCodec.Hash(DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate is null ? json :
            json + await UpdateRetirementCompletion.CatalogHashAsync(db, source, token));
    }
}

internal sealed record CompletedSamplesEvidence(GenerationObservationRecordRow[] Samples, GenerationObservationAttemptRow[] Attempts);
internal sealed record CompletedActionEvidence(DurableActionIntentRow Intent, DurableOperationRow Operation,
    DurableOperationAttemptRow Attempt, DurableActionObservationRow? Confirmation, OperationOutputRow Output);
internal sealed record CompletedBindingEvidence(Guid Id, Guid Installation, Guid Environment, Guid Managed, Guid Provider,
    long Configuration, string Authenticator, string Audience, string Role, string Contract, int Format, string Payload);
internal sealed record CompletedCatalogEvidence(CompletedActionEvidence[] Actions, ManagedWorkloadRow[] Managed,
    OwnedObjectRow[] Objects, CompletedBindingEvidence[] Bindings, WorkloadConfigurationRecipientRow[] Recipients, CompletedDocumentEvidence[] Documents, CompletionPublication[] Publications);
internal sealed record CompletedDocumentEvidence(Guid Id, Guid Deployment, long Generation, string Component, string Document, string Fingerprint);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(CompletedSamplesEvidence))]
[JsonSerializable(typeof(InitialCompletionProof))]
[JsonSerializable(typeof(CompletedCatalogEvidence))]
internal partial class InitialCompletionMetadata : JsonSerializerContext;
