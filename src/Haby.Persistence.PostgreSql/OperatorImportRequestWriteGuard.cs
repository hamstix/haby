using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    private async Task ValidateOperatorImportRequestWritesAsync(bool asynchronous, CancellationToken token)
    {
        foreach (var entry in ChangeTracker.Entries<OperatorImportRequestRow>().Where(x =>
            x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var row = entry.Entity;
            var binding = PostgreSqlDurableDeploymentPlanner.StoredImportBinding(row);
            binding.Validate();
            PostgreSqlDurableDeploymentPlanner.ValidateActor(row.CreatedBy);
            if (entry.State == EntityState.Added)
            {
                if (row.ChangeSet is not null || row.StagedCredential is not null || row.CompletedAt is not null || row.CompletedBy is not null)
                    throw new InvalidOperationException("An import request must be bound before credential staging.");
                continue;
            }
            var query = OperatorImportRequestRecords.AsNoTracking().Where(x => x.Id == row.Id);
            var saved = asynchronous ? await query.SingleOrDefaultAsync(token) : query.SingleOrDefault();
            if (entry.State == EntityState.Deleted || saved is null || saved.ChangeSet is not null ||
                !PostgreSqlDurableDeploymentPlanner.SameImportRequest(saved, binding) ||
                row.ManifestRevision != saved.ManifestRevision || row.CreatedAt != saved.CreatedAt || row.CreatedBy != saved.CreatedBy ||
                row.ChangeSet is null || row.StagedCredential is null || row.CompletedAt is null || row.CompletedBy is null ||
                Database.CurrentTransaction is null)
                throw new InvalidOperationException("Import request binding and completion history are immutable.");
            PostgreSqlDurableDeploymentPlanner.ValidateActor(row.CompletedBy);
            var change = ChangeTracker.Entries<DurableChangeSetRow>().SingleOrDefault(x =>
                x.State == EntityState.Added && x.Entity.Id == row.ChangeSet)?.Entity;
            var stageQuery = ProtectedApplicationCredentials.AsNoTracking().Where(x => x.Id == row.StagedCredential)
                .Select(x => new PostgreSqlDurableDeploymentPlanner.RequestCredential(x.Id, x.Request, x.Environment,
                    x.Application, x.Resource, x.Secret, x.Version, x.ConfirmedReceipt));
            var staged = asynchronous ? await stageQuery.SingleOrDefaultAsync(token) : stageQuery.SingleOrDefault();
            if (change is null || !PostgreSqlDurableDeploymentPlanner.MatchesRequestCredential(staged, row) || staged!.ConfirmedReceipt is not null ||
                change.EnvironmentRecordId != row.Environment || change.ApplicationRecordId != row.Application || change.RevisionNumber != row.Revision ||
                !change.ExpectedAbsence || change.ExpectedGeneration is not null || change.CreatedAt != row.CompletedAt || change.ActorId != row.CompletedBy ||
                change.ImportIntentJson != ResourceImportIntentCodec.Encode(binding.Attach(new(staged.Id,
                    new(Haby.Domain.SecretId.Parse(staged.Secret.ToString()), Haby.Domain.SecretVersionId.Parse(staged.Version.ToString()))))))
                throw new InvalidOperationException("Import request linkage requires its exact new ChangeSet and private credential.");
        }
    }
}
