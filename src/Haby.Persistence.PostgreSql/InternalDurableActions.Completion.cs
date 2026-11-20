using System.Text.Json;
using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed partial class InternalDurableActions
{
    private async Task<RolloutResult> CompleteInitialAsync(ManifestCatalogDbContext db, ObservationCall call,
        SubmittedSet current, ReadyProof fresh, RolloutResult result, CancellationToken token)
    {
        var source = current.Source;
        var owner = await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == source.DeploymentId, token);
        if (owner.DesiredGeneration != source.Generation) return result with { CompletionReason = "generation-superseded" };
        await InitialRetirementAuthority.ValidateAsync(db, source, token);
        var inventory = DurableOperationGraphCodec.Decode(source.GraphJson).Retirement!;
        var selected = await (configurationPreparation ?? throw new InvalidDataException("Configuration authority required."))
            .SelectAsync(db, source, token);
        var targets = InitialRetirementAuthority.Targets(source.Inputs(), selected);
        if (JsonSerializer.Serialize(new InitialRetirementDocument(1, inventory.Document.Shape, inventory.Document.PriorApplied,
                inventory.Document.History, targets, [], []), InitialRetirementMetadata.Default.InitialRetirementDocument) !=
            JsonSerializer.Serialize(inventory.Document, InitialRetirementMetadata.Default.InitialRetirementDocument))
            return result with { CompletionReason = "inventory-conflict" };
        if (DurableOperationGraphCodec.Decode(source.GraphJson).Baseline is not null)
            await ResourceContinuity.LockAsync(db, source.DeploymentId, token);
        await configurationPreparation.RequireCompleteAsync(db, source, token, completedRead: true);
        try { foreach (var member in current.Members) await RequireCurrentRuntimeAsync(db, member.Loaded.Context, token); }
        catch (InvalidDataException) { return result with { CompletionReason = "current-authority-unavailable", AppliedGeneration = owner.AppliedGeneration }; }
        var publications = await InitialRetirementCompletion.PublicationsAsync(db, source, token);
        if (publications.Length != targets.Sum(x => x.Documents.Length)) return result with { CompletionReason = "prerequisite-incomplete" };
        var expectedActions = current.Members.Select(x => x.Loaded.Intent.OperationId).ToHashSet(StringComparer.Ordinal);
        var expectedObjects = new HashSet<(Guid, string)>();
        foreach (var member in current.Members)
        {
            if (member.Loaded.Handler.ObjectConfirmation(member.Loaded.Context, member.Loaded.Intent.Payload, member.Receipt) is { } obj)
                expectedObjects.Add((member.Loaded.Intent.ManagedIdentity, obj.Role));
            if (member.Loaded.Context.Runtime is not { } runtime) continue;
            var binding = await db.RuntimeIdentityBindings.AsNoTracking().SingleAsync(x => x.Id == runtime.Binding, token);
            await RequireIdentityOriginCoreAsync(db, binding, OriginPurpose.HistoricalRead, token);
            var identity = await db.OwnedObjects.AsNoTracking().SingleAsync(x => x.ManagedWorkloadId == binding.ManagedWorkloadId && x.Role == binding.Role, token);
            expectedActions.Add(identity.OperationId); expectedObjects.Add((identity.ManagedWorkloadId, identity.Role));
        }
        var actualActions = await db.ActionIntents.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId).Select(x => new { x.Generation, x.OperationId }).Take(1025).ToArrayAsync(token);
        var actualObjects = await db.OwnedObjects.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId).Select(x => new { x.ManagedWorkloadId, x.Role }).Take(1025).ToArrayAsync(token);
        var managed = await db.ManagedWorkloads.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId).Select(x => x.Id).Take(513).ToArrayAsync(token);
        if (actualActions.Length != expectedActions.Count || actualActions.Any(x => x.Generation != source.Generation || !expectedActions.Contains(x.OperationId)) ||
            actualObjects.Length != expectedObjects.Count || actualObjects.Any(x => !expectedObjects.Contains((x.ManagedWorkloadId, x.Role))) ||
            !managed.ToHashSet().SetEquals(current.Members.Select(x => x.Origin.Managed)))
            return result with { CompletionReason = "inventory-conflict" };
        var documents = await db.ConfigurationDocuments.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation)
            .Select(x => new { x.Component, x.Document }).Take(513).ToArrayAsync(token);
        var requiredDocuments = selected.SelectMany(x => x.Value.Select(d => (x.Key.Component.Value, d.Value))).ToHashSet();
        var bindings = await (from b in db.RuntimeIdentityBindings.AsNoTracking()
                              join w in db.ManagedWorkloads.AsNoTracking() on b.ManagedWorkloadId equals w.Id
                              where w.DeploymentId == source.DeploymentId
                              select b.Id).Take(513).ToArrayAsync(token);
        if (documents.Length != requiredDocuments.Count || documents.Any(x => !requiredDocuments.Contains((x.Component, x.Document))) ||
            !bindings.ToHashSet().SetEquals(current.Members.Where(x => x.Loaded.Context.Runtime is not null).Select(x => x.Loaded.Context.Runtime!.Binding)))
            return result with { CompletionReason = "inventory-conflict" };
        await InitialRetirementCompletion.RequireFreshSamplesAsync(db, source, fresh, call.Request.Request, token);
        call.LifecycleToken.ThrowIfCancellationRequested();
        if (owner.AppliedGeneration >= source.Generation)
        {
            var existing = await InitialRetirementCompletion.RequireProofAsync(db, source, token);
            return result with { Applied = true, Retirement = existing.Id, AppliedGeneration = owner.AppliedGeneration, CompletionReason = "applied" };
        }
        await RequireReuseTransitionAsync(db, source.DeploymentId, source.Generation, token);
        if (owner.AppliedGeneration != inventory.Document.PriorApplied) return result with { CompletionReason = "generation-superseded" };
        var resources = await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.Stage == "ResourcesReady", token);
        var submitted = await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.Id == current.Checkpoint, token);
        var ready = await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.Id == result.SavedReady, token);
        var id = Guid.CreateVersion7(); var at = Now();
        var proof = new InitialCompletionProof(1, id, source.DeploymentId, source.Generation, source.PlanId, source.ChangeSetId,
            source.Fingerprint, inventory.Fingerprint, owner.AppliedGeneration, resources.Id, resources.EvidenceFingerprint,
            submitted.Id, submitted.EvidenceFingerprint, ready.Id, ready.EvidenceFingerprint, fresh, call.Request.Request,
            JsonSerializer.Deserialize(call.Request.InputJson, RolloutMetadata.Default.RolloutRequest)!.Actor, at,
            await InitialRetirementCompletion.CatalogHashAsync(db, source, publications, token),
            await InitialRetirementCompletion.SamplesHashAsync(db, fresh, token), publications);
        var json = JsonSerializer.Serialize(proof, InitialCompletionMetadata.Default.InitialCompletionProof);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 2097152) return result with { CompletionReason = "inventory-conflict" };
        db.GenerationStageCheckpoints.Add(new()
        {
            Id = id,
            DeploymentId = source.DeploymentId,
            Generation = source.Generation,
            Stage = InitialRetirementCompletion.Stage,
            FormatVersion = 1,
            InputFingerprint = source.Fingerprint,
            EvidenceJson = json,
            EvidenceFingerprint = ResourcesReadyCheckpointCodec.Hash(json),
            ConfirmedAt = at
        });
        call.LifecycleToken.ThrowIfCancellationRequested();
        if (await db.ApplicationDeployments.Where(x => x.Id == source.DeploymentId && x.DesiredGeneration == source.Generation &&
                x.AppliedGeneration == owner.AppliedGeneration && x.ConcurrencyToken == owner.ConcurrencyToken)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AppliedGeneration, source.Generation).SetProperty(x => x.ConcurrencyToken, x => x.ConcurrencyToken + 1), token) != 1)
            throw new InvalidDataException("Completion compare-and-swap failed.");
        return result with { Applied = true, Retirement = id, AppliedGeneration = source.Generation, CompletionReason = "applied" };
    }
}
