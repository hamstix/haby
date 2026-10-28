using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed class PostgreSqlEnvironmentQuery(IDbContextFactory<ManifestCatalogDbContext> contexts) : IEnvironmentQuery
{
    public async ValueTask<EnvironmentRecordId?> FindAsync(EnvironmentId id, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await context.Environments.AsNoTracking().SingleOrDefaultAsync(
            item => item.EnvironmentId == id.Value, cancellationToken);
        return row is null ? null : EnvironmentRecordId.Parse(row.Id.ToString());
    }
}
