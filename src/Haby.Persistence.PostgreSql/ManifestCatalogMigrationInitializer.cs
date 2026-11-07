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
        // The current pre-Profile schema admits this additive migration. Earlier alpha schemas
        // still require explicit reinitialization; never infer an upgrade across a reset boundary.
        var currentBaseLength = Array.IndexOf(expected, "20261103084056_AddProviderResourceReservations") + 1;
        if (applied.Length > 0 && (currentBaseLength == 0 || applied.Length < currentBaseLength ||
            applied.Length > expected.Length || !applied.SequenceEqual(expected.Take(applied.Length))))
            throw new InvalidOperationException("Unsupported catalog schema. Explicitly initialize a fresh catalog; external resources remain unchanged.");
        var frozenBaseLength = Array.IndexOf(expected, "20261107101422_PinConfigurationRendering") + 1;
        if (applied.Length >= currentBaseLength && applied.Length < frozenBaseLength &&
            (await context.DurablePlans.AsNoTracking().AnyAsync(cancellationToken) ||
                await context.DeploymentGenerations.AsNoTracking().AnyAsync(cancellationToken)))
            throw new InvalidOperationException("Older durable planning inputs require an explicitly initialized fresh catalog; external resources remain unchanged.");
        await context.Database.MigrateAsync(cancellationToken);
    }
}
