using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

public sealed class PostgreSqlPlacementRevisionStore(IDbContextFactory<ManifestCatalogDbContext> contexts, IPlacementCodec codec) : IPlacementRevisionStore
{
    public async ValueTask<PlacementCreateResult> CompareAndCreateAsync(PlacementRevisionKey key, PlacementDeclaration declaration,
        ImportAuditMetadata audit, CancellationToken token)
    {
        var validated = codec.Read(declaration.Contract, declaration.FormatVersion, declaration.Payload);
        if (validated != declaration || key.Name != declaration.Name || key.Revision != declaration.Revision)
            throw new ArgumentException("Invalid Placement snapshot.", nameof(declaration));
        if (audit.ActorId.Length is 0 or > 256 || audit.ActorId.Any(char.IsControl)) throw new ArgumentException("Invalid actor.", nameof(audit));
        var existing = await FindAsync(key, token);
        if (existing is not null) return new(existing, false);
        var utc = audit.ImportedAt.ToUniversalTime();
        var row = new PlacementRevisionRow
        {
            Id = Guid.NewGuid(),
            EnvironmentRecordId = Guid.Parse(key.Environment.ToString()),
            ProviderAlias = key.Alias.Value,
            Name = key.Name.Value,
            Revision = key.Revision,
            Contract = declaration.Contract,
            FormatVersion = declaration.FormatVersion,
            Payload = declaration.Payload,
            ActorId = audit.ActorId,
            ImportedAt = utc.AddTicks(-(utc.Ticks % 10))
        };
        var accepted = Rehydrate(row);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            db.PlacementRevisions.Add(row);
            await db.SaveChangesAsync(token);
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            return new(accepted, true);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_placement_revision_exact" })
        {
            // The losing transaction is disposed before reading the winner in an independent context.
            return new(await FindAsync(key, token) ?? throw new InvalidDataException("Stored Placement revision is unavailable."), false);
        }
    }

    public async ValueTask<AcceptedPlacementRevision?> FindAsync(PlacementRevisionKey key, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await db.PlacementRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentRecordId == Guid.Parse(key.Environment.ToString()) &&
            x.ProviderAlias == key.Alias.Value && x.Name == key.Name.Value && x.Revision == key.Revision, token);
        return row is null ? null : Rehydrate(row);
    }

    private AcceptedPlacementRevision Rehydrate(PlacementRevisionRow row)
    {
        try
        {
            if (row.Id == Guid.Empty || row.ActorId.Length is 0 or > 256 || row.ActorId.Any(char.IsControl)) throw new InvalidDataException();
            var declaration = codec.Read(row.Contract, row.FormatVersion, row.Payload);
            if (declaration.Name.Value != row.Name || declaration.Revision != row.Revision) throw new InvalidDataException();
            return new(row.Id, new(EnvironmentRecordId.Parse(row.EnvironmentRecordId.ToString()), ProviderAlias.Create(row.ProviderAlias),
                ManifestLocalId.Create(row.Name), row.Revision), declaration, new(row.ActorId, row.ImportedAt));
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { throw new InvalidDataException("Stored Placement revision is unavailable."); }
    }
}
