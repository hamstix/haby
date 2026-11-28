using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed partial class InternalDurableActions
{
    private async Task CaptureCorrectiveCompletionObjectsAsync(ManifestCatalogDbContext db, SubmittedSet current, CancellationToken token)
    {
        var source = current.Source;
        var inventory = DurableOperationGraphCodec.Decode(source.GraphJson).Correction?.Document ?? throw new InvalidDataException("Corrective inventory required.");
        var receipts = new List<CorrectiveMemberReceipt>();
        var creates = new List<string>();
        foreach (var member in current.Members.OrderBy(x => x.Subject, StringComparer.Ordinal))
        {
            var loaded = member.Loaded;
            var frozen = inventory.Members.Single(x => x.Component == loaded.Intent.Component && x.Workload == loaded.Intent.LocalId);
            if (loaded.Handler is not IInternalWorkloadCorrectionPlanner planner || loaded.Operation.CurrentAttemptId is not { } attempt ||
                loaded.Handler.UpdatesObject != (frozen.State == "ConfirmedOwned")) throw new InvalidDataException("Corrective completion support mismatch.");
            var confirmation = planner.CaptureConfirmedObject(loaded.Context, loaded.Intent.Payload, member.Receipt);
            var obj = loaded.Handler.ObjectConfirmation(loaded.Context, loaded.Intent.Payload, member.Receipt);
            if (obj is not null && (obj.Identity != confirmation.Identity || obj.Payload != member.Receipt || obj.Contract != loaded.Intent.OutputContract))
                throw new InvalidDataException("Corrective object confirmation mismatch.");
            receipts.Add(new(loaded.Intent.ManagedIdentity, loaded.Intent.Component, loaded.Intent.LocalId, loaded.Intent.OperationId, attempt,
                ResourcesReadyCheckpointCodec.Hash(loaded.Intent.Payload), ResourcesReadyCheckpointCodec.Hash(member.Receipt), confirmation,
                obj is null ? null : new(obj.Role, obj.Contract, obj.Format)));
            if (frozen.State == "NeverConfirmedInitialCreate") creates.Add(loaded.Intent.OperationId);
        }
        var updates = await db.ManagedObjectStates.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation)
            .OrderBy(x => x.ManagedWorkloadId).ThenBy(x => x.Role).Take(513).ToArrayAsync(token);
        var created = await db.OwnedObjects.AsNoTracking().Where(x => x.DeploymentId == source.DeploymentId && x.Generation == source.Generation && creates.Contains(x.OperationId))
            .OrderBy(x => x.ManagedWorkloadId).ThenBy(x => x.Role).Take(513).ToArrayAsync(token);
        await UpdateRetirementCompletion.CaptureCorrectiveObjectsAsync(db, source,
            new(1, source.DeploymentId, source.Generation, source.Fingerprint, receipts.ToArray(), updates, created), Now(), token);
    }
}
