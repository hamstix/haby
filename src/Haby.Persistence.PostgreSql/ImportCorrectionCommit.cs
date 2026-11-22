using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal static class ImportCorrectionCommit
{
    // Called after the new generation/commit/history rows are saved, before the owning transaction commits.
    internal static async Task<bool> ApplyAsync(ManifestCatalogDbContext db, OperatorImportRequestBinding binding,
        ChangeSetCommitRow commit, IReadOnlyList<ResourceManagementRow> management, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null || binding.Correction is not { } target) return false;
        var environment = Guid.Parse(binding.Environment.ToString());
        var application = Guid.Parse(binding.Revision.ApplicationRecordId.ToString());
        var resource = binding.CredentialResource.Value;
        var successor = await db.OperatorImportRequestRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding.RequestId, token);
        var previous = await db.OperatorImportRequestRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target.PreviousRequestId, token);
        if (successor is null || previous is null || !PostgreSqlDurableDeploymentPlanner.SameImportRequest(successor, binding) ||
            successor.ChangeSet != commit.ChangeSetId || successor.StagedCredential is not { } newStage || previous.Environment != environment ||
            previous.Application != application || previous.Revision != successor.Revision || previous.CredentialResource != resource ||
            await db.ImportSupersessions.AsNoTracking().AnyAsync(x => x.PreviousRequest == previous.Id, token)) return false;
        var stage = await PostgreSqlDurableDeploymentPlanner.ReadRequestCredentialAsync(db, newStage, token);
        if (!PostgreSqlDurableDeploymentPlanner.MatchesRequestCredential(stage, successor) || stage!.ConfirmedReceipt is not null) return false;
        var oldStage = await db.ProtectedApplicationCredentials.AsNoTracking().Where(x => x.Request == previous.Id)
            .Select(x => new { x.Id, x.ConfirmedReceipt, x.Secret, x.Version }).SingleOrDefaultAsync(token);
        if (oldStage is not null && (oldStage.ConfirmedReceipt is not null ||
            await db.Secrets.AsNoTracking().AnyAsync(x => x.Id == oldStage.Secret, token) ||
            await db.SecretVersions.AsNoTracking().AnyAsync(x => x.Id == oldStage.Version, token))) return false;
        if (!await db.ApplicationDeployments.AsNoTracking().AnyAsync(x => x.Id == commit.DeploymentId && x.EnvironmentRecordId == environment &&
            x.ApplicationRecordId == application && x.DesiredGeneration == commit.Generation && x.AppliedGeneration == null, token)) return false;
        var heads = await db.CurrentResourceManagement.AsNoTracking().Where(x => x.Environment == environment && x.Application == application).ToArrayAsync(token);
        if (target.Deployment is null)
        {
            if (commit.Generation != 1 || heads.Length != 0 || previous.ChangeSet is { } oldChange &&
                await db.ChangeSetCommits.AsNoTracking().AnyAsync(x => x.ChangeSetId == oldChange, token)) return false;
        }
        else
        {
            if (target.Generation is null || Guid.Parse(target.Deployment.ToString()) != commit.DeploymentId ||
                commit.Generation != checked(target.Generation.Value + 1) || previous.ChangeSet is not { } oldChange ||
                !await db.ChangeSetCommits.AsNoTracking().AnyAsync(x => x.ChangeSetId == oldChange && x.DeploymentId == commit.DeploymentId &&
                    x.Generation == target.Generation.Value, token) ||
                !await ImportCorrectionRules.UnconfirmedGenerationAsync(db, commit.DeploymentId, target.Generation.Value, token) ||
                !heads.Select(x => x.Resource).ToHashSet(StringComparer.Ordinal).SetEquals(management.Select(x => x.Resource))) return false;
        }
        if (management.Count == 0 || management.Any(x => x.Environment != environment || x.Application != application || x.Deployment != commit.DeploymentId ||
            x.SourceGeneration != commit.Generation || x.Origin != ResourceOrigin.Imported || x.Authority != ResourceLifecycleAuthority.UseAndRetain)) return false;
        var owner = await db.ProtectedCredentialOwners.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Environment == environment && x.Application == application && x.Resource == resource, token);
        if (oldStage is null)
        {
            if (owner is not null || previous.StagedCredential is not null || previous.PreviousRequest is not null) return false;
            db.ProtectedCredentialOwners.Add(new() { Environment = environment, Application = application, Resource = resource, Stage = newStage, Request = binding.RequestId });
        }
        else
        {
            if (owner is null || owner.Stage != oldStage.Id || owner.Request != previous.Id ||
                await db.ProtectedCredentialOwners.Where(x => x.Environment == environment && x.Application == application && x.Resource == resource &&
                    x.Stage == oldStage.Id && x.Request == previous.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Stage, newStage)
                        .SetProperty(x => x.Request, binding.RequestId), token) != 1) return false;
        }
        foreach (var row in management)
        {
            if (target.Deployment is null) db.ResourceManagementHeads.Add(new()
            { Environment = environment, Application = application, Resource = row.Resource, Management = row.Id });
            else
            {
                var old = heads.Single(x => x.Resource == row.Resource);
                if (await db.ResourceManagementHeads.Where(x => x.Environment == environment && x.Application == application && x.Resource == row.Resource &&
                    x.Management == old.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Management, row.Id), token) != 1) return false;
            }
        }
        var audit = new ImportSupersessionRow
        {
            PreviousRequest = previous.Id,
            SuccessorRequest = successor.Id,
            PreviousStage = oldStage?.Id,
            SuccessorStage = newStage,
            ChangeSet = commit.ChangeSetId,
            Deployment = commit.DeploymentId,
            Generation = commit.Generation,
            Actor = commit.ActorId,
            AcceptedAt = commit.CommittedAt,
            Reason = target.Reason.ToString()
        };
        db.AuthorizeImportSupersession(audit, environment, application);
        db.ImportSupersessions.Add(audit);
        await db.SaveChangesAsync(token);
        return true;
    }
}
