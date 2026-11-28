using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed partial class InternalDurableActions
{
    // Reconstruct immutable preparation from catalog-owned evidence; never accept caller UUIDs as authority.
    private async Task<InternalActionContext> RuntimeContextAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        InternalActionContext context, IInternalActionHandler handler, CancellationToken token, bool completedRead = false)
    {
        if (!handler.RequiresRuntimePublication || handler.PreparationDocuments(context).Count == 0) return context;
        var recipients = await (configurationPreparation ?? throw new InvalidDataException("Configuration authority unavailable."))
            .RequireRecipientsAsync(db, source, token, completedRead);
        var subject = (ProviderInput.Workload)context.Subject;
        var selected = recipients.Where(x => x.Workload == subject).ToArray();
        var managed = await db.ManagedWorkloads.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId &&
            x.Component == subject.Component.Value && x.Workload == subject.Id.Value, token) ?? throw new InvalidDataException("Prepared managed Workload required.");
        var binding = await db.RuntimeIdentityBindings.AsNoTracking().SingleOrDefaultAsync(x => x.ManagedWorkloadId == managed.Id, token)
            ?? throw new InvalidDataException("Prepared runtime binding required.");
        InternalRuntimePublication.ValidateBinding(binding);
        if (completedRead) await RequireIdentityOriginCoreAsync(db, binding, OriginPurpose.HistoricalRead, token);
        else await RequireIdentityOriginAsync(db, binding, token);
        if (binding.EnvironmentId != source.EnvironmentRecordId || binding.ProviderId.ToString() != context.Provider.Instance.ToString() ||
            binding.ConfigurationRevision != context.Provider.Configuration.Value || binding.InstallationId != context.InstallationId)
            throw new InvalidDataException("Prepared binding scope mismatch.");
        var identity = await db.OwnedObjects.AsNoTracking().SingleAsync(x => x.ManagedWorkloadId == managed.Id && x.Role == binding.Role, token);
        var checkpoint = await DurableAggregateCompletion.ReadAsync(db, source, token, true, completedRead) ?? throw new InvalidDataException("Resource proof required.");
        foreach (var recipient in selected)
        {
            var revision = Guid.Parse(recipient.Revision.ToString());
            var origin = new RuntimePublicationOrigin(source.DeploymentId, source.Generation, source.PlanId, source.ChangeSetId, source.AcceptedAt,
                source.ActorId, source.EnvironmentRecordId, source.ApplicationRecordId, managed.Id, subject.Component.Value, subject.Id.Value,
                recipient.Document.Value, source.Fingerprint, checkpoint.Id, identity.Generation, identity.OperationId);
            var key = InternalRuntimePublicationPreparation.Key(origin, revision);
            var original = await db.RuntimePublicationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.RequestKey == key, token)
                ?? throw new InvalidDataException("Original publication receipt required.");
            var receipt = InternalRuntimePublication.ReadReceipt(original);
            if (receipt.Request.Operation != RuntimePublicationOperation.PublishPermission || receipt.Request.Binding != binding.Id || receipt.Request.Revision != revision ||
                receipt.Request.Origin != origin || receipt.Outcome != RuntimePublicationOutcome.Published || receipt.After is not { Status: "Published" })
                throw new InvalidDataException("Original publication proof mismatch.");
        }
        return context with
        {
            ManagedIdentity = managed.Id,
            Runtime = new(binding.Id, binding.Audience, identity.Contract, identity.Payload,
            selected.ToDictionary(x => x.Document, x => x.Revision))
        };
    }

    // Current authority for new effects and fresh public readiness reads, under the deployment lock.
    // Immutable history, replay and internal post-dispatch observation do not require current permission.
    private static async Task RequireCurrentRuntimeAsync(ManifestCatalogDbContext db, InternalActionContext context, CancellationToken token, bool observation = false)
    {
        var owner = Guid.Parse(context.Key.DeploymentId.ToString());
        if (!await db.ApplicationDeployments.AsNoTracking().AnyAsync(x => x.Id == owner && x.DesiredGeneration == context.Key.Generation.Value, token))
            throw new InvalidDataException("Superseded generation cannot authorize runtime effects or fresh readiness.");
        if (context.Update is not null && !observation)
        {
            var deployment = Guid.Parse(context.Key.DeploymentId.ToString());
            var source = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment && x.Generation == context.Key.Generation.Value, token);
            await InternalGenerationAuthority.RequireCurrentStageAsync(db, source, token);
            if (context.Update.Correction is not null) await RequireCorrectiveProjectionAsync(db, source, context, token);
            else await RequireOrdinaryProjectionAsync(db, source, context, token);
        }
        if (context.Runtime is not { } prepared) return;
        var binding = await db.RuntimeIdentityBindings.AsNoTracking().SingleAsync(x => x.Id == prepared.Binding, token);
        if (InternalRuntimePublication.BindingState(binding).Status != "Prepared") throw new InvalidDataException("Runtime binding revoked.");
        foreach (var revision in prepared.Documents.Values)
        {
            var id = Guid.Parse(revision.ToString());
            var row = await db.RuntimeConfigurationPermissions.AsNoTracking().SingleOrDefaultAsync(x => x.BindingId == binding.Id && x.RevisionId == id, token);
            if (InternalRuntimePublication.PermissionState(row)?.Status != "Published") throw new InvalidDataException("Required runtime permission unavailable.");
            var receipt = InternalRuntimePublication.ReadReceipt(await db.RuntimePublicationReceipts.AsNoTracking().SingleAsync(x => x.RequestKey == row!.LastRequest, token));
            var origin = RuntimePublicationCodec.Origin(row!.OriginPayload);
            var original = InternalRuntimePublication.ReadReceipt(await db.RuntimePublicationReceipts.AsNoTracking()
                .SingleAsync(x => x.RequestKey == InternalRuntimePublicationPreparation.Key(origin, id), token));
            if (receipt.Outcome != RuntimePublicationOutcome.Published || receipt.After != InternalRuntimePublication.PermissionState(row) ||
                receipt.Request.Binding != binding.Id || receipt.Request.Revision != id || receipt.Request.Origin != origin || original.Request.Origin != origin ||
                origin.Deployment.ToString() != context.Key.DeploymentId.ToString() || origin.Generation != context.Key.Generation.Value ||
                origin.ManagedWorkload != context.ManagedIdentity || origin.Environment != binding.EnvironmentId ||
                context.Subject is not ProviderInput.Workload subject || origin.Component != subject.Component.Value || origin.Workload != subject.Id.Value ||
                !prepared.Documents.Any(x => x.Key.Value == origin.Document && x.Value == revision)) throw new InvalidDataException("Current runtime permission proof mismatch.");
        }
    }

    private static async Task AcceptObjectAsync(ManifestCatalogDbContext db, Loaded loaded, string receipt, CancellationToken token)
    {
        var value = loaded.Handler.ObjectConfirmation(loaded.Context, loaded.Intent.Payload, receipt);
        if (value is null) return;
        Bound(value.Contract, 128); Bound(value.Role, 128); Bound(value.Payload, 1048576);
        if (loaded.Handler.UpdatesObject)
        {
            var prior = loaded.Context.Update ?? throw new InvalidDataException("Original update authority required.");
            if (value.Format <= 0) throw new InvalidDataException("Current object transition conflict.");
            var source = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == loaded.Intent.DeploymentId && x.Generation == loaded.Intent.Generation, token);
            if (prior.Correction is not null) await RequireCorrectiveProjectionAsync(db, source, loaded.Context, token);
            else await RequireOrdinaryProjectionAsync(db, source, loaded.Context, token);
            var current = await db.ManagedObjectStates.SingleOrDefaultAsync(x => x.ManagedWorkloadId == loaded.Intent.ManagedIdentity && x.Role == value.Role, token);
            if (current is null && await db.ManagedObjectStates.AnyAsync(x => x.ManagedWorkloadId == loaded.Intent.ManagedIdentity, token))
                throw new InvalidDataException("Confirmed object role cannot change.");
            if (current is not null)
            {
                current.OriginGeneration = prior.Original.Key.Generation.Value;
                current.OriginOperation = prior.Original.Key.OperationId.Value;
                current.Generation = loaded.Intent.Generation; current.OperationId = loaded.Intent.OperationId;
                current.Contract = value.Contract; current.Format = value.Format; current.Payload = value.Payload;
                return;
            }
            db.ManagedObjectStates.Add(new()
            {
                ManagedWorkloadId = loaded.Intent.ManagedIdentity,
                Role = value.Role,
                DeploymentId = loaded.Intent.DeploymentId,
                OriginGeneration = prior.Original.Key.Generation.Value,
                OriginOperation = prior.Original.Key.OperationId.Value,
                Generation = loaded.Intent.Generation,
                OperationId = loaded.Intent.OperationId,
                Contract = value.Contract,
                Format = value.Format,
                Payload = value.Payload
            });
            return;
        }
        if (value.Format <= 0 || await db.OwnedObjects.AnyAsync(x => x.ManagedWorkloadId == loaded.Intent.ManagedIdentity && x.Role == value.Role, token))
            throw new InvalidDataException("Owned object confirmation conflict.");
        db.OwnedObjects.Add(new()
        {
            ManagedWorkloadId = loaded.Intent.ManagedIdentity,
            Role = value.Role,
            Contract = value.Contract,
            Format = value.Format,
            Payload = value.Payload,
            DeploymentId = loaded.Intent.DeploymentId,
            Generation = loaded.Intent.Generation,
            OperationId = loaded.Intent.OperationId
        });
    }

    private static async Task RequireOrdinaryProjectionAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        InternalActionContext context, CancellationToken token)
    {
        var update = DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate?.Document ?? throw new InvalidDataException("Applied UPDATE baseline required.");
        var baseline = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == update.BaselineGeneration, token);
        var frozen = (await UpdateRetirementCompletion.FrozenCurrentObjectsAsync(db, baseline, token)).Where(x => x.ManagedWorkloadId == context.ManagedIdentity)
            .OrderBy(x => x.Role, StringComparer.Ordinal).ToArray();
        var current = await db.ManagedObjectStates.AsNoTracking().Where(x => x.ManagedWorkloadId == context.ManagedIdentity).OrderBy(x => x.Role).Take(2).ToArrayAsync(token);
        if (frozen.Length > 1 || current.Length != frozen.Length || !current.Select(Projection).SequenceEqual(frozen.Select(Projection)))
            throw new InvalidDataException("Applied baseline current-object projection changed.");
    }

    private static (Guid, string, Guid, long, string, long, string, string, int, string) Projection(ManagedObjectStateRow row) =>
        (row.ManagedWorkloadId, row.Role, row.DeploymentId, row.OriginGeneration, row.OriginOperation, row.Generation, row.OperationId, row.Contract, row.Format, row.Payload);

    private static async Task RequireCorrectiveProjectionAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        InternalActionContext context, CancellationToken token)
    {
        var origin = context.Update?.Correction ?? throw new InvalidDataException("Corrective UPDATE origin required.");
        var inventory = DurableOperationGraphCodec.Decode(source.GraphJson).Correction ?? throw new InvalidDataException("Accepted corrective inventory required.");
        if (origin.InventoryFingerprint != inventory.Fingerprint || origin.ExpectedDesired != inventory.Document.ExpectedDesired || origin.ExpectedDesired + 1 != source.Generation)
            throw new InvalidDataException("Corrective UPDATE inventory mismatch.");
        var member = inventory.Document.Members.Single(x => x.ManagedIdentity == context.ManagedIdentity);
        var expected = inventory.Document.Objects.Where(x => x.ManagedIdentity == context.ManagedIdentity && x.Role.StartsWith("Current/", StringComparison.Ordinal))
            .OrderBy(x => x.Role, StringComparer.Ordinal).ToArray();
        var actual = await db.ManagedObjectStates.AsNoTracking().Where(x => x.ManagedWorkloadId == context.ManagedIdentity).OrderBy(x => x.Role).Take(2).ToArrayAsync(token);
        if (expected.Length > 1 || actual.Length != expected.Length) throw new InvalidDataException("Corrective current-object inventory changed.");
        for (var index = 0; index < actual.Length; index++)
        {
            var state = actual[index]; var frozen = expected[index];
            if (state.DeploymentId != source.DeploymentId || "Current/" + state.Role != frozen.Role || state.Contract != frozen.Contract ||
                state.Format != frozen.Format || state.Payload != frozen.Payload || state.Generation != frozen.OriginGeneration || state.OperationId != frozen.OriginOperation ||
                state.Generation != member.OriginGeneration || state.OperationId != member.Operation)
                throw new InvalidDataException("Corrective current-object projection mismatch.");
            var previous = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == state.Generation, token);
            _ = await InternalGenerationAuthority.RequireStageReadAsync(db, previous, token);
            var graph = DurableOperationGraphCodec.Decode(previous.GraphJson);
            var previousUpdate = graph.WorkloadUpdate?.Document.Members.SingleOrDefault(x => x.ManagedIdentity == state.ManagedWorkloadId);
            var previousCorrection = graph.Correction?.Document.Members.SingleOrDefault(x => x.ManagedIdentity == state.ManagedWorkloadId);
            var generation = previousUpdate is not null ? graph.WorkloadUpdate!.Document.BaselineGeneration : previousCorrection?.OriginGeneration;
            var operation = previousUpdate?.Operation ?? previousCorrection?.Operation;
            if (generation != state.OriginGeneration || operation != state.OriginOperation)
                throw new InvalidDataException("Corrective current-object original action mismatch.");
        }
    }
    private static async Task ValidateObjectAsync(ManifestCatalogDbContext db, Loaded loaded, string receipt, CancellationToken token)
    {
        var value = loaded.Handler.ObjectConfirmation(loaded.Context, loaded.Intent.Payload, receipt);
        if (value is null) return;
        if (loaded.Handler.UpdatesObject)
        {
            var prior = loaded.Context.Update ?? throw new InvalidDataException("Original update authority required.");
            var desired = await db.ApplicationDeployments.AsNoTracking().Where(x => x.Id == loaded.Intent.DeploymentId).Select(x => x.DesiredGeneration).SingleAsync(token);
            if (desired > loaded.Intent.Generation)
            {
                // TerminalAsync has already validated this generation's immutable
                // intent, original receipt, succeeded attempt and adapter confirmation.
                // A later accepted transition may replace only the latest projection.
                var historical = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == loaded.Intent.DeploymentId && x.Generation == loaded.Intent.Generation, token);
                _ = await InternalGenerationAuthority.RequireStageReadAsync(db, historical, token);
                return;
            }
            var state = await db.ManagedObjectStates.AsNoTracking().SingleOrDefaultAsync(x => x.ManagedWorkloadId == loaded.Intent.ManagedIdentity && x.Role == value.Role, token);
            if (state is null || state.OriginGeneration != prior.Original.Key.Generation.Value || state.OriginOperation != prior.Original.Key.OperationId.Value ||
                state.DeploymentId != loaded.Intent.DeploymentId || state.Generation != loaded.Intent.Generation || state.OperationId != loaded.Intent.OperationId ||
                state.Contract != value.Contract || state.Format != value.Format || state.Payload != value.Payload)
                throw new InvalidDataException("Confirmed current object transition mismatch.");
            return;
        }
        var saved = await db.OwnedObjects.AsNoTracking().SingleOrDefaultAsync(x => x.ManagedWorkloadId == loaded.Intent.ManagedIdentity && x.Role == value.Role, token);
        if (saved is null || saved.Contract != value.Contract || saved.Format != value.Format || saved.Payload != value.Payload ||
            saved.DeploymentId != loaded.Intent.DeploymentId || saved.Generation != loaded.Intent.Generation || saved.OperationId != loaded.Intent.OperationId)
            throw new InvalidDataException("Owned object proof mismatch.");
    }
}
