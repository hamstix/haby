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
    private static async Task RequireCurrentRuntimeAsync(ManifestCatalogDbContext db, InternalActionContext context, CancellationToken token)
    {
        if (context.Update is not null)
        {
            var deployment = Guid.Parse(context.Key.DeploymentId.ToString());
            var source = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment && x.Generation == context.Key.Generation.Value, token);
            await InternalGenerationAuthority.RequireCurrentStageAsync(db, source, token);
            if (await db.ManagedObjectStates.AsNoTracking().AnyAsync(x => x.ManagedWorkloadId == context.ManagedIdentity, token))
                throw new InvalidDataException("Managed object already transitioned.");
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
            if (value.Format <= 0 || await db.ManagedObjectStates.AnyAsync(x => x.ManagedWorkloadId == loaded.Intent.ManagedIdentity && x.Role == value.Role, token))
                throw new InvalidDataException("Current object transition conflict.");
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
    private static async Task ValidateObjectAsync(ManifestCatalogDbContext db, Loaded loaded, string receipt, CancellationToken token)
    {
        var value = loaded.Handler.ObjectConfirmation(loaded.Context, loaded.Intent.Payload, receipt);
        if (value is null) return;
        if (loaded.Handler.UpdatesObject)
        {
            var prior = loaded.Context.Update ?? throw new InvalidDataException("Original update authority required.");
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
