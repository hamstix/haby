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
            if (DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate is null) return null;
            await InternalGenerationAuthority.RequireCurrentStageAsync(db, source, token);
            var managed = await db.ManagedWorkloads.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Component == subject.Component.Value && x.Workload == subject.Id.Value, token);
            binding = await db.RuntimeIdentityBindings.AsNoTracking().SingleAsync(x => x.ManagedWorkloadId == managed.Id, token);
            await RequireIdentityOriginCoreAsync(db, binding, OriginPurpose.HistoricalRead, token);
            var inventory = await db.OwnedObjects.AsNoTracking().SingleAsync(x => x.ManagedWorkloadId == managed.Id && x.Role == binding.Role, token);
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

    private static async Task<InternalActionContext> UpdateContextAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        InternalActionContext context, IInternalActionHandler handler, CancellationToken token)
    {
        if (!handler.UpdatesObject) return context;
        var inventory = DurableOperationGraphCodec.Decode(source.GraphJson).WorkloadUpdate?.Document
            ?? throw new InvalidDataException("Update inventory required.");
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
        return context with { ManagedIdentity = member.ManagedIdentity, Update = new(prior, member.Delta, original.Payload, output.Payload) };
    }
}
