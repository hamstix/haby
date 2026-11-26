using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    private readonly HashSet<(Guid Transaction, Guid Environment, Guid Application)> _importGates = [];
    private readonly HashSet<ImportSupersessionRow> _authorizedSupersessions = [];
    private Guid? _supersessionTransaction;

    internal void RecordImportGate(Guid transaction, Guid environment, Guid application) => _importGates.Add((transaction, environment, application));
    internal bool HoldsImportGate(Guid environment, Guid application) => Database.CurrentTransaction is { } transaction &&
        _importGates.Contains((transaction.TransactionId, environment, application));

    internal void AuthorizeImportSupersession(ImportSupersessionRow row, Guid environment, Guid application)
    {
        if (!HoldsImportGate(environment, application) || _supersessionTransaction is { } transaction && Database.CurrentTransaction?.TransactionId != transaction)
            throw new InvalidOperationException("Import supersession needs its owning gated transaction.");
        if (!_authorizedSupersessions.Add(row)) throw new InvalidOperationException("Duplicate import supersession authorization.");
        _supersessionTransaction = Database.CurrentTransaction!.TransactionId;
    }

    private async Task ValidateImportAuthorityWritesAsync(bool asynchronous, CancellationToken token)
    {
        if (ChangeTracker.Entries<ImportSupersessionRow>().Any(x => x.State is EntityState.Modified or EntityState.Deleted) ||
            ChangeTracker.Entries<ProtectedCredentialOwnerRow>().Any(x => x.State is EntityState.Modified or EntityState.Deleted) ||
            ChangeTracker.Entries<ResourceManagementHeadRow>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Import history is immutable and current authority requires guarded compare-and-swap.");
        var audits = ChangeTracker.Entries<ImportSupersessionRow>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToArray();
        if (!_authorizedSupersessions.SetEquals(audits)) throw new InvalidOperationException("Import supersession authorization membership differs.");
        foreach (var change in audits.Select(x => x.ChangeSet).Distinct())
        {
            var expectedQuery = OperatorImportRequestRecords.AsNoTracking().Where(x => x.ChangeSet == change && x.PreviousRequest != null).Select(x => x.Id);
            var expected = asynchronous ? await expectedQuery.ToArrayAsync(token) : expectedQuery.ToArray();
            if (!expected.ToHashSet().SetEquals(audits.Where(x => x.ChangeSet == change).Select(x => x.SuccessorRequest)))
                throw new InvalidOperationException("Import supersession requires the complete accepted request set.");
        }
        foreach (var audit in audits)
        {
            if (!_authorizedSupersessions.Contains(audit) || Database.CurrentTransaction?.TransactionId != _supersessionTransaction)
                throw new InvalidOperationException("Import supersession has no owning acceptance scope.");
            var requestQuery = OperatorImportRequestRecords.AsNoTracking().Where(x => x.Id == audit.SuccessorRequest);
            var request = asynchronous ? await requestQuery.SingleOrDefaultAsync(token) : requestQuery.SingleOrDefault();
            var commitQuery = ChangeSetCommits.AsNoTracking().Where(x => x.ChangeSetId == audit.ChangeSet);
            var commit = asynchronous ? await commitQuery.SingleOrDefaultAsync(token) : commitQuery.SingleOrDefault();
            if (request is null || request.PreviousRequest != audit.PreviousRequest || request.StagedCredential != audit.SuccessorStage ||
                request.ChangeSet != audit.ChangeSet || request.CorrectionReason != audit.Reason || commit is null ||
                commit.DeploymentId != audit.Deployment || commit.Generation != audit.Generation || commit.ActorId != audit.Actor ||
                commit.CommittedAt != audit.AcceptedAt || !HoldsImportGate(request.Environment, request.Application))
                throw new InvalidOperationException("Import supersession audit does not match the accepted request and commit.");
            var predecessorQuery = OperatorImportRequestRecords.AsNoTracking().Where(x => x.Id == audit.PreviousRequest);
            var predecessor = asynchronous ? await predecessorQuery.SingleOrDefaultAsync(token) : predecessorQuery.SingleOrDefault();
            if (predecessor is null || predecessor.Environment != request.Environment || predecessor.Application != request.Application ||
                predecessor.CredentialResource != request.CredentialResource)
                throw new InvalidOperationException("Import supersession owner differs.");
            var ownerQuery = ProtectedCredentialOwners.AsNoTracking().Where(x => x.Environment == request.Environment && x.Application == request.Application &&
                x.Resource == request.CredentialResource && x.Stage == audit.SuccessorStage && x.Request == request.Id);
            var addedOwner = ChangeTracker.Entries<ProtectedCredentialOwnerRow>().Any(x => x.State == EntityState.Added && x.Entity.Environment == request.Environment &&
                x.Entity.Application == request.Application && x.Entity.Resource == request.CredentialResource && x.Entity.Stage == audit.SuccessorStage && x.Entity.Request == request.Id);
            if (!addedOwner && !(asynchronous ? await ownerQuery.AnyAsync(token) : ownerQuery.Any()))
                throw new InvalidOperationException("Import supersession has no exact credential owner transfer.");
        }
        foreach (var owner in ChangeTracker.Entries<ProtectedCredentialOwnerRow>().Where(x => x.State == EntityState.Added).Select(x => x.Entity))
        {
            if (!HoldsImportGate(owner.Environment, owner.Application)) throw new InvalidOperationException("Credential ownership requires its import gate.");
            if (audits.Any(x => x.SuccessorRequest == owner.Request && x.SuccessorStage == owner.Stage)) continue;
            var stage = ChangeTracker.Entries<ProtectedApplicationCredentialRow>().SingleOrDefault(x => x.State == EntityState.Added && x.Entity.Id == owner.Stage)?.Entity;
            var requestQuery = OperatorImportRequestRecords.AsNoTracking().Where(x => x.Id == owner.Request);
            var request = asynchronous ? await requestQuery.SingleOrDefaultAsync(token) : requestQuery.SingleOrDefault();
            if (stage is null || stage.Request != owner.Request || stage.Environment != owner.Environment || stage.Application != owner.Application ||
                stage.Resource != owner.Resource || request?.PreviousRequest is not null)
                throw new InvalidOperationException("Only a new ordinary private stage may reserve an unclaimed credential owner.");
        }
        foreach (var head in ChangeTracker.Entries<ResourceManagementHeadRow>().Where(x => x.State == EntityState.Added).Select(x => x.Entity))
        {
            if (audits.Length == 1 && HoldsImportGate(head.Environment, head.Application))
            {
                var historyQuery = ResourceManagement.AsNoTracking().Where(x => x.Id == head.Management && x.Environment == head.Environment &&
                    x.Application == head.Application && x.Resource == head.Resource && x.Deployment == audits[0].Deployment &&
                    x.SourceGeneration == audits[0].Generation && x.Origin == ResourceOrigin.Imported && x.Authority == ResourceLifecycleAuthority.UseAndRetain);
                if (asynchronous ? await historyQuery.AnyAsync(token) : historyQuery.Any()) continue;
            }
            var history = ChangeTracker.Entries<ResourceManagementRow>().SingleOrDefault(x => x.State == EntityState.Added && x.Entity.Id == head.Management)?.Entity;
            var accepted = history is null ? null : ChangeTracker.Entries<ChangeSetCommitRow>().SingleOrDefault(x => x.State == EntityState.Added &&
                x.Entity.DeploymentId == history.Deployment && x.Entity.Generation == history.SourceGeneration)?.Entity;
            if (history is null || accepted is null || history.Environment != head.Environment || history.Application != head.Application ||
                history.Resource != head.Resource || history.Origin == ResourceOrigin.Imported && !HoldsImportGate(head.Environment, head.Application))
                throw new InvalidOperationException("Current Resource management requires its exact newly accepted history.");
        }
    }
}
