using System.Text.Json;
using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed partial class InternalDurableActions
{
    /// <summary>Internal-only completion. Never reachable from public wait or submission composition.</summary>
    internal async ValueTask<RolloutResult> WaitUpdateAsync(DurableChangeSetCommit accepted, string request, string actor,
        RolloutOptions options, RecipientObservationOptions recipients, CancellationToken token)
    {
        options.Validate(); recipients.Validate(); PostgreSqlDurableDeploymentPlanner.ValidateActor(actor);
        _ = OperationId.Create(request);
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            var source = await InternalGenerationAuthority.RequireStageReadAsync(db, accepted, token);
            if (DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate is null) throw new InvalidDataException("Internal UPDATE completion requires an update inventory.");
        }
        var started = clock.GetTimestamp();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromMilliseconds(options.RequestMilliseconds));
        return await serialization.RunAsync(RolloutKey(accepted), () => ObserveRolloutCoreAsync(accepted, request, options,
            started, token, bounded.Token, actor, updateWait: true, recipientOptions: recipients), bounded.Token);
    }

    private void Trace(UpdateCompletionStage stage, UpdateCompletionPredicate predicate = UpdateCompletionPredicate.Unknown) =>
        UpdateCompletionDiagnostics.Emit(updateDiagnostic, stage, predicate);

    private async Task<RolloutResult> CompleteUpdateGuardedAsync(ObservationCall call, RolloutResult result,
        RolloutOptions rollout, RecipientObservationOptions options, CancellationToken token)
    {
        try { return await CompleteUpdateAsync(call, result, rollout, options, token); }
        catch (InvalidDataException error)
        {
            Trace(UpdateCompletionStage.PrerequisiteIncomplete, UpdateCompletionDiagnostics.Predicate(error));
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await DurableAggregateCompletion.LockAsync(db, call.Set.Source.DeploymentId, token);
            var owner = await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == call.Set.Source.DeploymentId, token);
            result = result with { AppliedGeneration = owner.AppliedGeneration, CompletionReason = "prerequisite-incomplete" };
            Trace(UpdateCompletionStage.CloseRequest);
            await CloseRequestAsync(db, call.Request, result, token); token.ThrowIfCancellationRequested();
            await tx.CommitAsync(CancellationToken.None); return result;
        }
    }

    private async Task<RolloutResult> CompleteUpdateAsync(ObservationCall call, RolloutResult result,
        RolloutOptions rollout, RecipientObservationOptions options, CancellationToken token)
    {
        Trace(UpdateCompletionStage.Begin);
        var source = call.Set.Source;
        var inventory = DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate ?? throw new InvalidDataException("UPDATE inventory required.");
        var observations = new List<RetirementRecipient>();
        var remainingCalls = options.MaximumCalls;
        long retainedEvidenceBytes = 0;
        Trace(UpdateCompletionStage.Recipients);
        foreach (var member in call.Set.Members)
        {
            if (member.Loaded.Handler is not IInternalRecipientObserver observer) return await CloseUpdateAsync(result with { CompletionReason = "recipient-evidence-incomplete" });
            if (observer.MaximumObservationCalls < 1 || observer.MaximumObservationCalls > remainingCalls)
                return await CloseUpdateAsync(result with { CompletionReason = "recipient-evidence-incomplete" });
            remainingCalls -= observer.MaximumObservationCalls;
            using var read = CancellationTokenSource.CreateLinkedTokenSource(token);
            read.CancelAfter(TimeSpan.FromMilliseconds(rollout.ReadTimeoutMilliseconds));
            RecipientObservation observed;
            try { observed = await observer.ObserveRecipientsAsync(member.Loaded.Context, member.Loaded.Intent.Payload, member.Receipt, options, read.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { return await CloseUpdateAsync(result with { CompletionReason = "recipient-evidence-incomplete" }); }
            if (!observed.Absent || !observer.ValidateRecipients(member.Loaded.Intent.Payload, member.Receipt, observed))
                return await CloseUpdateAsync(result with { CompletionReason = observed.Code == "old-recipients-present" ? "old-recipients-present" : "recipient-evidence-incomplete" });
            if (observed.Evidence is null) return await CloseUpdateAsync(result with { CompletionReason = "recipient-evidence-incomplete" });
            retainedEvidenceBytes += System.Text.Encoding.UTF8.GetByteCount(observed.Evidence.Json);
            if (retainedEvidenceBytes > 2097152) return await CloseUpdateAsync(result with { CompletionReason = "recipient-evidence-incomplete" });
            observations.Add(new(member.Subject, observed));
        }
        ReadyProof fresh;
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await DurableAggregateCompletion.LockAsync(db, source.DeploymentId, token);
            var current = await RequireUpdateClosureAsync(db, call, token);
            Trace(UpdateCompletionStage.FreshSamples);
            fresh = await FreshUpdateProofAsync(db, call, current, token);
            var owner = await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == source.DeploymentId, token);
            if (owner.AppliedGeneration == source.Generation)
            {
                var proof = await InitialRetirementCompletion.RequireProofAsync(db, source, token);
                result = result with { Applied = true, Retirement = proof.Id, AppliedGeneration = source.Generation, CompletionReason = "applied" };
                Trace(UpdateCompletionStage.CloseRequest);
                await CloseRequestAsync(db, call.Request, result, token); await db.SaveChangesAsync(token); token.ThrowIfCancellationRequested();
                await tx.CommitAsync(CancellationToken.None); return result;
            }
            if (inventory.Document.ObsoletePublications.Length > 0 && !await db.GenerationStageCheckpoints.AnyAsync(x => x.DeploymentId == source.DeploymentId &&
                x.Generation == source.Generation && x.Stage == UpdateRetirementCompletion.Started, token))
            {
                var start = new RetirementStart(1, source.DeploymentId, source.Generation, inventory.Fingerprint, fresh, call.Request.Request, observations.ToArray());
                Trace(UpdateCompletionStage.RetirementStart);
                await CheckpointAsync(db, source, UpdateRetirementCompletion.Started,
                    JsonSerializer.Serialize(start, UpdateCompletionMetadata.Default.RetirementStart), token);
                await db.SaveChangesAsync(token); token.ThrowIfCancellationRequested(); await tx.CommitAsync(CancellationToken.None);
            }
        }
        if (inventory.Document.ObsoletePublications.Length > 0) fault?.Invoke(InternalActionFault.AfterRetirementStart);
        foreach (var obsolete in inventory.Document.ObsoletePublications)
        {
            token.ThrowIfCancellationRequested();
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await DurableAggregateCompletion.LockAsync(db, source.DeploymentId, token);
            _ = await RequireUpdateClosureAsync(db, call, token);
            Trace(UpdateCompletionStage.Revoke);
            await RetirePermissionAsync(db, source, obsolete, JsonSerializer.Deserialize(call.Request.InputJson, RolloutMetadata.Default.RolloutRequest)!.Actor, token);
            await db.SaveChangesAsync(token); token.ThrowIfCancellationRequested(); await tx.CommitAsync(CancellationToken.None);
            fault?.Invoke(InternalActionFault.AfterRetirementPermission);
        }
        fault?.Invoke(InternalActionFault.BeforeUpdateApplied);
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await DurableAggregateCompletion.LockAsync(db, source.DeploymentId, token);
            var current = await RequireUpdateClosureAsync(db, call, token);
            Trace(UpdateCompletionStage.FreshSamples);
            fresh = await FreshUpdateProofAsync(db, call, current, token);
            var owner = await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == source.DeploymentId, token);
            if (owner.AppliedGeneration == source.Generation)
            {
                var existing = await InitialRetirementCompletion.RequireProofAsync(db, source, token);
                result = result with { Applied = true, Retirement = existing.Id, AppliedGeneration = source.Generation, CompletionReason = "applied" };
                Trace(UpdateCompletionStage.CloseRequest);
                await CloseRequestAsync(db, call.Request, result, token); await db.SaveChangesAsync(token); token.ThrowIfCancellationRequested();
                await tx.CommitAsync(CancellationToken.None); return result;
            }
            if (owner.AppliedGeneration != inventory.Document.BaselineGeneration) throw new InvalidDataException("Update expected Applied changed.");
            var resources = await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.Stage == "ResourcesReady", token);
            var submitted = await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.Id == current.Checkpoint, token);
            var ready = await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.Id == result.SavedReady, token);
            Trace(UpdateCompletionStage.Publications);
            var publications = await InitialRetirementCompletion.PublicationsAsync(db, source, token);
            var proof = new InitialCompletionProof(1, Guid.CreateVersion7(), source.DeploymentId, source.Generation, source.PlanId, source.ChangeSetId,
                source.Fingerprint, inventory.Fingerprint, inventory.Document.BaselineGeneration, resources.Id, resources.EvidenceFingerprint,
                submitted.Id, submitted.EvidenceFingerprint, ready.Id, ready.EvidenceFingerprint, fresh, call.Request.Request,
                JsonSerializer.Deserialize(call.Request.InputJson, RolloutMetadata.Default.RolloutRequest)!.Actor, Now(),
                await CatalogHash(), await SampleHash(), publications, observations.ToArray());
            async Task<string> CatalogHash()
            {
                Trace(UpdateCompletionStage.CatalogHash);
                return await InitialRetirementCompletion.CatalogHashAsync(db, source, publications, token);
            }
            async Task<string> SampleHash()
            {
                Trace(UpdateCompletionStage.SamplesHash);
                return await InitialRetirementCompletion.SamplesHashAsync(db, fresh, token);
            }
            var json = JsonSerializer.Serialize(proof, InitialCompletionMetadata.Default.InitialCompletionProof);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 2097152) throw new InvalidDataException("Update completion exceeds checkpoint bound.");
            db.GenerationStageCheckpoints.Add(new()
            {
                Id = proof.Id,
                DeploymentId = source.DeploymentId,
                Generation = source.Generation,
                Stage = InitialRetirementCompletion.Stage,
                FormatVersion = 1,
                InputFingerprint = source.Fingerprint,
                EvidenceJson = json,
                EvidenceFingerprint = ResourcesReadyCheckpointCodec.Hash(json),
                ConfirmedAt = proof.At
            });
            token.ThrowIfCancellationRequested();
            Trace(UpdateCompletionStage.AppliedCas);
            if (await db.ApplicationDeployments.Where(x => x.Id == source.DeploymentId && x.DesiredGeneration == source.Generation &&
                x.AppliedGeneration == inventory.Document.BaselineGeneration && x.ConcurrencyToken == owner.ConcurrencyToken)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AppliedGeneration, source.Generation).SetProperty(x => x.ConcurrencyToken, x => x.ConcurrencyToken + 1), token) != 1)
                throw new InvalidDataException("Update Applied compare-and-swap failed.");
            result = result with { Applied = true, Retirement = proof.Id, AppliedGeneration = source.Generation, CompletionReason = "applied" };
            Trace(UpdateCompletionStage.CloseRequest);
            await CloseRequestAsync(db, call.Request, result, token); await db.SaveChangesAsync(token); token.ThrowIfCancellationRequested();
            await tx.CommitAsync(CancellationToken.None);
        }
        fault?.Invoke(InternalActionFault.AfterUpdateApplied); return result;

        async Task<RolloutResult> CloseUpdateAsync(RolloutResult unfinished)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await DurableAggregateCompletion.LockAsync(db, source.DeploymentId, token);
            var owner = await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == source.DeploymentId, token);
            unfinished = unfinished with { AppliedGeneration = owner.AppliedGeneration };
            Trace(UpdateCompletionStage.CloseRequest);
            await CloseRequestAsync(db, call.Request, unfinished, token); token.ThrowIfCancellationRequested();
            await tx.CommitAsync(CancellationToken.None); return unfinished;
        }
    }

    private async Task<SubmittedSet> RequireUpdateClosureAsync(ManifestCatalogDbContext db, ObservationCall call, CancellationToken token)
    {
        Trace(UpdateCompletionStage.ClosureSource);
        var source = await InternalGenerationAuthority.RequireStageReadAsync(db, call.Set.Source, token);
        var inventory = DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate?.Document ?? throw new InvalidDataException("Update inventory missing.");
        var owner = await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == source.DeploymentId, token);
        if (owner.DesiredGeneration != source.Generation || owner.AppliedGeneration != inventory.BaselineGeneration && owner.AppliedGeneration != source.Generation)
            throw new InvalidDataException("Update generation superseded.");
        Trace(UpdateCompletionStage.CaptureSubmitted);
        var set = await CaptureSubmittedAsync(db, source, token, completedRead: true);
        if (set.Members.Length != inventory.Members.Length || set.Members.Any(x => call.Set.Members.Single(m => m.Subject == x.Subject).Attempt.Id != x.Attempt.Id))
            throw new InvalidDataException("Update attempts changed.");
        Trace(UpdateCompletionStage.ObsoleteProgress);
        foreach (var obsolete in inventory.ObsoletePublications)
        {
            var permission = await db.RuntimeConfigurationPermissions.AsNoTracking().SingleAsync(x => x.Id == obsolete.Permission, token);
            var binding = await db.RuntimeIdentityBindings.AsNoTracking().SingleAsync(x => x.Id == obsolete.Binding, token);
            var original = await db.RuntimePublicationReceipts.AsNoTracking().SingleAsync(x => x.RequestKey == obsolete.Request, token);
            var published = InternalRuntimePublication.ReadReceipt(original);
            if (permission.BindingId != obsolete.Binding || permission.RevisionId != obsolete.Revision || binding.Status != "Prepared" || binding.ConcurrencyToken != obsolete.BindingToken ||
                published.Id != obsolete.Receipt || published.Outcome != RuntimePublicationOutcome.Published || published.After?.Token != obsolete.PermissionToken ||
                ResourcesReadyCheckpointCodec.Hash(original.Payload) != obsolete.ReceiptHash) throw new InvalidDataException("Obsolete inventory changed.");
            var retired = await db.RuntimePublicationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.RequestKey == UpdateRetirementCompletion.Key(source, obsolete.Permission), token);
            if (retired is null)
            {
                if (published.After != InternalRuntimePublication.PermissionState(permission)) throw new InvalidDataException("Unretired publication changed.");
            }
            else
            {
                var progress = InternalRuntimePublication.ReadReceipt(retired);
                if (progress.Before != published.After || progress.Outcome != RuntimePublicationOutcome.Revoked ||
                    progress.Request.Operation != RuntimePublicationOperation.RevokePermission || progress.Request.ExpectedToken != obsolete.PermissionToken ||
                    progress.Request.Binding != obsolete.Binding || progress.Request.Revision != obsolete.Revision ||
                    progress.After != InternalRuntimePublication.PermissionState(permission) || permission.Status != "Revoked")
                    throw new InvalidDataException("Retirement progress changed.");
            }
        }
        Trace(UpdateCompletionStage.RetirementMarker);
        var marker = await db.GenerationStageCheckpoints.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Generation == source.Generation && x.Stage == UpdateRetirementCompletion.Started, token);
        if (marker is not null)
        {
            var start = JsonSerializer.Deserialize(marker.EvidenceJson, UpdateCompletionMetadata.Default.RetirementStart);
            if (marker.FormatVersion != 1 || marker.InputFingerprint != source.Fingerprint || marker.EvidenceFingerprint != ResourcesReadyCheckpointCodec.Hash(marker.EvidenceJson) ||
                start is not { Version: 1 } || start.Deployment != source.DeploymentId || start.Generation != source.Generation ||
                start.Inventory != DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate!.Fingerprint ||
                !start.Recipients.Select(x => x.Subject).SequenceEqual(set.Targets)) throw new InvalidDataException("Retirement-start mismatch.");
            await InitialRetirementCompletion.RequireFreshSamplesAsync(db, source, start.Ready, start.Request, token);
            foreach (var recipient in start.Recipients)
            {
                var member = set.Members.Single(x => x.Subject == recipient.Subject);
                if (!recipient.Observation.Absent || member.Loaded.Handler is not IInternalRecipientObserver observer ||
                    !observer.ValidateRecipients(member.Loaded.Intent.Payload, member.Receipt, recipient.Observation))
                    throw new InvalidDataException("Retirement-start recipient evidence invalid.");
            }
        }
        Trace(UpdateCompletionStage.CurrentRuntime);
        foreach (var member in set.Members) await RequireCurrentRuntimeAsync(db, member.Loaded.Context, token, observation: true);
        Trace(UpdateCompletionStage.ConfigurationComplete);
        await (configurationPreparation ?? throw new InvalidDataException("Configuration authority required.")).RequireCompleteAsync(db, source, token, completedRead: true);
        return set;
    }

    private static async Task<ReadyProof> FreshUpdateProofAsync(ManifestCatalogDbContext db, ObservationCall call, SubmittedSet set, CancellationToken token)
    {
        var samples = await db.ObservationRecords.AsNoTracking().Where(x => x.DeploymentId == set.Source.DeploymentId && x.Generation == set.Source.Generation &&
            x.Request == call.Request.Request && x.Kind == "Read" && x.Payload != null).OrderByDescending(x => x.Ordinal).Take(513).ToArrayAsync(token);
        var round = samples.Max(x => x.Ordinal);
        var members = set.Members.Select(member => new ReadyMember(member.Origin, member.Attempt.Id,
            samples.Single(x => x.Subject == member.Subject && x.Ordinal == round && x.AttemptId == member.Attempt.Id).Id)).ToArray();
        var proof = new ReadyProof(1, "RolloutReady", set.Source.DeploymentId, set.Source.Generation, set.Source.Fingerprint, set.Checkpoint!.Value, members);
        await InitialRetirementCompletion.RequireFreshSamplesAsync(db, set.Source, proof, call.Request.Request, token); return proof;
    }

    private async Task RetirePermissionAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        WorkloadUpdatePublication obsolete, string actor, CancellationToken token)
    {
        var key = UpdateRetirementCompletion.Key(source, obsolete.Permission);
        if (!await db.GenerationStageCheckpoints.AnyAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.Stage == UpdateRetirementCompletion.Started, token))
            throw new InvalidDataException("Retirement not started.");
        var permission = await db.RuntimeConfigurationPermissions.SingleAsync(x => x.Id == obsolete.Permission, token);
        var binding = await db.RuntimeIdentityBindings.AsNoTracking().SingleAsync(x => x.Id == obsolete.Binding, token);
        if (permission.BindingId != obsolete.Binding || permission.RevisionId != obsolete.Revision ||
            InternalRuntimePublication.BindingState(binding).Status != "Prepared" || binding.ConcurrencyToken != obsolete.BindingToken)
            throw new InvalidDataException("Frozen obsolete permission mismatch.");
        // Current/other active accepted preparations are checked under the same aggregate lock as preparation acceptance.
        if (await (from r in db.WorkloadConfigurationRecipients
                   join owner in db.ApplicationDeployments on r.DeploymentId equals owner.Id
                   where r.RevisionId == obsolete.Revision && r.Generation <= owner.DesiredGeneration &&
                       (r.Generation > (owner.AppliedGeneration ?? 0) || r.DeploymentId != source.DeploymentId && r.Generation == owner.AppliedGeneration)
                   select r).AnyAsync(token)) throw new InvalidDataException("Obsolete publication has an active live reference.");
        var saved = await db.RuntimePublicationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.RequestKey == key, token);
        if (saved is not null)
        {
            var previous = InternalRuntimePublication.ReadReceipt(saved);
            if (previous.Request.Operation != RuntimePublicationOperation.RevokePermission || previous.Request.Binding != obsolete.Binding ||
                previous.Request.Revision != obsolete.Revision || previous.Request.ExpectedToken != obsolete.PermissionToken ||
                previous.Outcome != RuntimePublicationOutcome.Revoked || previous.After != InternalRuntimePublication.PermissionState(permission))
                throw new InvalidDataException("Retirement progress mismatch.");
            return;
        }
        var originalRow = await db.RuntimePublicationReceipts.AsNoTracking().SingleAsync(x => x.RequestKey == obsolete.Request, token);
        var original = InternalRuntimePublication.ReadReceipt(originalRow);
        if (permission.Status != "Published" || permission.ConcurrencyToken != obsolete.PermissionToken ||
            original.Id != obsolete.Receipt || original.Outcome != RuntimePublicationOutcome.Published || original.After != InternalRuntimePublication.PermissionState(permission) ||
            ResourcesReadyCheckpointCodec.Hash(originalRow.Payload) != obsolete.ReceiptHash) throw new InvalidDataException("Current obsolete permission no longer matches inventory.");
        var before = InternalRuntimePublication.PermissionState(permission);
        permission.Status = "Revoked"; permission.ConcurrencyToken = Guid.CreateVersion7(); permission.ChangedAt = Now(); permission.ChangedBy = actor; permission.LastRequest = key;
        var intent = new RuntimePublicationRequest(key, RuntimePublicationOperation.RevokePermission, obsolete.Binding, obsolete.Revision, obsolete.PermissionToken, actor);
        var receipt = new RuntimePublicationReceipt(Guid.CreateVersion7(), intent, RuntimePublicationOutcome.Revoked, permission.ChangedAt, before,
            InternalRuntimePublication.PermissionState(permission), InternalRuntimePublication.BindingState(binding));
        db.RuntimePublicationReceipts.Add(new() { Id = receipt.Id, RequestKey = key, BindingId = obsolete.Binding, RevisionId = obsolete.Revision, Payload = RuntimePublicationCodec.Encode(receipt) });
    }
}
