using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Apply reviewed catalog migrations before a single-instance host accepts requests.</summary>
public sealed class ManifestCatalogMigrationInitializer(IDbContextFactory<ManifestCatalogDbContext> contexts)
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
    }
}
