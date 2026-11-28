using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Haby.Persistence.PostgreSql;

public sealed partial class PostgreSqlDurableDeploymentPlanner
{
    public async ValueTask<DurableChangeSetCreateResult> CreateCorrectiveChangeSetAsync(EnvironmentRecordId environment,
        ApplicationRecordId application, ManifestRevisionRef revision, CorrectiveChangeSetIntent correction, Guid requestId,
        string actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(correction);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(revision);
        ValidateActor(actor);
        if (requestId == Guid.Empty) return new(DurableChangeSetCreateStatus.InvalidInput);
        var intent = CorrectiveIntentCodec.Encode(correction);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var key = $"haby-corrective-request:{requestId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", token);
        var prior = await db.DurableChangeSets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId, token);
        if (prior is not null)
            return prior.EnvironmentRecordId.ToString() == environment.ToString() && prior.ApplicationRecordId.ToString() == application.ToString() &&
                prior.RevisionNumber == revision.RevisionNumber.Value && prior.CorrectiveIntentJson == intent
                ? new(DurableChangeSetCreateStatus.Created, ToChangeSet(prior)) : new(DurableChangeSetCreateStatus.InvalidInput);
        var refused = await ValidateChangeSetInputAsync(db, environment, application, revision, null, false, token);
        if (refused is { } failure) return new(failure);
        var owner = await db.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == Guid.Parse(correction.Deployment.ToString()) &&
            x.EnvironmentRecordId == Guid.Parse(environment.ToString()) && x.ApplicationRecordId == Guid.Parse(application.ToString()), token);
        if (owner is null || owner.DesiredGeneration != correction.ExpectedIncompleteDesiredGeneration.Value || owner.AppliedGeneration == owner.DesiredGeneration)
            return new(DurableChangeSetCreateStatus.ExpectedGenerationUnsupported);
        var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == owner.Id && x.Generation == owner.DesiredGeneration, token);
        if (source is not { InternalEffects: true }) return new(DurableChangeSetCreateStatus.UnsupportedShape);
        DeploymentChangeSet change;
        try
        {
            change = await AddChangeSetAsync(db, environment, application, revision,
                new DeploymentPrecondition.ExpectedGeneration(correction.ExpectedIncompleteDesiredGeneration), null, actor, token,
                source.Inputs().Acquisitions, correction, requestId);
        }
        catch (InvalidDataException) { return new(DurableChangeSetCreateStatus.UnsupportedShape); }
        await db.SaveChangesAsync(token);
        token.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        return new(DurableChangeSetCreateStatus.Created, ToChangeSet(await db.DurableChangeSets.AsNoTracking().SingleAsync(x => x.Id == requestId, token)));
    }

    private async Task<DurablePlanResult> CreateCorrectivePlanAsync(ManifestCatalogDbContext db, IDbContextTransaction transaction,
        DurableChangeSetRow change, ApplicationDeploymentRow? owner, string actor, IReadOnlyList<IInternalActionHandler> handlers, CancellationToken token)
    {
        try
        {
            if (owner is null) return new(DurablePlanStatus.ChangeSetStale);
            var captured = await CaptureCorrectiveAsync(db, change, owner, handlers, token);
            var id = Guid.CreateVersion7(); var at = clock.GetUtcNow();
            var row = new DurablePlanRow
            {
                Id = id,
                ChangeSetId = change.Id,
                InternalEffects = true,
                EnvironmentRecordId = change.EnvironmentRecordId,
                EnvironmentKey = captured.Inputs.EnvironmentKey.Value,
                ApplicationRecordId = change.ApplicationRecordId,
                RevisionNumber = change.RevisionNumber,
                SnapshotFormatVersion = DurableInputStorage.Version,
                ProviderInputsJson = ExactProviderResolutionSetCodec.Encode(captured.Inputs.Providers),
                GraphJson = DurableOperationGraphCodec.Encode(captured.Graph),
                ConfigurationJson = ConfigurationPlanningSnapshotCodec.Encode(captured.Inputs.Configuration!),
                Fingerprint = DurableOperationGraphCodec.Fingerprint(captured.Inputs, captured.Graph),
                CreatedAt = at,
                ExpiresAt = at.Add(Lifetime),
                ActorId = actor
            };
            db.DurablePlans.Add(row);
            foreach (var exact in captured.Inputs.Providers.Entries.Values)
                db.InternalPlanInputs.Add(new()
                {
                    PlanId = id,
                    Subject = exact.Input is ProviderInput.Workload w ? "Workload/" + w.Component.Value + "/" + w.Id.Value :
                    "Resource/" + ((ProviderInput.Resource)exact.Input).Id.Value,
                    ProviderId = Guid.Parse(exact.Instance.ToString()),
                    ConfigurationRevision = exact.Configuration.Value,
                    PlacementId = exact.Placement is { } placement ? InternalPlacementInput.Decode(placement).Id : null
                });
            await db.SaveChangesAsync(token);
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            return new(DurablePlanStatus.Planned, ToPlan(row, null, null));
        }
        catch (Exception error) when (error is InvalidDataException or WorkloadUpdateRefusedException or ArgumentException or System.Text.Json.JsonException)
        { return new(DurablePlanStatus.PlanInputUnavailable); }
    }

    private async Task<DurablePlanCurrentStatus> CheckCorrectivePlanAsync(ManifestCatalogDbContext db, DurablePlanRow row,
        IReadOnlyList<IInternalActionHandler> handlers, CancellationToken token)
    {
        if (clock.GetUtcNow() >= row.ExpiresAt) return DurablePlanCurrentStatus.Expired;
        if (await db.DurablePlanClosures.AnyAsync(x => x.PlanId == row.Id, token)) return DurablePlanCurrentStatus.Closed;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var key = $"haby-deployment:{row.EnvironmentRecordId:N}:{row.ApplicationRecordId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", token);
        var change = await db.DurableChangeSets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.ChangeSetId, token);
        var owner = await db.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentRecordId == row.EnvironmentRecordId && x.ApplicationRecordId == row.ApplicationRecordId, token);
        if (change is null || owner is null || !Matches(change, owner)) return DurablePlanCurrentStatus.ChangeSetStale;
        try
        {
            _ = ToPlan(row, null, null);
            if (change.EnvironmentRecordId != row.EnvironmentRecordId || change.ApplicationRecordId != row.ApplicationRecordId || change.RevisionNumber != row.RevisionNumber)
                return DurablePlanCurrentStatus.PlanInputUnavailable;
            var captured = await CaptureCorrectiveAsync(db, change, owner, handlers, token);
            return await ValidateOperatorPinsAsync(db, row.Id, captured.Inputs, token) &&
                row.Fingerprint == DurableOperationGraphCodec.Fingerprint(captured.Inputs, captured.Graph)
                ? DurablePlanCurrentStatus.Current : DurablePlanCurrentStatus.PlanStale;
        }
        catch (Exception error) when (error is InvalidDataException or WorkloadUpdateRefusedException or ArgumentException or System.Text.Json.JsonException) { return DurablePlanCurrentStatus.PlanStale; }
    }

    internal async Task<(DurablePlanningInputs Inputs, DurableOperationGraph Graph)> CaptureCorrectiveAsync(ManifestCatalogDbContext db,
        DurableChangeSetRow change, ApplicationDeploymentRow owner, IReadOnlyList<IInternalActionHandler> handlers, CancellationToken token)
    {
        var intent = CorrectiveIntentCodec.Decode(change.CorrectiveIntentJson) ?? throw new InvalidDataException("Explicit corrective intent required.");
        if (intent.Deployment.ToString() != owner.Id.ToString() || intent.ExpectedIncompleteDesiredGeneration.Value != owner.DesiredGeneration ||
            owner.AppliedGeneration >= owner.DesiredGeneration || change.ImportIntentJson is not null || change.ExpectedAbsence || change.ExpectedGeneration != owner.DesiredGeneration)
            throw new InvalidDataException("Exact incomplete desired generation required.");
        // Catalog capture only: this shared lock freezes publication writers, not an executor handoff.
        // No execution authority is granted here. The accepting caller owns the
        // deployment-wide gate, locked recapture and atomic desired-owner CAS.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM application_deployments WHERE id = {owner.Id} FOR SHARE", token);
        var source = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == owner.Id && x.Generation == owner.DesiredGeneration, token);
        source = await InternalGenerationAuthority.RequireStageAsync(db, source, token);
        await InternalGenerationAuthority.RequireCurrentStageAsync(db, source, token);
        if (await db.GenerationStageCheckpoints.AnyAsync(x => x.DeploymentId == owner.Id && x.Generation == source.Generation &&
                (x.Stage == "RetirementStarted" || x.Stage == "RetirementCompleted"), token) ||
            await db.DurableOperations.AnyAsync(x => x.DeploymentId == owner.Id && x.Generation == source.Generation &&
                (x.State == "Preparing" || x.State == "Dispatching" || x.State == "NeedsAttention"), token))
            throw new InvalidDataException("Unresolved dispatch or retirement blocks correction.");
        var proof = await DurableAggregateCompletion.ReadAsync(db, source, token, true) ?? throw new InvalidDataException("ResourcesReady required.");
        var metadata = await DurableAggregateCompletion.InspectMetadataAsync(db, owner.Id, source.Generation, outputContracts ?? [], _codecs, token, admission, internalResourceStage: true);
        if (metadata is null || !await DurableAggregateCompletion.CheckCredentialMetadataAsync(db, metadata, token)) throw new InvalidDataException("Current Resource authority required.");
        var previous = source.Inputs();
        if (!SupportsRendering(previous.Configuration!)) throw new InvalidDataException("Frozen renderer support unavailable.");
        var revision = ManifestRevisionRef.Create(ApplicationRecordId.Parse(change.ApplicationRecordId.ToString()), ManifestRevisionNumber.Create(change.RevisionNumber));
        var target = await revisions.FindAsync(revision, token) ?? throw new InvalidDataException("Target revision unavailable.");
        var resourceProviders = target.Declaration.Spec.Resources.Count == 0 ? [] : (await CaptureResourceProvidersAsync(db, target.Declaration, previous.Environment, token)).Resources;
        var providers = await CaptureInternalProvidersAsync(db, target.Declaration, previous.Environment, resourceProviders, token);
        if (!await ValidateInternalProvidersAsync(db, previous, token)) throw new InvalidDataException("Current exact Provider authority unavailable.");
        var configuration = await ConfigurationPlanningCapture.CaptureAsync(db, new(previous.Environment, previous.EnvironmentKey), revision,
            target.Declaration, token, _rendering, previous.Configuration!.RenderPins);
        var inputs = new DurablePlanningInputs(previous.Environment, previous.EnvironmentKey, revision, providers)
        { Configuration = configuration.Snapshot ?? throw new InvalidDataException("Target configuration unavailable."), Acquisitions = previous.Acquisitions };
        if (previous.Revision == inputs.Revision && previous.Configuration.Equals(inputs.Configuration) && previous.Providers.Equals(inputs.Providers))
            throw new InvalidDataException("Unchanged desired input must resume its generation.");
        var oldVersion = await db.Revisions.AsNoTracking().Where(x => x.ApplicationRecordId == source.ApplicationRecordId && x.LocalRevisionNumber == source.RevisionNumber).Select(x => x.ApplicationVersion).SingleAsync(token);
        var selected = await SelectInternalDocumentsAsync(db, inputs, target.ApplicationVersion.Value, handlers, token);
        if (!selected.Keys.ToHashSet().SetEquals(previous.Providers.Entries.Keys.OfType<ProviderInput.Workload>()))
            throw new InvalidDataException("Corrective Workload identity set changed.");
        var stable = await db.ManagedWorkloads.AsNoTracking().Where(x => x.DeploymentId == owner.Id).OrderBy(x => x.Component).ThenBy(x => x.Workload).Take(513).ToArrayAsync(token);
        var objects = await db.OwnedObjects.AsNoTracking().Where(x => x.DeploymentId == owner.Id).OrderBy(x => x.ManagedWorkloadId).ThenBy(x => x.Role).Take(1025).ToArrayAsync(token);
        var states = await db.ManagedObjectStates.AsNoTracking().Where(x => x.DeploymentId == owner.Id).OrderBy(x => x.ManagedWorkloadId).ThenBy(x => x.Role).Take(1025).ToArrayAsync(token);
        var members = new List<CorrectiveMember>();
        var significantInput = oldVersion != target.ApplicationVersion.Value;
        foreach (var subject in selected.Keys.OrderBy(x => x.Component.Value, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal))
        {
            var exact = previous.Providers.Entries[subject];
            var providerId = Guid.Parse(exact.Instance.ToString());
            var providerInstance = await db.ProviderInstances.AsNoTracking().SingleAsync(x => x.Id == providerId, token);
            var exactRevision = await db.ProviderConfigurationRevisions.AsNoTracking().SingleAsync(x => x.ProviderInstanceId == providerInstance.Id && x.RevisionNumber == exact.Configuration.Value, token);
            var candidates = handlers.Where(x => x is IInternalWorkloadCorrectionPlanner && !x.UpdatesObject && x.Contract.Subject == previous.Providers.Declaration.Spec.Components[subject.Component].Workloads[subject.Id].Type.Value &&
                x.Contract.Implementation == exact.Implementation.Value && x.Contract.ProviderKind == providerInstance.ProviderKindId &&
                x.Contract.Configuration == exactRevision.ConfigurationShapeId).ToArray();
            if (candidates is not [var handler]) throw new InvalidDataException("Exact corrective planning support unavailable.");
            if (handler is not IInternalReadinessHandler || handler is not IInternalRecipientObserver)
                throw new InvalidDataException("Complete corrective CREATE continuation unavailable.");
            var image = ((IInternalWorkloadCorrectionPlanner)handler).CaptureCorrection(previous, oldVersion, inputs, target.ApplicationVersion.Value, subject);
            significantInput |= image.PreviousImage != image.TargetImage;
            var managed = stable.SingleOrDefault(x => x.Component == subject.Component.Value && x.Workload == subject.Id.Value);
            var history = await db.ActionIntents.AsNoTracking().Where(x => x.DeploymentId == owner.Id && x.SubjectKind == "Workload" && x.Component == subject.Component.Value && x.LocalId == subject.Id.Value && x.OutputContract == handler.Contract.OutputContract)
                .OrderBy(x => x.Generation).ThenBy(x => x.OperationId).Take(257).ToArrayAsync(token);
            if (history.Length > 256) throw new InvalidDataException("Member history exceeds bound.");
            var current = history.SingleOrDefault(x => x.Generation == source.Generation && x.ActionContract == handler.Contract.Action);
            var confirmed = new List<(DurableActionIntentRow Intent, DurableOperationRow Operation, OperationOutputRow Output)>();
            foreach (var action in history)
            {
                var operation = await db.DurableOperations.AsNoTracking().SingleAsync(x => x.DeploymentId == owner.Id && x.Generation == action.Generation && x.OperationId == action.OperationId, token);
                var output = await db.OperationOutputs.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == owner.Id && x.Generation == action.Generation && x.OperationId == action.OperationId, token);
                if (operation.State == "Succeeded" && output is not null) confirmed.Add((action, operation, output));
                else if (operation.State != "Pending" || operation.AttemptNumber != 0 || operation.CurrentAttemptId is not null || operation.DispatchedAt is not null || output is not null ||
                    await db.DurableOperationAttempts.AnyAsync(x => x.DeploymentId == owner.Id && x.Generation == action.Generation && x.OperationId == action.OperationId, token))
                    throw new InvalidDataException("Unproven member outcome blocks correction.");
            }
            if (confirmed.Count > 0)
            {
                var updateHandlers = handlers.Where(x => x.UpdatesObject && x.Contract.Subject == handler.Contract.Subject &&
                    x.Contract.ProviderKind == handler.Contract.ProviderKind && x.Contract.Configuration == handler.Contract.Configuration &&
                    x.Contract.Implementation == handler.Contract.Implementation && x.Contract.OutputContract == handler.Contract.OutputContract).ToArray();
                if (updateHandlers is not [var updateHandler] || updateHandler is not IInternalReadinessHandler || updateHandler is not IInternalRecipientObserver ||
                    updateHandler is not IInternalWorkloadUpdatePlanner || updateHandler is not IInternalWorkloadCorrectionPlanner)
                    throw new InvalidDataException("Complete corrective UPDATE continuation unavailable.");
                var last = confirmed[^1];
                if (managed is null || last.Intent.ManagedIdentity != managed.Id || last.Operation.CurrentAttemptId is null || last.Output.Contract != last.Intent.OutputContract ||
                    managed.EnvironmentId != source.EnvironmentRecordId || managed.ApplicationId != source.ApplicationRecordId || managed.ProviderId.ToString() != exact.Instance.ToString())
                    throw new InvalidDataException("Confirmed owned member proof unavailable.");
                var origin = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == owner.Id && x.Generation == last.Intent.Generation, token);
                var originalInputs = origin.Inputs();
                var originalProvider = originalInputs.Providers.Entries[subject];
                var providerRevision = await db.ProviderConfigurationRevisions.AsNoTracking().SingleAsync(x => x.ProviderInstanceId == last.Intent.ProviderId && x.RevisionNumber == last.Intent.ConfigurationRevision, token);
                var originalVersion = await db.Revisions.AsNoTracking().Where(x => x.ApplicationRecordId == origin.ApplicationRecordId && x.LocalRevisionNumber == origin.RevisionNumber).Select(x => x.ApplicationVersion).SingleAsync(token);
                var originalHandler = handlers.SingleOrDefault(x => x.Contract == new InternalActionContract(last.Intent.SubjectContract, last.Intent.ProviderKind,
                    last.Intent.ConfigurationContract, last.Intent.Implementation, last.Intent.ActionContract, last.Intent.InputFormat, last.Intent.OutputContract)) as IInternalWorkloadUpdatePlanner
                    ?? throw new InvalidDataException("Confirmed member adapter unavailable.");
                var context = new InternalActionContext(new(ApplicationDeploymentId.Parse(owner.Id.ToString()), DeploymentGeneration.Create(origin.Generation), OperationId.Create(last.Intent.OperationId)),
                    originalInputs, subject, originalProvider, PreparedConfiguration(last.Intent.ProviderKind, providerRevision), originalVersion, last.Intent.Marker,
                    managed.Id, InternalPlacementInput.Decode(originalProvider.Placement!).Declaration);
                context = await InternalDurableActions.UpdateContextAsync(db, origin, context, (IInternalActionHandler)originalHandler, token);
                _ = originalHandler.CaptureUpdate(context, inputs, target.ApplicationVersion.Value, last.Intent.Payload, last.Output.Payload);
                var attempt = await db.DurableOperationAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == last.Operation.CurrentAttemptId && x.DeploymentId == owner.Id &&
                    x.Generation == last.Intent.Generation && x.OperationId == last.Intent.OperationId && x.AttemptNumber == last.Operation.AttemptNumber, token);
                if (attempt?.FinishedAt is null || attempt.DispatchedAt is null || attempt.DispatchedAt != last.Operation.DispatchedAt ||
                    attempt.FinishedAt != last.Operation.FinalizedAt || attempt.Outcome is not ("Succeeded" or "Uncertain")) throw new InvalidDataException("Confirmed attempt proof unavailable.");
                members.Add(new(subject.Component.Value, subject.Id.Value, "ConfirmedOwned", managed.Id, last.Intent.Generation, last.Intent.OperationId,
                    last.Operation.CurrentAttemptId, ResourcesReadyCheckpointCodec.Hash(last.Intent.Payload), ResourcesReadyCheckpointCodec.Hash(last.Output.Payload), ((IInternalWorkloadCorrectionPlanner)handler).CaptureConfirmedObject(context, last.Intent.Payload, last.Output.Payload), image, selected[subject].Select(x => x.Value).Order(StringComparer.Ordinal).ToArray()));
            }
            else
            {
                // Positive accepted initial target plus no dispatch anywhere in its complete history.
                if (!await HasOriginalInitialTargetAsync(db, source, subject, token) ||
                    managed is not null && objects.Any(x => x.ManagedWorkloadId == managed.Id && !db.RuntimeIdentityBindings.Any(b => b.ManagedWorkloadId == x.ManagedWorkloadId && b.Role == x.Role)) || managed is not null && states.Any(x => x.ManagedWorkloadId == managed.Id))
                    throw new InvalidDataException("Pending initial creation is not proven.");
                members.Add(new(subject.Component.Value, subject.Id.Value, "NeverConfirmedInitialCreate", managed?.Id, current?.Generation, current?.OperationId,
                    null, current is null ? null : ResourcesReadyCheckpointCodec.Hash(current.Payload), null, null, image, selected[subject].Select(x => x.Value).Order(StringComparer.Ordinal).ToArray()));
            }
        }
        if (!significantInput) throw new InvalidDataException("Unchanged desired input must resume its generation.");
        if (stable.Any(x => !members.Any(m => m.Component == x.Component && m.Workload == x.Workload))) throw new InvalidDataException("Unexplained managed member.");
        var ids = stable.Select(x => x.Id).ToArray();
        var bindings = await db.RuntimeIdentityBindings.AsNoTracking().Where(x => ids.Contains(x.ManagedWorkloadId)).OrderBy(x => x.Id).Take(513).ToArrayAsync(token);
        if (bindings.Any(x => x.Status != "Prepared")) throw new InvalidDataException("Revoked runtime identity cannot be revived.");
        var permissions = await db.RuntimeConfigurationPermissions.AsNoTracking().Where(x => bindings.Select(b => b.Id).Contains(x.BindingId)).OrderBy(x => x.Id).Take(1025).ToArrayAsync(token);
        var publications = new List<CorrectivePublication>();
        foreach (var permission in permissions)
        {
            var receipt = await db.RuntimePublicationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.RequestKey == permission.LastRequest, token)
                ?? throw new InvalidDataException("Publication receipt unavailable.");
            publications.Add(new(permission.Id, permission.BindingId, permission.RevisionId, permission.ConcurrencyToken, permission.Status,
                ResourcesReadyCheckpointCodec.Hash(permission.OriginPayload), permission.LastRequest, receipt.Id, ResourcesReadyCheckpointCodec.Hash(receipt.Payload)));
        }
        var preparations = await db.WorkloadConfigurationRecipients.AsNoTracking().Where(x => x.DeploymentId == owner.Id).OrderBy(x => x.Generation).ThenBy(x => x.Component).ThenBy(x => x.Workload).ThenBy(x => x.Document).Take(2049).ToArrayAsync(token);
        var historyQuery = db.DeploymentGenerations.AsNoTracking().Where(x => x.DeploymentId == owner.Id && x.Generation <= source.Generation);
        if (await historyQuery.CountAsync(token) > 256 || await historyQuery.SumAsync(x => (long)x.GraphJson.Length, token) > 16_777_216)
            throw new InvalidDataException("Corrective history exceeds bound.");
        var inherited = new List<string>();
        foreach (var generation in await db.DeploymentGenerations.AsNoTracking().Where(x => x.DeploymentId == owner.Id && x.Generation <= source.Generation).OrderBy(x => x.Generation).Take(257).ToArrayAsync(token))
        {
            var graph = DurableOperationGraphCodec.Decode(generation.GraphJson);
            inherited.Add(graph.Correction?.Fingerprint ?? graph.WorkloadUpdate?.Fingerprint ?? graph.Retirement?.Fingerprint ?? generation.Fingerprint);
        }
        var inventory = new CorrectiveInventory(new(1, owner.Id, owner.DesiredGeneration, owner.AppliedGeneration, source.PlanId, source.ChangeSetId, source.Fingerprint,
            proof.Id, ResourcesReadyCheckpointCodec.Encode(proof), members.ToArray(), objects.Select(x => new WorkloadUpdateObject(x.ManagedWorkloadId, x.Role, x.Contract, x.Format, x.Payload, x.Generation, x.OperationId))
                .Concat(states.Select(x => new WorkloadUpdateObject(x.ManagedWorkloadId, "Current/" + x.Role, x.Contract, x.Format, x.Payload, x.Generation, x.OperationId))).ToArray(),
            bindings.Select(x => new WorkloadUpdateBinding(x.Id, x.ManagedWorkloadId, x.ConcurrencyToken, x.Contract, x.Format, x.ValidationPayload)).ToArray(), publications.ToArray(),
            preparations.Select(x => new CorrectivePreparation(x.Generation, x.Component, x.Workload, x.Document, x.RevisionId, x.CheckpointId, x.InputFingerprint)).ToArray(), inherited.ToArray()));
        var reuse = new List<ResourceReuse>();
        foreach (var resource in proof.Resources)
        {
            var managed = await db.CurrentResourceManagement.AsNoTracking().SingleOrDefaultAsync(x => x.Id == resource.ManagementId && x.Deployment == owner.Id, token)
                ?? throw new InvalidDataException("Original Resource management unavailable.");
            var evidence = await ResourceContinuity.CorrectiveEvidenceAsync(db, managed, proof, token) ?? throw new InvalidDataException("Original Resource evidence unavailable.");
            reuse.Add(new(ManifestLocalId.Create(resource.Resource), resource.ManagementId, resource.SourceGeneration, evidence));
        }
        return (inputs, new DurableOperationGraph([], reuse, new(ApplicationDeploymentId.Parse(owner.Id.ToString()), source.Generation, source.Fingerprint))
        { Acquisitions = previous.Acquisitions }.WithCorrection(inventory));
    }

    private static async Task<bool> HasOriginalInitialTargetAsync(ManifestCatalogDbContext db, DeploymentGenerationRow source,
        ProviderInput.Workload subject, CancellationToken token)
    {
        var current = source;
        for (var count = 0; count < 256; count++)
        {
            var graph = DurableOperationGraphCodec.Decode(current.GraphJson);
            if (graph.Retirement is { } initial)
            {
                _ = await InternalGenerationAuthority.RequireStageReadAsync(db, current, token);
                return initial.Document.Targets.Any(x => x.Subject == "Workload/" + subject.Component.Value + "/" + subject.Id.Value);
            }
            if (graph.Correction?.Document is not { } correction || correction.ExpectedDesired >= current.Generation ||
                correction.Members.SingleOrDefault(x => x.Component == subject.Component.Value && x.Workload == subject.Id.Value)?.State != "NeverConfirmedInitialCreate") return false;
            var previous = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == source.DeploymentId && x.Generation == correction.ExpectedDesired, token);
            if (previous.PlanId != correction.OriginalPlan || previous.ChangeSetId != correction.OriginalChangeSet || previous.Fingerprint != correction.OriginalFingerprint) return false;
            current = previous;
        }
        return false;
    }
}
