using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Exact protected request resolution. Callers own the import gate and transaction.</summary>
internal static class GenericImportIntake
{
    internal static bool Matches(DurableChangeSetRow change, OperatorImportRequestRow row, ProtectedCredentialReference credential)
    {
        if (change.ImportIntentJson is not null || change.AcquisitionIntentJson is null) return false;
        var acquired = ResourceAcquisitionIntentCodec.Decode(change.AcquisitionIntentJson).Resources.Where(x => x.ImportRequestId == row.Id).ToArray();
        var bound = PostgreSqlDurableDeploymentPlanner.StoredImportBinding(row).Attach(credential).Resources;
        return acquired.Length == bound.Count && bound.All(x => acquired.Any(a => a.Resource == x.Resource &&
            a.Mode == ResourceAcquisitionMode.ImportUseAndRetain && a.Input == x.Input && a.Credential == x.Credential && a.SharingRequest is null));
    }

    internal static async Task<ResourceAcquisitionIntent?> ResolveAsync(ManifestCatalogDbContext db, EnvironmentRecordId environment,
        ManifestRevisionRef revision, ResourceAcquisitionIntent requested, CancellationToken token)
    {
        var result = requested.Resources.ToList();
        foreach (var group in requested.Resources.Where(x => x.Mode == ResourceAcquisitionMode.ImportUseAndRetain).GroupBy(x => x.ImportRequestId))
        {
            if (group.Key is not { } request || request == Guid.Empty || group.Any(x => x.Input is not null || x.Credential is not null)) return null;
            var row = await db.OperatorImportRequestRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request, token);
            if (row is null || row.Environment != Guid.Parse(environment.ToString()) || row.Application != Guid.Parse(revision.ApplicationRecordId.ToString()) ||
                row.Revision != revision.RevisionNumber.Value || await db.ImportSupersessions.AsNoTracking().AnyAsync(x => x.PreviousRequest == request, token)) return null;
            var binding = PostgreSqlDurableDeploymentPlanner.StoredImportBinding(row);
            if (!binding.Input.Resources.Select(x => x.Resource).ToHashSet().SetEquals(group.Select(x => x.Resource))) return null;
            var staged = await db.ProtectedApplicationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Request == request, token);
            if (staged is null || staged.Environment != row.Environment || staged.Application != row.Application || staged.Resource != row.CredentialResource || staged.ConfirmedReceipt is not null) return null;
            if (binding.Correction is not null)
            {
                if (!await ImportCorrectionRules.EligibleAsync(db, binding, token)) return null;
            }
            else if (!await db.ProtectedCredentialOwners.AsNoTracking().AnyAsync(x => x.Environment == row.Environment && x.Application == row.Application &&
                x.Resource == row.CredentialResource && x.Stage == staged.Id && x.Request == row.Id, token)) return null;
            result.RemoveAll(x => x.ImportRequestId == request);
            result.AddRange(binding.Attach(PostgreSqlProtectedApplicationCredentialIntake.Reference(staged)).Resources.Select(x =>
                new ResourceAcquisitionRequest(x.Resource, ResourceAcquisitionMode.ImportUseAndRetain, x.Input, x.Credential) { ImportRequestId = request }));
        }
        return new(result);
    }

    internal static async Task<bool> ValidateAsync(ManifestCatalogDbContext db, DurableChangeSetRow change, CancellationToken token)
    {
        var acquisitions = ResourceAcquisitionIntentCodec.Decode(change.AcquisitionIntentJson!);
        var linked = await db.OperatorImportRequestRecords.AsNoTracking().Where(x => x.ChangeSet == change.Id).Select(x => x.Id).ToArrayAsync(token);
        var ownRequests = acquisitions.Resources.Where(x => x.ImportRequestId is not null).Select(x => x.ImportRequestId!.Value).Distinct().ToArray();
        if (change.ImportIntentJson is null && linked.Any(x => !ownRequests.Contains(x))) return false;
        foreach (var group in acquisitions.Resources.Where(x => x.ImportRequestId is not null).GroupBy(x => x.ImportRequestId!.Value))
        {
            var row = await db.OperatorImportRequestRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == group.Key, token);
            if (row is null || row.ChangeSet is null || row.Environment != change.EnvironmentRecordId || row.Application != change.ApplicationRecordId ||
                row.StagedCredential is not { } stageId ||
                await db.ImportSupersessions.AsNoTracking().AnyAsync(x => x.PreviousRequest == row.Id, token)) return false;
            var staged = await PostgreSqlDurableDeploymentPlanner.ReadRequestCredentialAsync(db, stageId, token);
            if (!PostgreSqlDurableDeploymentPlanner.MatchesRequestCredential(staged, row)) return false;
            var reference = new ProtectedCredentialReference(staged!.Id, new(SecretId.Parse(staged.Secret.ToString()), SecretVersionId.Parse(staged.Version.ToString())));
            if (!Matches(change, row, reference) || row.ChangeSet == change.Id && row.Revision != change.RevisionNumber) return false;
            if (row.PreviousRequest is not null && !await db.ChangeSetCommits.AsNoTracking().AnyAsync(x => x.ChangeSetId == row.ChangeSet, token))
            {
                if (!await ImportCorrectionRules.EligibleAsync(db, PostgreSqlDurableDeploymentPlanner.StoredImportBinding(row), token)) return false;
            }
            else if (!await db.ProtectedCredentialOwners.AsNoTracking().AnyAsync(x => x.Stage == stageId && x.Request == row.Id &&
                x.Environment == row.Environment && x.Application == row.Application && x.Resource == row.CredentialResource, token)) return false;
        }
        return true;
    }

    internal static async Task<bool> AcceptCorrectionsAsync(ManifestCatalogDbContext db, DurableChangeSetRow change, ChangeSetCommitRow commit, CancellationToken token)
    {
        foreach (var successor in await db.OperatorImportRequestRecords.AsNoTracking().Where(x => x.ChangeSet == change.Id && x.PreviousRequest != null).ToArrayAsync(token))
        {
            var binding = PostgreSqlDurableDeploymentPlanner.StoredImportBinding(successor);
            var target = binding.Correction!;
            if (target.Deployment is not null || target.Generation is not null || successor.StagedCredential is not { } newStage) return false;
            var previous = await db.OperatorImportRequestRecords.AsNoTracking().SingleAsync(x => x.Id == target.PreviousRequestId, token);
            if (previous.Environment != successor.Environment || previous.Application != successor.Application || previous.CredentialResource != successor.CredentialResource ||
                previous.ChangeSet is { } oldChange && await db.ChangeSetCommits.AsNoTracking().AnyAsync(x => x.ChangeSetId == oldChange, token) ||
                await db.ImportSupersessions.AsNoTracking().AnyAsync(x => x.PreviousRequest == previous.Id, token)) return false;
            var oldStage = await db.ProtectedApplicationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Request == previous.Id, token);
            var staged = await PostgreSqlDurableDeploymentPlanner.ReadRequestCredentialAsync(db, newStage, token);
            if (!PostgreSqlDurableDeploymentPlanner.MatchesRequestCredential(staged, successor) || staged!.ConfirmedReceipt is not null ||
                oldStage?.ConfirmedReceipt is not null) return false;
            if (oldStage is null)
            {
                if (await db.ProtectedCredentialOwners.AsNoTracking().AnyAsync(x => x.Environment == successor.Environment && x.Application == successor.Application && x.Resource == successor.CredentialResource, token)) return false;
                db.ProtectedCredentialOwners.Add(new() { Environment = successor.Environment, Application = successor.Application, Resource = successor.CredentialResource, Stage = newStage, Request = successor.Id });
            }
            else if (await db.ProtectedCredentialOwners.Where(x => x.Environment == successor.Environment && x.Application == successor.Application &&
                x.Resource == successor.CredentialResource && x.Stage == oldStage.Id && x.Request == previous.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Stage, newStage).SetProperty(x => x.Request, successor.Id), token) != 1) return false;
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
            db.AuthorizeImportSupersession(audit, successor.Environment, successor.Application);
            db.ImportSupersessions.Add(audit);
        }
        await db.SaveChangesAsync(token);
        return true;
    }

    internal static async Task<bool> LinkAsync(ManifestCatalogDbContext db, DeploymentChangeSet change, CancellationToken token)
    {
        foreach (var request in change.Acquisitions!.Resources.Where(x => x.ImportRequestId is not null).Select(x => x.ImportRequestId!.Value).Distinct())
        {
            var row = await db.OperatorImportRequestRecords.SingleAsync(x => x.Id == request, token);
            if (row.ChangeSet is not null) continue;
            var stage = await db.ProtectedApplicationCredentials.AsNoTracking().SingleAsync(x => x.Request == request, token);
            row.ChangeSet = Guid.Parse(change.Id.ToString()); row.StagedCredential = stage.Id;
            row.CompletedAt = change.CreatedAt; row.CompletedBy = change.ActorId;
        }
        return true;
    }
}
