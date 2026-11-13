using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed partial class InternalDurableActions
{
    private sealed record Loaded(DurableActionIntentRow Intent, DurableOperationRow Operation,
        InternalActionContext Context, IInternalActionHandler Handler);
    private static ProviderInput Subject(DurableActionIntentRow row) => row.SubjectKind switch
    {
        "Workload" => new ProviderInput.Workload(ManifestLocalId.Create(row.Component), ManifestLocalId.Create(row.LocalId)),
        "Resource" when row.Component == "" => new ProviderInput.Resource(ManifestLocalId.Create(row.LocalId)),
        _ => throw new InvalidDataException("Invalid saved action subject.")
    };
    private static async Task<DeploymentGenerationRow> SourceAsync(ManifestCatalogDbContext db, DurableChangeSetCommit accepted, CancellationToken token)
    {
        var deployment = Guid.Parse(accepted.DeploymentId.ToString());
        var row = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == accepted.Generation.Value, token)
            ?? throw new InvalidDataException("Accepted internal generation unavailable.");
        var owner = await db.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deployment, token);
        var plan = await db.DurablePlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.PlanId, token);
        var commit = await db.ChangeSetCommits.AsNoTracking().SingleOrDefaultAsync(x => x.ChangeSetId == row.ChangeSetId, token);
        var change = await db.DurableChangeSets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.ChangeSetId, token);
        var inputs = row.Inputs();
        var graph = DurableOperationGraphCodec.Decode(row.GraphJson); graph.ValidateReferences(inputs);
        if (!row.InternalEffects || row.Presence != nameof(DesiredPresence.Present) || owner is null || owner.DesiredGeneration != row.Generation ||
            owner.EnvironmentRecordId != row.EnvironmentRecordId || owner.ApplicationRecordId != row.ApplicationRecordId || owner.AppliedGeneration != graph.Baseline?.Generation ||
            owner.AppliedGeneration is null || owner.AppliedGeneration >= row.Generation || graph.Nodes.Count != 0 || graph.RetainedResources.Count != 0 ||
            graph.ReusedResources.Count != inputs.Configuration!.Manifest.Spec.Resources.Count || graph.ReusedResources.Count == 0 ||
            plan is null || !plan.InternalEffects || plan.ChangeSetId != row.ChangeSetId || plan.EnvironmentRecordId != row.EnvironmentRecordId ||
            plan.ApplicationRecordId != row.ApplicationRecordId || plan.RevisionNumber != row.RevisionNumber || plan.EnvironmentKey != row.EnvironmentKey ||
            plan.SnapshotFormatVersion != row.SnapshotFormatVersion || plan.Fingerprint != row.Fingerprint || plan.ProviderInputsJson != row.ProviderInputsJson || plan.ConfigurationJson != row.ConfigurationJson || plan.GraphJson != row.GraphJson ||
            change is null || change.ExpectedAbsence || change.ExpectedGeneration != graph.Baseline?.Generation || change.RevisionNumber != row.RevisionNumber ||
            change.EnvironmentRecordId != row.EnvironmentRecordId || change.ApplicationRecordId != row.ApplicationRecordId ||
            commit is null || commit.PlanId != row.PlanId || commit.DeploymentId != row.DeploymentId || commit.Generation != row.Generation ||
            commit.PlanId != Guid.Parse(accepted.PlanId.ToString()) || commit.ChangeSetId != Guid.Parse(accepted.ChangeSetId.ToString()) ||
            commit.ActorId != accepted.ActorId || commit.CommittedAt != accepted.CommittedAt || row.AcceptedAt != commit.CommittedAt || row.ActorId != commit.ActorId)
            throw new InvalidDataException("Accepted internal authority mismatch.");
        var revision = await db.Revisions.AsNoTracking().SingleOrDefaultAsync(x => x.ApplicationRecordId == row.ApplicationRecordId && x.LocalRevisionNumber == row.RevisionNumber, token)
            ?? throw new InvalidDataException("Accepted declaration unavailable.");
        var manifest = NormalizedSnapshotCodec.Decode(revision.Snapshot, revision.InputApiVersion, revision.NormalizedModelVersion, revision.ApplicationId, revision.SnapshotFormatVersion);
        if (!manifest.Equals(inputs.Configuration.Manifest) || !await ResourceContinuity.ValidateAsync(db, deployment, graph, token))
            throw new InvalidDataException("Accepted declaration or original Resource provenance mismatch.");
        return row;
    }

    private async Task<(InternalActionContext Context, IInternalActionHandler Handler)> ContextAsync(ManifestCatalogDbContext db,
        DeploymentGenerationRow source, DurableOperationKey key, ProviderInput subject, string action, Guid marker, Guid managed, CancellationToken token)
    {
        var inputs = source.Inputs();
        if (!inputs.Providers.Entries.TryGetValue(subject, out var exact)) throw new InvalidDataException("Subject is not declared in this generation.");
        var contract = subject switch
        {
            ProviderInput.Workload w => inputs.Configuration!.Manifest.Spec.Components[w.Component].Workloads[w.Id].Type.Value,
            ProviderInput.Resource r => inputs.Configuration!.Manifest.Spec.Resources[r.Id].Type.Value,
            _ => throw new InvalidDataException("Subject contract unavailable.")
        };
        var provider = Guid.Parse(exact.Instance.ToString());
        var instance = await db.ProviderInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == provider && x.EnvironmentRecordId == source.EnvironmentRecordId, token)
            ?? throw new InvalidDataException("Exact Provider owner unavailable.");
        var revision = await db.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderInstanceId == provider &&
            x.EnvironmentRecordId == source.EnvironmentRecordId && x.RevisionNumber == exact.Configuration.Value && x.IsSealed, token)
            ?? throw new InvalidDataException("Exact Provider revision unavailable.");
        if (revision.ImplementationId != exact.Implementation.Value) throw new InvalidDataException("Exact implementation mismatch.");
        // Exact handler selection precedes codec decode and all material reads.
        var matches = handlers.Where(x => x.Contract.Subject == contract && x.Contract.ProviderKind == instance.ProviderKindId &&
            x.Contract.Configuration == revision.ConfigurationShapeId && x.Contract.Implementation == revision.ImplementationId && x.Contract.Action == action).ToArray();
        if (matches is not [var handler]) throw new InvalidDataException("Exact action handler unavailable or ambiguous.");
        var readers = typedCodecs.Where(x => x.ProviderKindId == instance.ProviderKindId && x.ShapeId == revision.ConfigurationShapeId && x.PayloadFormatVersion == revision.PayloadFormatVersion).ToArray();
        if (readers is not [var codec]) throw new InvalidDataException("Exact action configuration codec unavailable.");
        var prepared = codec.Encode(codec.Decode(revision.PayloadJson));
        if (prepared.PayloadJson != revision.PayloadJson || prepared.ProviderKindId != instance.ProviderKindId || prepared.ShapeId != revision.ConfigurationShapeId ||
            prepared.ImplementationId != revision.ImplementationId || prepared.TargetIdentity.Value != instance.TargetIdentity || prepared.TargetIdentity.FormatVersion != instance.TargetFormatVersion)
            throw new InvalidDataException("Exact Provider configuration mismatch.");
        var refs = await db.ProviderRevisionSecretRefs.AsNoTracking().Where(x => x.ProviderInstanceId == provider && x.RevisionNumber == exact.Configuration.Value).ToArrayAsync(token);
        if (refs.Length != prepared.SecretReferences.Count || prepared.SecretReferences.Any(x => !refs.Any(r => r.Slot == x.Slot &&
            r.SecretId == Guid.Parse(x.Reference.SecretId.ToString()) && r.SecretVersionId == Guid.Parse(x.Reference.SecretVersionId.ToString()))))
            throw new InvalidDataException("Exact SecretRef metadata mismatch.");
        var inputSubject = subject switch { ProviderInput.Workload w => "Workload/" + w.Component.Value + "/" + w.Id.Value, ProviderInput.Resource r => "Resource/" + r.Id.Value, _ => throw new InvalidDataException() };
        if (!await db.InternalPlanInputs.AsNoTracking().AnyAsync(x => x.PlanId == source.PlanId && x.Subject == inputSubject && x.ProviderId == provider && x.ConfigurationRevision == exact.Configuration.Value, token))
            throw new InvalidDataException("Exact administrative input pin unavailable.");
        PlacementDeclaration? placement = null;
        if (exact.Placement is { } frozen)
        {
            var pin = InternalPlacementInput.Decode(frozen);
            var row = await db.PlacementRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == pin.Id && x.EnvironmentRecordId == source.EnvironmentRecordId && x.ProviderAlias == exact.Alias.Value, token);
            var logical = subject is ProviderInput.Workload w ? "Workload/" + w.Component.Value + "/" + w.Id.Value : throw new InvalidDataException("Placement subject mismatch.");
            if (row is null || row.Name != pin.Declaration.Name.Value || row.Revision != pin.Declaration.Revision || row.Contract != pin.Declaration.Contract ||
                row.FormatVersion != pin.Declaration.FormatVersion || row.Payload != pin.Declaration.Payload ||
                !await db.InternalPlanInputs.AsNoTracking().AnyAsync(x => x.PlanId == source.PlanId && x.Subject == logical && x.PlacementId == pin.Id, token))
                throw new InvalidDataException("Exact Placement mismatch.");
            placement = pin.Declaration;
        }
        var manifestRow = await db.Revisions.AsNoTracking().SingleAsync(x => x.ApplicationRecordId == source.ApplicationRecordId && x.LocalRevisionNumber == source.RevisionNumber, token);
        return (new(key, inputs, subject, exact, prepared, manifestRow.ApplicationVersion, marker, managed, placement), handler);
    }

    private async Task<Loaded> LoadAsync(ManifestCatalogDbContext db, DurableOperationKey key, CancellationToken token)
    {
        var deployment = Guid.Parse(key.DeploymentId.ToString());
        var intent = await db.ActionIntents.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == key.Generation.Value && x.OperationId == key.OperationId.Value, token)
            ?? throw new InvalidDataException("Immutable action intent unavailable.");
        var generation = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == key.Generation.Value, token)
            ?? throw new InvalidDataException("Accepted action generation unavailable.");
        var accepted = await db.ChangeSetCommits.AsNoTracking().SingleOrDefaultAsync(x => x.ChangeSetId == generation.ChangeSetId, token)
            ?? throw new InvalidDataException("Accepted action commit unavailable.");
        generation = await SourceAsync(db, new(DeploymentChangeSetId.Parse(accepted.ChangeSetId.ToString()), DeploymentPlanId.Parse(accepted.PlanId.ToString()),
            key.DeploymentId, key.Generation, accepted.CommittedAt, accepted.ActorId), token);
        var proof = await DurableAggregateCompletion.ReadAsync(db, generation, token, true);
        if (intent.FormatVersion != 1 || intent.Marker == Guid.Empty || intent.ManagedIdentity == Guid.Empty || intent.InputFingerprint != generation.Fingerprint ||
            proof is null || proof.Id != intent.CheckpointId || intent.Payload.Length is 0 or > 1_048_576 || System.Text.Encoding.UTF8.GetByteCount(intent.Payload) > 1_048_576 ||
            intent.OperationId != "effect-" + intent.RequestId) throw new InvalidDataException("Immutable action authority mismatch.");
        var selected = await ContextAsync(db, generation, key, Subject(intent), intent.ActionContract, intent.Marker, intent.ManagedIdentity, token);
        var metadata = await DurableAggregateCompletion.InspectMetadataAsync(db, deployment, key.Generation.Value, resourceOutputs, typedCodecs, token, resourceAdmission, internalResourceStage: true);
        if (metadata is null || !await DurableAggregateCompletion.CheckCredentialMetadataAsync(db, metadata, token)) throw new InvalidDataException("Current Resource evidence metadata unavailable.");
        var contract = selected.Handler.Contract;
        if (contract != new InternalActionContract(intent.SubjectContract, intent.ProviderKind, intent.ConfigurationContract, intent.Implementation,
                intent.ActionContract, intent.InputFormat, intent.OutputContract) || selected.Handler.ObservationRecovery != intent.ObservationRecovery ||
            selected.Context.Provider.Instance.ToString() != intent.ProviderId.ToString() || selected.Context.Provider.Configuration.Value != intent.ConfigurationRevision ||
            (selected.Context.Provider.Placement is { } placement ? InternalPlacementInput.Decode(placement).Id : (Guid?)null) != intent.PlacementId ||
            !selected.Handler.Validate(selected.Context, intent.Payload) || selected.Handler.Prepare(selected.Context) != intent.Payload)
            throw new InvalidDataException("Immutable action payload or handler mismatch.");
        var operation = await DurableOperationTransitions.Operation(db, key).AsNoTracking().SingleOrDefaultAsync(token)
            ?? throw new InvalidDataException("Shared action state unavailable.");
        if (operation.State == nameof(DurableOperationState.Pending))
        {
            if (operation.CurrentAttemptId is not null || operation.AttemptNumber != 0 || operation.DispatchedAt is not null || operation.FinalizedAt is not null)
                throw new InvalidDataException("Invalid pending action state.");
        }
        else
        {
            var attempt = await db.DurableOperationAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == operation.CurrentAttemptId &&
                x.DeploymentId == deployment && x.Generation == key.Generation.Value && x.OperationId == key.OperationId.Value && x.AttemptNumber == operation.AttemptNumber, token);
            if (attempt is null || attempt.DispatchedAt != operation.DispatchedAt) throw new InvalidDataException("Invalid current action attempt.");
            var consistent = operation.State switch
            {
                nameof(DurableOperationState.Preparing) => attempt.Outcome is null && attempt.FinishedAt is null && attempt.DispatchedAt is null && operation.FinalizedAt is null,
                nameof(DurableOperationState.Dispatching) => attempt.Outcome is null && attempt.FinishedAt is null && attempt.DispatchedAt is not null && operation.FinalizedAt is null,
                nameof(DurableOperationState.NeedsAttention) => operation.ReasonCode == nameof(DurableOperationReason.UncertainAfterDispatch) && attempt.Outcome == nameof(DurableAttemptOutcome.Uncertain) && attempt.FinishedAt is not null && attempt.DispatchedAt is not null && operation.FinalizedAt is not null,
                nameof(DurableOperationState.Succeeded) => (attempt.Outcome is nameof(DurableAttemptOutcome.Succeeded) or nameof(DurableAttemptOutcome.Uncertain)) && attempt.FinishedAt is not null && attempt.DispatchedAt is not null && operation.FinalizedAt is not null,
                _ => false
            };
            if (!consistent) throw new InvalidDataException("Inconsistent retained action state.");
        }
        return new(intent, operation, selected.Context, selected.Handler);
    }
}
