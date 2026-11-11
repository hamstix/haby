using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

public sealed class PostgreSqlPlacementSelectionStore(IDbContextFactory<ManifestCatalogDbContext> contexts, IPlacementCodec codec) : IPlacementSelectionStore
{
    public async ValueTask<PersistedPlacementSelection?> FindAsync(EnvironmentRecordId environment, ProviderAlias alias, PlacementSelectionTarget target, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await Query(db, environment, alias, target).AsNoTracking().SingleOrDefaultAsync(token);
        if (row is null) return null;
        row.Revision = await db.PlacementRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.RevisionId, token) ?? throw new InvalidDataException("Stored Placement selection is unavailable.");
        return await RehydrateAsync(db, row, token);
    }
    public async ValueTask<PlacementSelectionResult> ApplyAsync(PlacementSelectionTarget target, PlacementRevisionKey reference,
        PlacementSelectionPrecondition precondition, PlacementSelectionAudit audit, CancellationToken token)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var row = await Query(db, reference.Environment, reference.Alias, target).SingleOrDefaultAsync(token);
            if (row is not null) row.Revision = await db.PlacementRevisions.SingleOrDefaultAsync(x => x.Id == row.RevisionId, token)
                ?? throw new InvalidDataException("Stored Placement selection is unavailable.");
            var current = row is null ? null : await RehydrateAsync(db, row, token);
            if (precondition switch
            {
                PlacementSelectionPrecondition.ExpectedAbsence => current is not null,
                PlacementSelectionPrecondition.ExpectedToken match => current is null || current.Token != match.Token,
                _ => true
            }) return new(PlacementSelectionOutcome.Conflict);
            var unchanged = current?.Reference == reference;
            if (unchanged)
            {
                // A conditional UPDATE of the existing token linearizes equal contenders too.
                // No new locks or workflow SQL are needed; a stale snapshot cannot report success.
                db.Entry(row!).Property(x => x.Token).IsModified = true;
            }
            else
            {
                var env = Guid.Parse(reference.Environment.ToString());
                var revision = await db.PlacementRevisions.SingleOrDefaultAsync(x => x.EnvironmentRecordId == env &&
                    x.ProviderAlias == reference.Alias.Value && x.Name == reference.Name.Value && x.Revision == reference.Revision, token);
                if (revision is null) return new(PlacementSelectionOutcome.RevisionNotFound);
                _ = new PostgreSqlPlacementRevisionStore(contexts, codec).Rehydrate(revision);
                if (row is null)
                {
                    row = new()
                    {
                        EnvironmentRecordId = env,
                        ProviderAlias = reference.Alias.Value,
                        TargetKind = target.Kind,
                        Selector = target.Selector,
                        ApplicationId = target.Application,
                        ComponentRef = target.Component,
                        ActorId = ""
                    };
                    db.PlacementSelections.Add(row);
                }
                row.RevisionId = revision.Id; row.Revision = revision;
                row.Token = PlacementSelectionToken.New().Value;
                row.ActorId = audit.ActorId;
                var utc = audit.ChangedAtUtc.ToUniversalTime(); row.ChangedAtUtc = utc.AddTicks(-(utc.Ticks % 10));
                row.CheckedApplicationRecordId = audit.CheckedRevision is { } r ? Guid.Parse(r.ApplicationRecordId.ToString()) : null;
                row.CheckedRevisionNumber = audit.CheckedRevision?.RevisionNumber.Value;
            }
            var accepted = await RehydrateAsync(db, row!, token);
            await db.SaveChangesAsync(token);
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            return new(current is null ? PlacementSelectionOutcome.Created : unchanged ? PlacementSelectionOutcome.Unchanged : PlacementSelectionOutcome.Updated, accepted);
        }
        catch (DbUpdateConcurrencyException) { return new(PlacementSelectionOutcome.Conflict); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_placement_selections" })
        { return new(PlacementSelectionOutcome.Conflict); }
    }
    private static IQueryable<PlacementSelectionRow> Query(ManifestCatalogDbContext db, EnvironmentRecordId environment, ProviderAlias alias, PlacementSelectionTarget target)
    {
        var env = Guid.Parse(environment.ToString());
        return db.PlacementSelections.Where(x => x.EnvironmentRecordId == env && x.ProviderAlias == alias.Value && x.TargetKind == target.Kind &&
            x.Selector == target.Selector && x.ApplicationId == target.Application && x.ComponentRef == target.Component);
    }
    private async ValueTask<PersistedPlacementSelection> RehydrateAsync(ManifestCatalogDbContext db, PlacementSelectionRow row, CancellationToken token)
    {
        try
        {
            var target = PlacementSelectionTarget.Create(row.TargetKind, row.Selector, row.ApplicationId, row.ComponentRef);
            if (row.ActorId.Length is 0 or > 256 || row.ActorId.Any(char.IsControl) || row.ChangedAtUtc.Offset != TimeSpan.Zero ||
                row.Revision is null || row.RevisionId != row.Revision.Id || row.EnvironmentRecordId != row.Revision.EnvironmentRecordId || row.ProviderAlias != row.Revision.ProviderAlias ||
                (target.Kind == "component" ? row.CheckedApplicationRecordId is null || row.CheckedApplicationRecordId == Guid.Empty || row.CheckedRevisionNumber is null or <= 0 :
                    row.CheckedApplicationRecordId is not null || row.CheckedRevisionNumber is not null)) throw new InvalidDataException();
            var revision = new PostgreSqlPlacementRevisionStore(contexts, codec).Rehydrate(row.Revision);
            ManifestRevisionRef? check = row.CheckedApplicationRecordId is { } app ? ManifestRevisionRef.Create(ApplicationRecordId.Parse(app.ToString()),
                ManifestRevisionNumber.Create(row.CheckedRevisionNumber!.Value)) : null;
            if (check is not null)
            {
                // Apply owns an active transaction: validate on its connection rather than
                // renting a second one and waiting on our own pool slot.
                var checkedApplication = Guid.Parse(check.ApplicationRecordId.ToString());
                var checkedNumber = check.RevisionNumber.Value;
                var stored = await db.Revisions.AsNoTracking().SingleOrDefaultAsync(x => x.ApplicationRecordId == checkedApplication &&
                    x.LocalRevisionNumber == checkedNumber, token);
                var manifest = stored is null ? null : PostgreSqlManifestImportStore.Rehydrate(stored);
                if (manifest is null || manifest.ApplicationId.Value != target.Application ||
                    !manifest.Declaration.Spec.Components.ContainsKey(ManifestLocalId.Create(target.Component))) throw new InvalidDataException();
            }
            return new(target, revision.Key, PlacementSelectionToken.Parse(row.Token.ToString()), new(row.ActorId, row.ChangedAtUtc, check));
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or NullReferenceException)
        { throw new InvalidDataException("Stored Placement selection is unavailable."); }
    }
}
