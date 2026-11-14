using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed record WorkloadConfigurationRecipient(ProviderInput.Workload Workload,
    ManifestLocalId Document, ConfigurationDocumentRevisionId Revision);
internal sealed record WorkloadConfigurationPreparation(IReadOnlyList<WorkloadConfigurationRecipient> Prepared,
    ConfigurationDocumentTarget? Failed = null, ConfigurationDocumentStatus? Failure = null, bool Cancelled = false);

/// <summary>Trusted friend-only composition. The caller supplies an accepted receipt, never document targets.</summary>
internal sealed class InternalWorkloadConfiguration(IDbContextFactory<ManifestCatalogDbContext> contexts,
    PostgreSqlDurableDeploymentPlanner planner, PostgreSqlConfigurationDocumentStore documents,
    IReadOnlyList<IInternalActionHandler> handlers, IEnumerable<IExternalOperationOutputContract> outputs,
    IEnumerable<IProviderConfigurationCodec> codecs, DurableActionAdmission admission)
{
    internal async ValueTask<WorkloadConfigurationPreparation> PrepareAsync(DurableChangeSetCommit accepted, string actor, CancellationToken token)
    {
        PostgreSqlDurableDeploymentPlanner.ValidateActor(actor);
        var authority = new Authority(this, accepted);
        IReadOnlyDictionary<ProviderInput.Workload, IReadOnlyList<ManifestLocalId>> selection;
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            var source = await InternalGenerationAuthority.RequireAsync(db, accepted, token);
            selection = await SelectAsync(db, source, token); // Validate ALL subjects before preparing any document.
            if (await DurableAggregateCompletion.ReadAsync(db, source, token, true) is null)
            {
                // Reuse the ordinary completion boundary, including bounded Resource validation.
                var metadata = await DurableAggregateCompletion.InspectMetadataAsync(db, source.DeploymentId, source.Generation,
                    outputs, codecs, token, admission, internalResourceStage: true);
                var prepared = await DurableAggregateCompletion.PrepareAsync(db, metadata, token)
                    ?? throw new InvalidDataException("Resource-stage evidence unavailable.");
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                await DurableAggregateCompletion.LockAsync(db, source.DeploymentId, token);
                source = await InternalGenerationAuthority.RequireAsync(db, accepted, token);
                if (await DurableAggregateCompletion.ReadAsync(db, source, token, true) is null &&
                    (await db.WorkloadConfigurationRecipients.AnyAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation, token) ||
                    await db.ConfigurationDocuments.AnyAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation, token) ||
                    !await DurableAggregateCompletion.TryAdvanceAsync(db, source.DeploymentId, source.Generation, token,
                        outputs, admission, codecs, prepared, internalResourceStage: true)))
                    throw new InvalidDataException("Resource-stage proof unavailable or missing after preparation.");
                token.ThrowIfCancellationRequested(); await transaction.CommitAsync(CancellationToken.None);
            }
        }
        var results = new List<WorkloadConfigurationRecipient>();
        var targets = selection.SelectMany(pair => pair.Value.Select(document =>
            new ConfigurationDocumentTarget(accepted.DeploymentId, accepted.Generation, pair.Key.Component, document)))
            .Distinct().OrderBy(x => x.Component.Value, StringComparer.Ordinal).ThenBy(x => x.Document.Value, StringComparer.Ordinal).ToArray();
        foreach (var target in targets)
        {
            try
            {
                var result = await documents.MaterializeInternalAsync(target, actor, authority, token);
                if (result.Revision is null) return new(results.ToArray(), target, result.Status);
                foreach (var recipient in selection.Where(x => x.Key.Component == target.Component && x.Value.Contains(target.Document)))
                    results.Add(new(recipient.Key, target.Document, result.Revision.Metadata.Id));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { return new(results.ToArray(), target, ConfigurationDocumentStatus.NotAttempted, true); }
            catch (InvalidDataException) { return new(results.ToArray(), target, ConfigurationDocumentStatus.InputCorrupt); }
            catch (IOException) { return new(results.ToArray(), target, ConfigurationDocumentStatus.StorageUnavailable); }
        }
        return new(results.ToArray());
    }

    // Read-only downstream authority: a partial PrepareAsync result never authorizes effects.
    internal async Task<ProviderInput.Workload[]> RequireCompleteAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        source = await InternalGenerationAuthority.RequireAsync(db, source, token);
        var selected = await SelectAsync(db, source, token);
        var proof = await DurableAggregateCompletion.ReadAsync(db, source, token, true)
            ?? throw new InvalidDataException("Resource-stage proof unavailable.");
        var metadata = await DurableAggregateCompletion.InspectMetadataAsync(db, source.DeploymentId, source.Generation,
            outputs, codecs, token, admission, internalResourceStage: true);
        if (metadata is null || !await DurableAggregateCompletion.CheckCredentialMetadataAsync(db, metadata, token))
            throw new InvalidDataException("Current Resource evidence metadata unavailable.");
        var pairs = selected.SelectMany(x => x.Value.Select(d => (Workload: x.Key, Document: d))).ToArray();
        var rows = await db.WorkloadConfigurationRecipients.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation).ToArrayAsync(token);
        if (rows.Length != pairs.Length) throw new InvalidDataException("Complete exact configuration preparation required.");
        foreach (var (workload, document) in pairs)
        {
            var row = rows.SingleOrDefault(x => x.Component == workload.Component.Value && x.Workload == workload.Id.Value && x.Document == document.Value)
                ?? throw new InvalidDataException("Exact prepared recipient unavailable.");
            var target = new ConfigurationDocumentTarget(ApplicationDeploymentId.Parse(source.DeploymentId.ToString()),
                DeploymentGeneration.Create(source.Generation), workload.Component, document);
            var revision = await PostgreSqlConfigurationDocumentStore.RequireStoredPreparationAsync(db, source, target, token);
            if (row.RevisionId.ToString() != revision.Metadata.Id.ToString() || row.CheckpointId != proof.Id || row.InputFingerprint != source.Fingerprint ||
                row.Actor != revision.Metadata.Actor || row.CreatedAt != revision.Metadata.CreatedAt.AddTicks(-(revision.Metadata.CreatedAt.Ticks % TimeSpan.TicksPerMicrosecond)))
                throw new InvalidDataException("Exact prepared artifact association mismatch.");
        }
        return selected.Where(x => x.Value.Count > 0).Select(x => x.Key).ToArray();
    }

    private async Task<IReadOnlyDictionary<ProviderInput.Workload, IReadOnlyList<ManifestLocalId>>> SelectAsync(
        ManifestCatalogDbContext db, DeploymentGenerationRow source, CancellationToken token)
    {
        var version = await db.Revisions.AsNoTracking().Where(x => x.ApplicationRecordId == source.ApplicationRecordId && x.LocalRevisionNumber == source.RevisionNumber)
            .Select(x => x.ApplicationVersion).SingleAsync(token);
        var selected = await planner.SelectInternalDocumentsAsync(db, source.Inputs(), version, handlers, token);
        var stored = db.WorkloadConfigurationRecipients.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation);
        if (await stored.CountAsync(token) > ConfigurationApplicationLimits.MaxItems)
            throw new InvalidDataException("Stored preparation coverage exceeds its bound.");
        var associations = await stored.ToArrayAsync(token);
        if (associations.Any(x => !selected.Any(pair => pair.Key.Component.Value == x.Component && pair.Key.Id.Value == x.Workload &&
                pair.Value.Any(document => document.Value == x.Document))))
            throw new InvalidDataException("Stored preparation contains an undeclared recipient or document.");
        return selected;
    }

    internal sealed class Authority(InternalWorkloadConfiguration owner, DurableChangeSetCommit accepted)
    {
        internal async Task<(ResourcesReadyCheckpoint Proof, ProviderInput.Workload[] Recipients)> RequireAsync(
            ManifestCatalogDbContext db, DeploymentGenerationRow source, ConfigurationDocumentTarget target, CancellationToken token)
        {
            var exact = await InternalGenerationAuthority.RequireAsync(db, accepted, token);
            if (source.DeploymentId != exact.DeploymentId || source.Generation != exact.Generation || source.Fingerprint != exact.Fingerprint ||
                target.Deployment != accepted.DeploymentId || target.Generation != accepted.Generation)
                throw new InvalidDataException("Preparation source mismatch.");
            var selected = await owner.SelectAsync(db, exact, token);
            var recipients = selected.Where(x => x.Key.Component == target.Component && x.Value.Contains(target.Document)).Select(x => x.Key).ToArray();
            if (recipients.Length == 0) throw new InvalidDataException("Document is not selected by this Component's Workloads.");
            var proof = await DurableAggregateCompletion.ReadAsync(db, exact, token, true)
                ?? throw new InvalidDataException("Resource-stage proof unavailable.");
            var metadata = await DurableAggregateCompletion.InspectMetadataAsync(db, exact.DeploymentId, exact.Generation,
                owner.Outputs, owner.Codecs, token, owner.Admission, internalResourceStage: true);
            if (metadata is null || !await DurableAggregateCompletion.CheckCredentialMetadataAsync(db, metadata, token))
                throw new InvalidDataException("Current Resource evidence metadata unavailable.");
            return (proof, recipients);
        }

        internal async Task AssociateAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
            ConfigurationDocumentRevision revision, CancellationToken token)
        {
            var (proof, recipients) = await RequireAsync(db, source, revision.Metadata.Target, token);
            var target = revision.Metadata.Target;
            if (revision.Metadata.Plan.ToString() != source.PlanId.ToString() ||
                revision.Metadata.Revision.ApplicationRecordId.ToString() != source.ApplicationRecordId.ToString() ||
                revision.Metadata.Revision.RevisionNumber.Value != source.RevisionNumber ||
                revision.Metadata.Environment.Value != source.EnvironmentKey ||
                revision.Metadata.Application != source.Inputs().Configuration!.Manifest.ApplicationId)
                throw new InvalidDataException("Stored preparation artifact source mismatch.");
            var old = await db.WorkloadConfigurationRecipients.Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation &&
                x.Component == target.Component.Value && x.Document == target.Document.Value).ToArrayAsync(token);
            if (old.Any(x => !recipients.Any(r => r.Id.Value == x.Workload) || x.RevisionId.ToString() != revision.Metadata.Id.ToString() ||
                x.CheckpointId != proof.Id || x.InputFingerprint != source.Fingerprint || x.Actor != revision.Metadata.Actor ||
                x.CreatedAt != revision.Metadata.CreatedAt.AddTicks(-(revision.Metadata.CreatedAt.Ticks % TimeSpan.TicksPerMicrosecond))))
                throw new InvalidDataException("Stored preparation association mismatch.");
            foreach (var recipient in recipients.Where(r => !old.Any(x => x.Workload == r.Id.Value)))
                db.WorkloadConfigurationRecipients.Add(new()
                {
                    DeploymentId = source.DeploymentId,
                    Generation = source.Generation,
                    Component = recipient.Component.Value,
                    Workload = recipient.Id.Value,
                    Document = target.Document.Value,
                    RevisionId = Guid.Parse(revision.Metadata.Id.ToString()),
                    CheckpointId = proof.Id,
                    InputFingerprint = source.Fingerprint,
                    Actor = revision.Metadata.Actor,
                    CreatedAt = revision.Metadata.CreatedAt
                });
        }
    }
    private IEnumerable<IExternalOperationOutputContract> Outputs => outputs;
    private IEnumerable<IProviderConfigurationCodec> Codecs => codecs;
    private DurableActionAdmission Admission => admission;
}
