using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Catalog-only checks. Mutation callers own the import gate and transaction.</summary>
internal static class ImportCorrectionRules
{
    internal static async Task<bool> EligibleAsync(ManifestCatalogDbContext db, OperatorImportRequestBinding binding, CancellationToken token)
    {
        if (binding.Correction is not { } target) return false;
        var environment = Guid.Parse(binding.Environment.ToString());
        var application = Guid.Parse(binding.Revision.ApplicationRecordId.ToString());
        var resource = binding.CredentialResource.Value;
        var previous = await db.OperatorImportRequestRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target.PreviousRequestId, token);
        if (previous is null || previous.Environment != environment || previous.Application != application || previous.CredentialResource != resource ||
            previous.Revision != binding.Revision.RevisionNumber.Value &&
                (target.Deployment is not null || target.Generation is not null || target.Reason != OperatorImportCorrectionReason.InputCorrection) ||
            await db.ImportSupersessions.AsNoTracking().AnyAsync(x => x.PreviousRequest == previous.Id, token)) return false;
        var owner = await db.ProtectedCredentialOwners.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Environment == environment && x.Application == application && x.Resource == resource, token);
        var stage = await db.ProtectedApplicationCredentials.AsNoTracking().Where(x => x.Request == previous.Id)
            .Select(x => new { x.Id, x.Environment, x.Application, x.Resource, x.ConfirmedReceipt, x.Secret, x.Version }).SingleOrDefaultAsync(token);
        if (stage is null)
        {
            if (owner is not null || previous.StagedCredential is not null || previous.PreviousRequest is not null) return false;
        }
        else if (stage.Environment != environment || stage.Application != application || stage.Resource != resource || stage.ConfirmedReceipt is not null ||
            owner is null || owner.Stage != stage.Id || owner.Request != previous.Id ||
            await db.Secrets.AsNoTracking().AnyAsync(x => x.Id == stage.Secret, token) ||
            await db.SecretVersions.AsNoTracking().AnyAsync(x => x.Id == stage.Version, token)) return false;
        var managed = await db.CurrentResourceManagement.AsNoTracking().Where(x => x.Environment == environment && x.Application == application).ToArrayAsync(token);
        var deployment = await db.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x =>
            x.EnvironmentRecordId == environment && x.ApplicationRecordId == application, token);
        var commit = previous.ChangeSet is { } change
            ? await db.ChangeSetCommits.AsNoTracking().SingleOrDefaultAsync(x => x.ChangeSetId == change, token) : null;
        if (target.Deployment is null)
        {
            if (target.Generation is not null || commit is not null) return false;
            if (deployment is null) return managed.Length == 0;
            // Pre-Commit correction of a new group cannot replace any already managed Resource.
            return deployment.AppliedGeneration == deployment.DesiredGeneration &&
                binding.Input.Resources.All(x => managed.All(m => m.Resource != x.Resource.Value)) &&
                (previous.ChangeSet is null || await db.DurableChangeSets.AsNoTracking().AnyAsync(x => x.Id == previous.ChangeSet &&
                    x.ImportIntentJson == null && !x.ExpectedAbsence && x.ExpectedGeneration == deployment.AppliedGeneration, token));
        }
        var expectedDeployment = Guid.Parse(target.Deployment.ToString());
        if (deployment is null || commit is null || target.Generation is null || deployment.Id != expectedDeployment ||
            deployment.DesiredGeneration != target.Generation.Value || deployment.AppliedGeneration is not null ||
            commit.DeploymentId != expectedDeployment || commit.Generation != target.Generation.Value || managed.Length == 0 ||
            managed.Any(x => x.Deployment != expectedDeployment || x.SourceGeneration != target.Generation.Value ||
                x.Origin != ResourceOrigin.Imported || x.Authority != ResourceLifecycleAuthority.UseAndRetain)) return false;
        return await UnconfirmedGenerationAsync(db, expectedDeployment, target.Generation.Value, token);
    }

    internal static async Task<bool> UnconfirmedGenerationAsync(ManifestCatalogDbContext db, Guid deployment, long generation, CancellationToken token)
    {
        var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == generation, token);
        if (source is null || source.InternalEffects || source.Inputs().Import is null ||
            await db.ResourceObservationReceipts.AsNoTracking().AnyAsync(x => x.Deployment == deployment, token) ||
            await db.OperationOutputs.AsNoTracking().AnyAsync(x => x.DeploymentId == deployment, token)) return false;
        var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        if (graph.Baseline is not null || graph.ReusedResources.Count != 0 || graph.RetainedResources.Count != 0 || graph.Nodes.Count == 0 ||
            graph.Nodes.Any(x => x.Action.Admission.Mode != ResourceAcquisitionMode.ImportUseAndRetain)) return false;
        var managed = await db.CurrentResourceManagement.AsNoTracking().Where(x => x.Deployment == deployment).ToArrayAsync(token);
        if (!graph.Nodes.Select(x => x.ResourceId.Value).ToHashSet(StringComparer.Ordinal).SetEquals(managed.Select(x => x.Resource)) ||
            managed.Any(x => x.SourceGeneration != generation || x.Origin != ResourceOrigin.Imported || x.Authority != ResourceLifecycleAuthority.UseAndRetain)) return false;
        var operations = await db.DurableOperations.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == generation).ToArrayAsync(token);
        if (!graph.Nodes.Select(x => x.Id.Value).ToHashSet(StringComparer.Ordinal).SetEquals(operations.Select(x => x.OperationId)) ||
            operations.Any(x => x.State is not (nameof(DurableOperationState.Pending) or nameof(DurableOperationState.Failed)) ||
                x.ReasonCode == nameof(DurableOperationReason.UncertainAfterDispatch))) return false;
        return !await db.DurableOperationAttempts.AsNoTracking().AnyAsync(x => x.DeploymentId == deployment && x.Generation == generation &&
            (x.FinishedAt == null || x.Outcome == nameof(DurableAttemptOutcome.Uncertain) || x.Outcome == nameof(DurableAttemptOutcome.Succeeded)), token);
    }
}
