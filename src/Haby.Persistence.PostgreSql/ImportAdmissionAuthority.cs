using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal static class ImportAdmissionAuthority
{
    internal static async Task<OperatorImportRequestBinding?> CorrectionAsync(ManifestCatalogDbContext db, Guid change, CancellationToken token)
    {
        var requests = await db.OperatorImportRequestRecords.AsNoTracking().Where(x => x.ChangeSet == change).Take(2).ToArrayAsync(token);
        if (requests.Length > 1) return null;
        var request = requests.SingleOrDefault();
        return request?.PreviousRequest is null ? null : PostgreSqlDurableDeploymentPlanner.StoredImportBinding(request);
    }

    internal static async Task<bool> CanPlanAsync(ManifestCatalogDbContext db, DurableChangeSetRow change,
        OperatorImportRequestBinding? correction, CancellationToken token)
    {
        var intent = ResourceImportIntentCodec.Decode(change.ImportIntentJson);
        if (intent is not null && await db.OperatorImportRequestRecords.AsNoTracking().CountAsync(x => x.ChangeSet == change.Id, token) > 1) return false;
        if (intent is null) return correction is null;
        if (correction is not null)
        {
            if (!await ImportCorrectionRules.EligibleAsync(db, correction, token)) return false;
            var request = await db.OperatorImportRequestRecords.AsNoTracking().SingleAsync(x => x.Id == correction.RequestId, token);
            if (request.StagedCredential is not { } id || request.ChangeSet != change.Id ||
                intent.Resources.SingleOrDefault(x => x.Resource == correction.CredentialResource)?.Credential is not { } candidate || candidate.Id != id)
                return false;
            var stage = await PostgreSqlDurableDeploymentPlanner.ReadRequestCredentialAsync(db, id, token);
            return PostgreSqlDurableDeploymentPlanner.MatchesRequestCredential(stage, request) && stage!.ConfirmedReceipt is null &&
                stage.Secret == Guid.Parse(candidate.Reference.SecretId.ToString()) && stage.Version == Guid.Parse(candidate.Reference.SecretVersionId.ToString());
        }
        return await OwnsInputsAsync(db, change.EnvironmentRecordId, change.ApplicationRecordId, intent, token);
    }

    internal static async Task<bool> OwnsInputsAsync(ManifestCatalogDbContext db, Guid environment, Guid application,
        ResourceImportIntent intent, CancellationToken token)
    {
        var credentials = intent.Resources.Where(x => x.Credential is not null).ToArray();
        foreach (var item in credentials)
        {
            var reference = item.Credential!;
            var stage = await PostgreSqlDurableDeploymentPlanner.ReadRequestCredentialAsync(db, reference.Id, token);
            if (stage is null || stage.Environment != environment || stage.Application != application || stage.Resource != item.Resource.Value ||
                stage.Secret != Guid.Parse(reference.Reference.SecretId.ToString()) || stage.Version != Guid.Parse(reference.Reference.SecretVersionId.ToString()) ||
                !await db.ProtectedCredentialOwners.AsNoTracking().AnyAsync(x => x.Environment == environment && x.Application == application &&
                    x.Resource == item.Resource.Value && x.Stage == stage.Id && x.Request == stage.Request, token) ||
                await db.ImportSupersessions.AsNoTracking().AnyAsync(x => x.PreviousRequest == stage.Request, token)) return false;
        }
        return true;
    }

    internal static async Task<bool> BeforeExecutionAsync(ManifestCatalogDbContext db, Guid deployment, long generation, CancellationToken token)
    {
        var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == generation, token);
        if (source is null) return false;
        var inputs = source.Inputs();
        var intent = inputs.Import;
        if (inputs.Acquisitions.Resources.Any(x => x.Mode == ResourceAcquisitionMode.Reference))
        {
            if (source.InternalEffects)
            {
                var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
                if (graph.Nodes.Count != 0 || graph.RetainedResources.Count != 0 ||
                    graph.ReusedResources.Count != inputs.Providers.Declaration.Spec.Resources.Count) return false;
                await ImportMutationGate.LockAsync(db, source.EnvironmentRecordId, source.ApplicationRecordId, token);
                return await db.ApplicationDeployments.AsNoTracking().AnyAsync(x => x.Id == deployment && x.DesiredGeneration == generation, token) &&
                    await ResourceContinuity.ValidateAsync(db, deployment, graph, token);
            }
            await ImportMutationGate.LockAsync(db, source.EnvironmentRecordId, source.ApplicationRecordId, token);
            if (!await db.ApplicationDeployments.AsNoTracking().AnyAsync(x => x.Id == deployment && x.DesiredGeneration == generation, token)) return false;
            foreach (var managed in await db.ResourceManagement.AsNoTracking().Where(x => x.Deployment == deployment && x.SourceGeneration == generation).ToArrayAsync(token))
                if (!ResourceContinuity.HasAcquisitionAuthority(managed, inputs) || !await ResourceObservationReceiptIntegrity.IsActiveAsync(db, managed.Id, token)) return false;
        }
        if (intent is null)
        {
            var imported = inputs.Acquisitions.Resources.Where(x => x.Mode == ResourceAcquisitionMode.ImportUseAndRetain).ToArray();
            return imported.Length == 0 || await OwnsInputsAsync(db, source.EnvironmentRecordId, source.ApplicationRecordId,
                new(imported.Select(x => new ResourceImportRequest(x.Resource, x.Input!, x.Credential))), token);
        }
        if (source.InternalEffects) return false;
        await ImportMutationGate.LockAsync(db, source.EnvironmentRecordId, source.ApplicationRecordId, token);
        source = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment && x.Generation == generation, token);
        return await db.ApplicationDeployments.AsNoTracking().AnyAsync(x => x.Id == deployment && x.DesiredGeneration == generation, token) &&
            source.Inputs().Import is { } frozen && await OwnsInputsAsync(db, source.EnvironmentRecordId, source.ApplicationRecordId, frozen, token);
    }
}
