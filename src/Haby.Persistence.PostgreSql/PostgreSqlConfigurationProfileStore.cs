using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>Immutable, installation-scoped acceptance guarded by PostgreSQL composite uniqueness.</summary>
public sealed class PostgreSqlConfigurationProfileStore(IDbContextFactory<ManifestCatalogDbContext> contexts)
    : IConfigurationProfileImportStore, IConfigurationProfileRevisionQuery
{
    public async ValueTask<ProfileCompareAndCreateResult> CompareAndCreateAsync(ConfigurationProfileRevision candidate,
        ImportAuditMetadata auditMetadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(auditMetadata);
        ArgumentNullException.ThrowIfNull(auditMetadata.ActorId);
        if (auditMetadata.ActorId.Length is 0 or > 256 || auditMetadata.ActorId.Any(char.IsControl))
            throw new ArgumentException("Invalid import actor.", nameof(auditMetadata));
        cancellationToken.ThrowIfCancellationRequested();
        var existing = await FindAsync(candidate.Reference, cancellationToken);
        if (existing is not null) return new(existing, false);
        var utc = auditMetadata.ImportedAt.ToUniversalTime();
        var row = new ProfileRevisionRow
        {
            ProfileId = candidate.Reference.ProfileId.Value,
            Revision = candidate.Reference.Revision,
            ModelVersion = ProfileSnapshotCodec.ModelVersion,
            SnapshotFormatVersion = ProfileSnapshotCodec.FormatVersion,
            Snapshot = ProfileSnapshotCodec.Encode(candidate),
            ActorId = auditMetadata.ActorId,
            // PostgreSQL timestamps have microsecond precision. Return the exact durable audit on first import too.
            ImportedAt = utc.AddTicks(-(utc.Ticks % 10))
        };
        var accepted = Rehydrate(row);
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            context.ProfileRevisions.Add(row);
            await context.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Once commit starts, wait for its confirmed outcome, including if the caller cancels.
            await transaction.CommitAsync(CancellationToken.None);
            return new(accepted, true);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_configuration_profile_revisions" })
        {
            // The losing transaction has been disposed/rolled back. Read the winner with an independent context.
            var winner = await FindAsync(candidate.Reference, cancellationToken)
                ?? throw new InvalidDataException("Accepted Profile revision is unavailable.");
            return new(winner, false);
        }
    }

    public async ValueTask<AcceptedConfigurationProfile?> FindAsync(ProfileRevisionRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await context.ProfileRevisions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ProfileId == reference.ProfileId.Value && x.Revision == reference.Revision, cancellationToken);
        return row is null ? null : Rehydrate(row);
    }

    private static AcceptedConfigurationProfile Rehydrate(ProfileRevisionRow row)
    {
        if (row.ActorId is null || row.ActorId.Length is 0 or > 256 || row.ActorId.Any(char.IsControl))
            throw new InvalidDataException("Stored Profile audit is invalid.");
        return new(ProfileSnapshotCodec.Decode(row.Snapshot, row.ProfileId, row.Revision,
            row.SnapshotFormatVersion, row.ModelVersion), new(row.ActorId, row.ImportedAt));
    }
}
