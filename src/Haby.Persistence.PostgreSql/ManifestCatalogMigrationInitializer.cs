using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Apply reviewed catalog migrations before a single-instance host accepts requests.</summary>
public sealed class ManifestCatalogMigrationInitializer(IDbContextFactory<ManifestCatalogDbContext> contexts)
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        var expected = context.Database.GetMigrations().ToArray();
        if (applied.Length > 0 && !applied.SequenceEqual(expected))
            throw new InvalidOperationException("Unsupported catalog schema. Explicitly initialize a fresh catalog; external resources remain unchanged.");
        await context.Database.MigrateAsync(cancellationToken);
    }
}
