using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    private readonly HashSet<(Guid Transaction, Guid Environment, Guid Application)> _importGates = [];
    private ImportSupersessionRow? _authorizedSupersession;
    private Guid? _supersessionTransaction;

    internal void RecordImportGate(Guid transaction, Guid environment, Guid application) => _importGates.Add((transaction, environment, application));
    internal bool HoldsImportGate(Guid environment, Guid application) => Database.CurrentTransaction is { } transaction &&
        _importGates.Contains((transaction.TransactionId, environment, application));

    internal void AuthorizeImportSupersession(ImportSupersessionRow row, Guid environment, Guid application)
    {
        if (_authorizedSupersession is not null || !HoldsImportGate(environment, application))
            throw new InvalidOperationException("Import supersession needs its owning gated transaction.");
        _authorizedSupersession = row;
        _supersessionTransaction = Database.CurrentTransaction!.TransactionId;
    }

    private async Task ValidateImportAuthorityWritesAsync(bool asynchronous, CancellationToken token)
    {
        if (ChangeTracker.Entries<ImportSupersessionRow>().Any(x => x.State is EntityState.Modified or EntityState.Deleted) ||
            ChangeTracker.Entries<ProtectedCredentialOwnerRow>().Any(x => x.State is EntityState.Modified or EntityState.Deleted) ||
            ChangeTracker.Entries<ResourceManagementHeadRow>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Import history is immutable and current authority requires guarded compare-and-swap.");
        var audits = ChangeTracker.Entries<ImportSupersessionRow>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToArray();
        foreach (var audit in audits)
        {
            if (!ReferenceEquals(audit, _authorizedSupersession) || Database.CurrentTransaction?.TransactionId != _supersessionTransaction)
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
