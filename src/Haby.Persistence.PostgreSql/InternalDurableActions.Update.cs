using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed partial class InternalDurableActions
{
    internal async Task<PreparedRuntimeIdentity?> ObserveRetainedIdentityAsync(DurableChangeSetCommit accepted, ProviderInput.Workload subject, CancellationToken token)
    {
        Loaded original;
        RuntimeIdentityBindingRow binding;
        InternalActionResult saved;
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            var source = await InternalGenerationAuthority.RequireStageAsync(db, accepted, token);
            var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
            if (graph.WorkloadUpdate is null && graph.Correction is null) return null;
            await InternalGenerationAuthority.RequireCurrentStageAsync(db, source, token);
            var managed = await db.ManagedWorkloads.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == source.DeploymentId && x.Component == subject.Component.Value && x.Workload == subject.Id.Value, token);
            var retained = managed is null ? null : await db.RuntimeIdentityBindings.AsNoTracking().SingleOrDefaultAsync(x => x.ManagedWorkloadId == managed.Id, token);
            if (retained is null)
            {
                var pending = graph.Correction?.Document.Members.Single(x => x.Component == subject.Component.Value && x.Workload == subject.Id.Value);
                if (pending?.State == "NeverConfirmedInitialCreate" && (managed is null || !await db.OwnedObjects.AnyAsync(x => x.ManagedWorkloadId == managed.Id, token))) return null;
                throw new InvalidDataException("Confirmed runtime identity cannot be recreated.");
            }
            binding = retained;
            var stable = managed ?? throw new InvalidDataException("Retained identity owner unavailable.");
            await RequireIdentityOriginCoreAsync(db, binding, OriginPurpose.HistoricalRead, token);
            var inventory = await db.OwnedObjects.AsNoTracking().SingleAsync(x => x.ManagedWorkloadId == stable.Id && x.Role == binding.Role, token);
            original = await LoadReadAsync(db, new(accepted.DeploymentId, DeploymentGeneration.Create(inventory.Generation), OperationId.Create(inventory.OperationId)), token);
            saved = await TerminalAsync(db, original, token);
        }
        var observed = await original.Handler.ObserveAsync(original.Context, original.Intent.Payload, token);
        if (observed.Outcome != InternalActionOutcome.Confirmed || observed.Receipt is null ||
            !original.Handler.ValidateReceipt(original.Context, original.Intent.Payload, observed.Receipt, true) ||
            original.Handler.IdentityConfirmation(original.Context, original.Intent.Payload, observed.Receipt)?.ValidationPayload !=
                original.Handler.IdentityConfirmation(original.Context, original.Intent.Payload, saved.Receipt!)?.ValidationPayload)
            return new(subject, new(InternalActionOutcome.Refused, "retained-identity-unconfirmed"), binding.Id);
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            var source = await InternalGenerationAuthority.RequireStageAsync(db, accepted, token);
            await InternalGenerationAuthority.RequireCurrentStageAsync(db, source, token);
        }
        return new(subject, new(InternalActionOutcome.Confirmed, "retained-identity-confirmed"), binding.Id, InternalRuntimePublication.BindingState(binding));
    }

    internal static async Task<InternalActionContext> UpdateContextAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        InternalActionContext context, IInternalActionHandler handler, CancellationToken token, int remaining = 256)
    {
        if (!handler.UpdatesObject) return context;
        if (remaining < 1) throw new InvalidDataException("Update context history exceeds bound.");
        var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        if (graph.Correction is not null) return await CorrectiveUpdateContextAsync(db, source, graph.Correction, context, handler, token, remaining);
        var inventory = graph.WorkloadUpdate?.Document ?? throw new InvalidDataException("Update inventory required.");
        var subject = context.Subject as ProviderInput.Workload ?? throw new InvalidDataException("Update Workload required.");
        var member = inventory.Members.Single(x => x.Component == subject.Component.Value && x.Workload == subject.Id.Value);
        var baseline = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == inventory.BaselineGeneration, token);
        var original = await db.ActionIntents.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == baseline.Generation && x.OperationId == member.Operation, token);
        var output = await db.OperationOutputs.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == baseline.Generation && x.OperationId == member.Operation, token);
        var version = await db.Revisions.AsNoTracking().Where(x => x.ApplicationRecordId == baseline.ApplicationRecordId && x.LocalRevisionNumber == baseline.RevisionNumber).Select(x => x.ApplicationVersion).SingleAsync(token);
        var inputs = baseline.Inputs();
        var prior = context with
        {
            Key = new(context.Key.DeploymentId, DeploymentGeneration.Create(baseline.Generation), OperationId.Create(member.Operation)),
            Inputs = inputs,
            Provider = inputs.Providers.Entries[subject],
            ApplicationVersion = version,
            Marker = original.Marker,
            ManagedIdentity = member.ManagedIdentity,
            Runtime = null,
            Update = null
        };
        if (member.TargetContract != handler.ManagedTarget(context)?.Contract || member.ManagedIdentity != original.ManagedIdentity ||
            original.OutputContract != output.Contract || original.InputFingerprint != baseline.Fingerprint)
            throw new InvalidDataException("Original update action mismatch.");
        if (baseline.Generation >= source.Generation) throw new InvalidDataException("Update origin must precede its target.");
        if (original.ActionContract == handler.Contract.Action) prior = await UpdateContextAsync(db, baseline, prior, handler, token, remaining - 1);
        return context with { ManagedIdentity = member.ManagedIdentity, Update = new(prior, member.Delta, original.Payload, output.Payload) };
    }

    private static async Task<InternalActionContext> CorrectiveUpdateContextAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        CorrectiveInventory inventory, InternalActionContext context, IInternalActionHandler handler, CancellationToken token, int remaining)
    {
        var document = inventory.Document;
        var subject = context.Subject as ProviderInput.Workload ?? throw new InvalidDataException("Corrective Workload required.");
        var member = document.Members.Single(x => x.Component == subject.Component.Value && x.Workload == subject.Id.Value);
        if (member.State != "ConfirmedOwned" || member.OriginGeneration is not { } generation || member.Operation is not { } operation ||
            document.Deployment != source.DeploymentId || document.ExpectedDesired + 1 != source.Generation ||
            handler is not IInternalWorkloadUpdatePlanner update || handler is not IInternalWorkloadCorrectionPlanner correction)
            throw new InvalidDataException("Exact corrective UPDATE origin required.");
        context = context with { ManagedIdentity = member.ManagedIdentity!.Value };
        var baseline = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == generation, token);
        var original = await db.ActionIntents.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == generation && x.OperationId == operation, token);
        var output = await db.OperationOutputs.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == generation && x.OperationId == operation, token);
        var attempt = await db.DurableOperations.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == generation && x.OperationId == operation, token);
        if (original.ManagedIdentity != member.ManagedIdentity || original.InputFingerprint != baseline.Fingerprint || original.OutputContract != output.Contract ||
            attempt.State != "Succeeded" || attempt.CurrentAttemptId != member.Attempt ||
            ResourcesReadyCheckpointCodec.Hash(original.Payload) != member.IntentHash || ResourcesReadyCheckpointCodec.Hash(output.Payload) != member.ReceiptHash)
            throw new InvalidDataException("Corrective original confirmation mismatch.");
        var version = await db.Revisions.AsNoTracking().Where(x => x.ApplicationRecordId == baseline.ApplicationRecordId && x.LocalRevisionNumber == baseline.RevisionNumber)
            .Select(x => x.ApplicationVersion).SingleAsync(token);
        var inputs = baseline.Inputs();
        var prior = context with
        {
            Key = new(context.Key.DeploymentId, DeploymentGeneration.Create(generation), OperationId.Create(operation)),
            Inputs = inputs,
            Provider = inputs.Providers.Entries[subject],
            ApplicationVersion = version,
            Marker = original.Marker,
            ManagedIdentity = original.ManagedIdentity,
            Runtime = null,
            Update = null
        };
        if (generation >= source.Generation) throw new InvalidDataException("Corrective original must precede its target.");
        if (original.ActionContract == handler.Contract.Action) prior = await UpdateContextAsync(db, baseline, prior, handler, token, remaining - 1);
        var confirmed = correction.CaptureConfirmedObject(prior, original.Payload, output.Payload);
        if (confirmed != member.Confirmation) throw new InvalidDataException("Corrective confirmed object changed.");
        var delta = update.CaptureUpdate(prior, context.Inputs, context.ApplicationVersion, original.Payload, output.Payload);
        return context with
        {
            Update = new(prior, delta, original.Payload, output.Payload,
            new(document.ExpectedDesired, inventory.Fingerprint, confirmed.Identity))
        };
    }
}
