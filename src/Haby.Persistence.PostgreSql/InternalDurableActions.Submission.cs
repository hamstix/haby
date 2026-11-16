using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed partial class InternalDurableActions
{
    internal async Task RequireConfirmedSubmissionAsync(ManifestCatalogDbContext db, DurableOperationKey key, CancellationToken token)
    {
        var loaded = await LoadAsync(db, key, token);
        if (loaded.Operation.State != nameof(DurableOperationState.Succeeded)) throw new InvalidDataException("Confirmed submission required.");
        _ = await TerminalAsync(db, loaded, token); // Immutable catalog-only confirmation, never current rights or live API.
    }

    internal async Task RequireSubmissionSupportAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        IReadOnlyDictionary<ProviderInput.Workload, IReadOnlyList<ManifestLocalId>> selection, string deploymentAction, string identityAction, CancellationToken token)
    {
        foreach (var pair in selection)
        {
            var exact = source.Inputs().Providers.Entries[pair.Key];
            var provider = Guid.Parse(exact.Instance.ToString());
            var instance = await db.ProviderInstances.AsNoTracking().SingleAsync(x => x.Id == provider, token);
            var revision = await db.ProviderConfigurationRevisions.AsNoTracking().SingleAsync(x => x.ProviderInstanceId == provider && x.RevisionNumber == exact.Configuration.Value, token);
            var subject = source.Inputs().Configuration!.Manifest.Spec.Components[pair.Key.Component].Workloads[pair.Key.Id].Type.Value;
            bool Matches(IInternalActionHandler handler) => handler.Contract.Subject == subject && handler.Contract.ProviderKind == instance.ProviderKindId &&
                handler.Contract.Configuration == revision.ConfigurationShapeId && handler.Contract.Implementation == exact.Implementation.Value;
            if (handlers.Count(h => Matches(h) && h is IInternalReadinessHandler && h.Contract.Action == deploymentAction) != 1 ||
                pair.Value.Count > 0 && handlers.Count(h => Matches(h) && h.EstablishesIdentity && h.Contract.Action == identityAction) != 1)
                throw new InvalidDataException("Exact submission support unavailable.");
        }
    }

    internal async Task<WorkloadSubmissionProgress> ReadSubmissionAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        InternalWorkloadConfiguration preparation, CancellationToken token)
    {
        var set = await InspectSubmittedAsync(db, source, false, token);
        var selection = await preparation.SelectAsync(db, source, token);
        var owner = await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == source.DeploymentId, token);
        var resources = await DurableAggregateCompletion.ReadAsync(db, source, token, true);
        var rows = await db.WorkloadConfigurationRecipients.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation).ToArrayAsync(token);
        var members = new List<WorkloadSubmissionMember>();
        var deploymentActions = handlers.Where(h => h is IInternalReadinessHandler).Select(h => h.Contract.Action).ToArray();
        foreach (var pair in selection.OrderBy(x => x.Key.Component.Value, StringComparer.Ordinal).ThenBy(x => x.Key.Id.Value, StringComparer.Ordinal))
        {
            var subject = pair.Key;
            var documents = rows.Where(x => x.Component == subject.Component.Value && x.Workload == subject.Id.Value).ToArray();
            foreach (var document in documents)
            {
                if (resources is null || document.CheckpointId != resources.Id || document.InputFingerprint != source.Fingerprint ||
                    !pair.Value.Any(x => x.Value == document.Document)) throw new InvalidDataException("Prepared recipient proof mismatch.");
                var saved = await PostgreSqlConfigurationDocumentStore.RequireStoredPreparationAsync(db, source,
                    new(ApplicationDeploymentId.Parse(source.DeploymentId.ToString()), DeploymentGeneration.Create(source.Generation), subject.Component,
                        ManifestLocalId.Create(document.Document)), token);
                if (saved.Metadata.Id.ToString() != document.RevisionId.ToString() || saved.Metadata.Actor != document.Actor ||
                    saved.Metadata.CreatedAt.AddTicks(-(saved.Metadata.CreatedAt.Ticks % TimeSpan.TicksPerMicrosecond)) != document.CreatedAt)
                    throw new InvalidDataException("Prepared document proof mismatch.");
            }
            var managed = await db.ManagedWorkloads.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId && x.Component == subject.Component.Value && x.Workload == subject.Id.Value, token);
            var binding = managed is null ? null : await db.RuntimeIdentityBindings.AsNoTracking().SingleOrDefaultAsync(x => x.ManagedWorkloadId == managed.Id, token);
            var identity = "notAttempted"; var publication = "notAttempted"; var authority = "notApplicable";
            if (pair.Value.Count == 0) identity = publication = "notApplicable";
            else if (binding is not null)
            {
                await RequireIdentityOriginAsync(db, binding, token);
                identity = "confirmed"; authority = InternalRuntimePublication.BindingState(binding).Status == "Prepared" ? "available" : "revoked";
                var permissions = await db.RuntimeConfigurationPermissions.AsNoTracking().Where(x => x.BindingId == binding.Id).ToArrayAsync(token);
                var exact = documents.Select(x => x.RevisionId).ToArray();
                var published = 0;
                foreach (var document in exact)
                {
                    var permission = permissions.SingleOrDefault(x => x.RevisionId == document);
                    if (permission is null) { authority = authority == "revoked" ? authority : "unavailable"; continue; }
                    _ = InternalRuntimePublication.PermissionState(permission);
                    // Original publication proof and current permission are deliberately separate.
                    var origin = RuntimePublicationCodec.Origin(permission.OriginPayload);
                    var receipt = await db.RuntimePublicationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.RequestKey == InternalRuntimePublicationPreparation.Key(origin, document), token);
                    if (receipt is null) throw new InvalidDataException("Publication origin unavailable.");
                    var original = InternalRuntimePublication.ReadReceipt(receipt);
                    if (original.Outcome != RuntimePublicationOutcome.Published || original.Request.Origin != origin || original.Request.Binding != binding.Id || original.Request.Revision != document ||
                        origin.Deployment != source.DeploymentId || origin.Generation != source.Generation || origin.ManagedWorkload != managed!.Id ||
                        origin.Component != subject.Component.Value || origin.Workload != subject.Id.Value || origin.Checkpoint != resources!.Id)
                        throw new InvalidDataException("Publication origin mismatch.");
                    published++;
                    if (permission.Status == "Revoked") authority = "revoked";
                }
                publication = published == pair.Value.Count ? "published" : published > 0 ? "partial" : "notAttempted";
            }
            else if (pair.Value.Count > 0)
            {
                authority = "unavailable";
                var identityActions = handlers.Where(h => h.EstablishesIdentity).Select(h => h.Contract.Action).ToArray();
                var account = await db.ActionIntents.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation &&
                    x.Component == subject.Component.Value && x.LocalId == subject.Id.Value && identityActions.Contains(x.ActionContract), token);
                if (account is not null)
                {
                    var operation = await db.DurableOperations.AsNoTracking().SingleAsync(x => x.DeploymentId == account.DeploymentId && x.Generation == account.Generation && x.OperationId == account.OperationId, token);
                    identity = operation.State is "Pending" or "Preparing" ? "pending" : "uncertain";
                }
            }
            var confirmed = set.Members.SingleOrDefault(x => x.Subject == Logical(subject));
            var intent = await db.ActionIntents.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation &&
                x.Component == subject.Component.Value && x.LocalId == subject.Id.Value && deploymentActions.Contains(x.ActionContract), token);
            var state = intent is null ? null : await db.DurableOperations.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && x.OperationId == intent.OperationId, token);
            members.Add(new(subject.Component.Value, subject.Id.Value,
                pair.Value.Count == 0 ? "notApplicable" : documents.Length == pair.Value.Count ? "prepared" : documents.Length > 0 ? "partial" : "notAttempted",
                identity, publication, confirmed is not null ? "confirmed" : state is null ? "notAttempted" : state.State == "Pending" || state.State == "Preparing" ? "pending" : "uncertain", authority, intent?.OperationId));
        }
        var evidence = await db.ActionIntents.AsNoTracking().AnyAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation, token) || rows.Length > 0;
        return new(source.DeploymentId.ToString(), source.Generation, owner.DesiredGeneration, owner.AppliedGeneration,
            set.Checkpoint is not null ? "submitted" : evidence ? "partial" : "notSubmitted", resources?.Id, set.Checkpoint, members);
    }
}
