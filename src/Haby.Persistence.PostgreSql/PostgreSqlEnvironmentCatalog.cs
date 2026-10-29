using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed class PostgreSqlEnvironmentCatalog(IDbContextFactory<ManifestCatalogDbContext> contexts) : IEnvironmentCatalog
{
    public async ValueTask<EnvironmentCreation> CreateOrGetAsync(EnvironmentId name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var id = Guid.Parse(EnvironmentRecordId.New().ToString());
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        // The unique index arbitrates races. A separate READ COMMITTED statement sees the winner
        // after ON CONFLICT waits for its commit, including when this insert did nothing.
        var inserted = await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO environments (id, environment_id) VALUES ({id}, {name.Value}) ON CONFLICT (environment_id) DO NOTHING",
            cancellationToken);
        var row = await context.Environments.AsNoTracking().SingleAsync(x => x.EnvironmentId == name.Value, cancellationToken);
        return new(Metadata(row), inserted == 1);
    }

    public async ValueTask<EnvironmentMetadata?> GetAsync(EnvironmentId name, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await context.Environments.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentId == name.Value, cancellationToken);
        return row is null ? null : Metadata(row);
    }

    public async ValueTask<IReadOnlyList<EnvironmentMetadata>> ListAsync(EnvironmentId? after, int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > EnvironmentAdministration.MaxPageSize + 1) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var query = context.Environments.AsNoTracking();
        if (after is not null)
            query = query.Where(x => string.Compare(EF.Functions.Collate(x.EnvironmentId, "C"), after.Value) > 0);
        var rows = await query.OrderBy(x => EF.Functions.Collate(x.EnvironmentId, "C")).Take(limit).ToArrayAsync(cancellationToken);
        return rows.Select(Metadata).ToArray();
    }

    private static EnvironmentMetadata Metadata(EnvironmentRow row) =>
        new(EnvironmentRecordId.Parse(row.Id.ToString()), EnvironmentId.Create(row.EnvironmentId));
}
