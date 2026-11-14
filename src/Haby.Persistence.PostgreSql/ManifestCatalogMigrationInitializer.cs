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
        var actionBaseLength = Array.IndexOf(expected, "20261113200008_PersistInternalActionIntents") + 1;
        var identityBaseLength = Array.IndexOf(expected, "20261114033757_StableWorkloadRuntimeIdentity") + 1;
        // Inspect only tables known to exist in the validated old migration prefix, before any mutation.
        if (actionBaseLength > 0 && identityBaseLength > 0 && applied.Length >= actionBaseLength && applied.Length < identityBaseLength &&
            await context.ActionIntents.AsNoTracking().AnyAsync(x => x.SubjectKind == "Workload", cancellationToken))
            throw new InvalidOperationException("Superseded Workload identity requires an explicitly initialized fresh catalog; external resources remain unchanged.");
        var setBaseLength = Array.IndexOf(expected, "20261112002755_PersistExactProviderInputSets") + 1;
        var checkpointBaseLength = Array.IndexOf(expected, "20261112035955_PersistResourcesReadyCheckpoints") + 1;
        if (applied.Length > 0 && applied.Length < checkpointBaseLength &&
            (await context.DeploymentGenerations.AsNoTracking().AnyAsync(cancellationToken) ||
             await context.DurablePlans.AsNoTracking().AnyAsync(cancellationToken)))
            throw new InvalidOperationException("Superseded generation checkpoint history requires an explicitly initialized fresh catalog; external resources remain unchanged.");
        if (applied.Length > 0 && applied.Length < setBaseLength &&
            (await context.DurablePlans.AsNoTracking().AnyAsync(cancellationToken) ||
             await context.DeploymentGenerations.AsNoTracking().AnyAsync(cancellationToken)))
            throw new InvalidOperationException("Superseded singleton durable inputs require an explicitly initialized fresh catalog; external resources remain unchanged.");
        var frozenBaseLength = Array.IndexOf(expected, "20261107101422_PinConfigurationRendering") + 1;
        if (applied.Length >= currentBaseLength && applied.Length < frozenBaseLength &&
            (await context.DurablePlans.AsNoTracking().AnyAsync(cancellationToken) ||
                await context.DeploymentGenerations.AsNoTracking().AnyAsync(cancellationToken)))
            throw new InvalidOperationException("Older durable planning inputs require an explicitly initialized fresh catalog; external resources remain unchanged.");
        var namingBaseLength = Array.IndexOf(expected, "20261110122411_AddResourceNaming") + 1;
        if (applied.Length > 0 && applied.Length < namingBaseLength &&
            (await context.DurablePlans.AsNoTracking().AnyAsync(cancellationToken) ||
             await context.DeploymentGenerations.AsNoTracking().AnyAsync(cancellationToken)))
            throw new InvalidOperationException("Older naming inputs require an explicitly initialized fresh catalog; external resources remain unchanged.");
        if (applied.Length > 0 && await context.Revisions.AsNoTracking().AnyAsync(row =>
                row.NormalizedModelVersion != Haby.Domain.NormalizedApplicationManifest.CurrentModelVersion ||
                row.SnapshotFormatVersion != Haby.Application.NormalizedSnapshotCodec.FormatVersion, cancellationToken))
            throw new InvalidOperationException("Unsupported normalized manifest model. Explicitly initialize a fresh catalog; external resources remain unchanged.");
        await context.Database.MigrateAsync(cancellationToken);
        if (await context.DurablePlans.AsNoTracking().AnyAsync(x => x.SnapshotFormatVersion != DurableInputStorage.Version, cancellationToken) ||
            await context.DeploymentGenerations.AsNoTracking().AnyAsync(x => x.SnapshotFormatVersion != DurableInputStorage.Version, cancellationToken))
            throw new InvalidOperationException("Unsupported durable input format. Explicitly initialize a fresh catalog; external resources remain unchanged.");
    }
}
