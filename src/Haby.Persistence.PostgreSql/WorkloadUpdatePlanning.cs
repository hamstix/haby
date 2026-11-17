using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class PostgreSqlDurableDeploymentPlanner
{
    internal static async Task<bool> HasAppliedBaselineProofAsync(ManifestCatalogDbContext db, Guid deployment, long generation, CancellationToken token)
    {
        var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == generation, token);
        if (source is null) return false;
        if (!source.InternalEffects) return await DurableAggregateCompletion.HasProofAsync(db, deployment, generation, token);
        try { _ = await InitialRetirementCompletion.RequireProofAsync(db, source, token); return true; }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException) { return false; }
    }
    internal static async Task<bool> HasWorkloadBaselineAsync(ManifestCatalogDbContext db, ApplicationDeploymentRow? owner, CancellationToken token) =>
        owner is not null && await db.DeploymentGenerations.AsNoTracking().AnyAsync(x => x.DeploymentId == owner.Id &&
            x.Generation == owner.DesiredGeneration && x.InternalEffects, token);

    internal async Task<WorkloadUpdateInventory> CaptureWorkloadUpdateAsync(ManifestCatalogDbContext db,
        ApplicationDeploymentRow owner, DurablePlanningInputs inputs, DurableOperationGraph graph,
        string version, IReadOnlyList<IInternalActionHandler> handlers,
        IReadOnlyDictionary<ProviderInput.Workload, IReadOnlyList<ManifestLocalId>> selected, CancellationToken token)
    {
        if (owner.AppliedGeneration != owner.DesiredGeneration || graph.Nodes.Count != 0 || graph.RetainedResources.Count != 0 ||
            graph.ReusedResources.Count != inputs.Providers.Declaration.Spec.Resources.Count || graph.ReusedResources.Any(x => x.Reintroduced) ||
            graph.Baseline?.Generation != owner.DesiredGeneration || graph.Baseline.Deployment.ToString() != owner.Id.ToString())
            throw new WorkloadUpdateRefusedException(WorkloadUpdateRefusal.BaselineUnavailable);
        var source = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == owner.Id && x.Generation == owner.DesiredGeneration, token);
        source = await InternalGenerationAuthority.RequireReadAsync(db, source, token);
        var proof = await InitialRetirementCompletion.RequireProofAsync(db, source, token);
        var completion = await db.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.Id == proof.Id, token);
        var previous = source.Inputs();
        var equivalent = previous.Revision == inputs.Revision && previous.Configuration!.Equals(inputs.Configuration) && previous.Providers.Equals(inputs.Providers);
        if (source.Fingerprint != graph.Baseline.Fingerprint)
            throw new WorkloadUpdateRefusedException(WorkloadUpdateRefusal.InputChanged);
        var previousVersion = await db.Revisions.AsNoTracking().Where(x => x.ApplicationRecordId == source.ApplicationRecordId &&
            x.LocalRevisionNumber == source.RevisionNumber).Select(x => x.ApplicationVersion).SingleAsync(token);
        var managed = await db.ManagedWorkloads.AsNoTracking().Where(x => x.DeploymentId == owner.Id).OrderBy(x => x.Component).ThenBy(x => x.Workload).ToArrayAsync(token);
        if (managed.Length != selected.Count || managed.Any(x => x.EnvironmentId != source.EnvironmentRecordId || x.ApplicationId != source.ApplicationRecordId) ||
            managed.Any(x => !selected.Keys.Any(s => s.Component.Value == x.Component && s.Id.Value == x.Workload)))
            throw new WorkloadUpdateRefusedException(WorkloadUpdateRefusal.EvidenceUnavailable);
        var members = new List<WorkloadUpdateMember>();
        foreach (var stable in managed)
        {
            var subject = new ProviderInput.Workload(ManifestLocalId.Create(stable.Component), ManifestLocalId.Create(stable.Workload));
            var exact = inputs.Providers.Entries[subject];
            var candidates = await db.ActionIntents.AsNoTracking().Where(x => x.DeploymentId == owner.Id && x.Generation == source.Generation &&
                x.SubjectKind == "Workload" && x.Component == stable.Component && x.LocalId == stable.Workload).ToArrayAsync(token);
            var matched = candidates.SelectMany(intent => handlers.Where(h => h is IInternalWorkloadUpdatePlanner &&
                h.Contract == new InternalActionContract(intent.SubjectContract, intent.ProviderKind, intent.ConfigurationContract,
                    intent.Implementation, intent.ActionContract, intent.InputFormat, intent.OutputContract)).Select(h => (Intent: intent, Handler: h))).ToArray();
            if (matched is not [var match]) throw new WorkloadUpdateRefusedException(WorkloadUpdateRefusal.UnsupportedWorkload);
            var intent = match.Intent;
            var operation = await db.DurableOperations.AsNoTracking().SingleAsync(x => x.DeploymentId == owner.Id && x.Generation == source.Generation && x.OperationId == intent.OperationId, token);
            var output = await db.OperationOutputs.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == owner.Id && x.Generation == source.Generation && x.OperationId == intent.OperationId, token);
            var observation = await db.ActionObservations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == owner.Id && x.Generation == source.Generation && x.OperationId == intent.OperationId && x.Confirmed, token);
            if (operation.State != nameof(DurableOperationState.Succeeded) || operation.CurrentAttemptId is null || output is null ||
                intent.ManagedIdentity != stable.Id || stable.ProviderId.ToString() != exact.Instance.ToString() || intent.ProviderId != stable.ProviderId ||
                intent.ConfigurationRevision != exact.Configuration.Value || intent.PlacementId != InternalPlacementInput.Decode(exact.Placement!).Id ||
                intent.InputFingerprint != source.Fingerprint || intent.CheckpointId != proof.ResourcesReady)
                throw new WorkloadUpdateRefusedException(WorkloadUpdateRefusal.EvidenceUnavailable);
            var provider = await db.ProviderConfigurationRevisions.AsNoTracking().SingleAsync(x => x.ProviderInstanceId == intent.ProviderId && x.RevisionNumber == intent.ConfigurationRevision, token);
            var oldContext = new InternalActionContext(new(ApplicationDeploymentId.Parse(owner.Id.ToString()), DeploymentGeneration.Create(source.Generation), OperationId.Create(intent.OperationId)),
                previous, subject, previous.Providers.Entries[subject], PreparedConfiguration(intent.ProviderKind, provider), previousVersion, intent.Marker, stable.Id, InternalPlacementInput.Decode(exact.Placement!).Declaration);
            if (match.Handler.ManagedTarget(oldContext) != new InternalManagedTarget(stable.TargetContract, stable.TargetFormat, stable.TargetPayload) ||
                output.Contract != intent.OutputContract) throw new WorkloadUpdateRefusedException(WorkloadUpdateRefusal.EvidenceUnavailable);
            var delta = ((IInternalWorkloadUpdatePlanner)match.Handler).CaptureUpdate(oldContext, inputs, version, intent.Payload, output.Payload);
            members.Add(new(stable.Component, stable.Workload, stable.Id, stable.TargetContract, stable.TargetFormat,
                stable.TargetPayload, intent.OperationId, operation.CurrentAttemptId.Value, observation?.Id, delta,
                equivalent ? [] : selected[subject].Select(x => x.Value).Order(StringComparer.Ordinal).ToArray()));
        }
        var objects = await db.OwnedObjects.AsNoTracking().Where(x => x.DeploymentId == owner.Id)
            .OrderBy(x => x.ManagedWorkloadId).ThenBy(x => x.Role).ToArrayAsync(token);
        var ids = managed.Select(x => x.Id).ToArray();
        var bindings = await db.RuntimeIdentityBindings.AsNoTracking().Where(x => ids.Contains(x.ManagedWorkloadId)).OrderBy(x => x.Id).ToArrayAsync(token);
        var publications = proof.Publications.OrderBy(x => x.Permission).ToArray();
        foreach (var binding in bindings)
            if (binding.Status != "Prepared") throw new WorkloadUpdateRefusedException(WorkloadUpdateRefusal.EvidenceUnavailable);
        foreach (var publication in publications)
            if (!await db.RuntimeConfigurationPermissions.AnyAsync(x => x.Id == publication.Permission && x.Status == "Published" &&
                x.ConcurrencyToken == publication.PermissionToken, token)) throw new WorkloadUpdateRefusedException(WorkloadUpdateRefusal.EvidenceUnavailable);
        // Every current permission for the retained identities must be represented. Refuse
        // unexplained live rights rather than silently dropping retirement obligations.
        var bindingIds = bindings.Select(x => x.Id).ToArray();
        var permissions = await db.RuntimeConfigurationPermissions.AsNoTracking().Where(x => bindingIds.Contains(x.BindingId)).Select(x => x.Id).ToArrayAsync(token);
        if (!permissions.ToHashSet().SetEquals(publications.Select(x => x.Permission)))
            throw new WorkloadUpdateRefusedException(WorkloadUpdateRefusal.EvidenceUnavailable);
        var publicationInventory = publications.Select(x => new WorkloadUpdatePublication(x.Permission, x.Binding, x.Revision, x.PermissionToken,
            x.BindingToken, x.Request, x.Receipt, x.ReceiptHash)).ToArray();
        return new(new(1, owner.Id, source.Generation, equivalent ? source.Generation : checked(source.Generation + 1), source.PlanId, source.ChangeSetId, source.Fingerprint,
            proof.Id, completion.EvidenceFingerprint, members.ToArray(), objects.Select(x => new WorkloadUpdateObject(x.ManagedWorkloadId,
                x.Role, x.Contract, x.Format, x.Payload, x.Generation, x.OperationId)).ToArray(),
            bindings.Select(x => new WorkloadUpdateBinding(x.Id, x.ManagedWorkloadId, x.ConcurrencyToken, x.Contract, x.Format, x.ValidationPayload)).ToArray(),
            equivalent ? publicationInventory : [], equivalent ? [] : publicationInventory));
    }
}
