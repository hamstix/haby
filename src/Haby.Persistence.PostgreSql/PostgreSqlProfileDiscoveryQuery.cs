using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed class PostgreSqlProfileDiscoveryQuery(IDbContextFactory<ManifestCatalogDbContext> contexts) : IProfileDiscoveryQuery
{
    public async ValueTask<ProfileDiscoveryPage> ListAsync(int pageSize = 50, ProfileId? afterProfileId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSize(pageSize);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        // Aggregate before paging identities, then join the exact winning row for its own audit.
        var maxima = context.ProfileRevisions.GroupBy(x => x.ProfileId)
            .Select(g => new { ProfileId = g.Key, Revision = g.Max(x => x.Revision) });
        var query = from row in context.ProfileRevisions.AsNoTracking()
                    join maximum in maxima on new { row.ProfileId, row.Revision } equals new { maximum.ProfileId, maximum.Revision }
                    select row;
        if (afterProfileId is not null)
            query = query.Where(x => string.Compare(EF.Functions.Collate(x.ProfileId, "C"), afterProfileId.Value) > 0);
        var rows = await query.OrderBy(x => EF.Functions.Collate(x.ProfileId, "C")).Take(pageSize)
            .Select(x => new MetadataRow(x.ProfileId, x.Revision, x.ActorId, x.ImportedAt)).ToArrayAsync(cancellationToken);
        var items = rows.Select(Read).ToArray();
        return new(items, items.Length == pageSize ? items[^1].Reference.ProfileId : null);
    }

    public async ValueTask<ProfileRevisionDiscoveryPage> ListRevisionsAsync(ProfileId profileId, int pageSize = 50,
        int? afterRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        ValidateSize(pageSize);
        if (afterRevision is <= 0) throw new ArgumentOutOfRangeException(nameof(afterRevision));
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var query = context.ProfileRevisions.AsNoTracking().Where(x => x.ProfileId == profileId.Value);
        if (afterRevision is not null) query = query.Where(x => x.Revision > afterRevision.Value);
        var rows = await query.OrderBy(x => x.Revision).Take(pageSize)
            .Select(x => new MetadataRow(x.ProfileId, x.Revision, x.ActorId, x.ImportedAt)).ToArrayAsync(cancellationToken);
        var items = rows.Select(Read).ToArray();
        return new(items, items.Length == pageSize ? items[^1].Reference.Revision : null);
    }

    private static void ValidateSize(int size)
    {
        if (size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(size));
    }

    private sealed record MetadataRow(string ProfileId, int Revision, string ActorId, DateTimeOffset ImportedAt);

    private static ImportedProfileMetadata Read(MetadataRow row)
    {
        if (string.IsNullOrWhiteSpace(row.ActorId) || row.ActorId.Length > 256 || row.ActorId.Any(char.IsControl) ||
            row.ImportedAt == DateTimeOffset.MinValue || row.ImportedAt == DateTimeOffset.MaxValue || row.ImportedAt.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Stored Profile metadata is invalid.");
        try { return new(new(ProfileId.Create(row.ProfileId), row.Revision), new(row.ActorId, row.ImportedAt)); }
        catch (ArgumentException) { throw new InvalidDataException("Stored Profile metadata is invalid."); }
    }
}
