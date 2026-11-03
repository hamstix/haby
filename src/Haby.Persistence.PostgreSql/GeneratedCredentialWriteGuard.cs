using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        // The synchronous branch performs synchronous queries and never waits on asynchronous I/O.
        ValidateGeneratedWritesAsync(false, default).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await ValidateGeneratedWritesAsync(true, cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private async Task ValidateGeneratedWritesAsync(bool asynchronous, CancellationToken cancellationToken)
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries<ProviderResourceReservationRow>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Provider resource reservations are immutable.");
        if (ChangeTracker.Entries<OperationOutputRow>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Operation output history is immutable.");
        if (ChangeTracker.Entries<GeneratedCredentialSlotRow>().Any(e => e.State is EntityState.Modified or EntityState.Deleted) ||
            ChangeTracker.Entries<GeneratedCredentialCreationAuditRow>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Generated credential history is immutable.");

        foreach (var entry in ChangeTracker.Entries<SecretRow>().Where(e => e.State is EntityState.Modified or EntityState.Deleted))
        {
            var query = Secrets.AsNoTracking().Where(s => s.Id == entry.Entity.Id);
            // Read the persisted classification: detached writers cannot bypass this by changing Owner/OriginalValues.
            var saved = asynchronous ? await query.SingleOrDefaultAsync(cancellationToken) : query.SingleOrDefault();
            if (saved?.Owner != "Resource" && entry.Entity.Owner != "Resource") continue;
            if (saved is null || entry.State == EntityState.Deleted || saved.CurrentVersionId is not null ||
                entry.Entity.CurrentVersionId is null || entry.Properties.Any(p => p.IsModified && p.Metadata.Name != nameof(SecretRow.CurrentVersionId)))
                throw new InvalidOperationException("Generated credential metadata is immutable.");
            // Only the initial null -> first-version pin is allowed, inside the candidate creation transaction.
            var versionQuery = SecretVersions.AsNoTracking().Where(v => v.SecretId == saved.Id &&
                v.Id == entry.Entity.CurrentVersionId && v.Number == 1 &&
                v.ProtectionFormat == PlaintextProtectionFormat.Name && v.FormatVersion == PlaintextProtectionFormat.Version);
            var valid = asynchronous ? await versionQuery.AnyAsync(cancellationToken) : versionQuery.Any();
            if (!valid) throw new InvalidOperationException("Invalid generated credential association.");
        }

        var addedSlots = ChangeTracker.Entries<GeneratedCredentialSlotRow>().Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity).ToArray();
        foreach (var slot in addedSlots)
        {
            var query = Secrets.AsNoTracking().Where(s => s.Id == slot.SecretId);
            var secret = asynchronous ? await query.SingleOrDefaultAsync(cancellationToken) : query.SingleOrDefault();
            if (secret is null || secret.Owner != "Resource" || secret.Purpose != "ApplicationLogin" ||
                secret.EnvironmentRecordId != slot.EnvironmentRecordId || secret.ApplicationRecordId != slot.ApplicationRecordId ||
                secret.ResourceLocalId != slot.ResourceLocalId || secret.CurrentVersionId != slot.VersionId ||
                secret.Sensitivity != "Credential" || secret.ContentType != "application/octet-stream" ||
                secret.OwnershipPolicy != "Managed" || secret.StorageBackend != "PostgreSqlPlaintext" || secret.Name is not null ||
                slot.SlotId != Haby.Domain.GeneratedCredentialSlot.ApplicationPassword ||
                slot.GenerationProfile != Haby.Domain.GeneratedCredentialSlot.RandomPasswordV1)
                throw new InvalidOperationException("Invalid generated credential association.");
            var versions = SecretVersions.AsNoTracking().Where(v => v.Id == slot.VersionId && v.SecretId == slot.SecretId &&
                v.Number == 1 && v.CreatedAt == secret.CreatedAt && v.ProtectionFormat == PlaintextProtectionFormat.Name &&
                v.FormatVersion == PlaintextProtectionFormat.Version);
            var valid = asynchronous ? await versions.AnyAsync(cancellationToken) : versions.Any();
            var audits = ChangeTracker.Entries<GeneratedCredentialCreationAuditRow>().Where(e => e.State == EntityState.Added &&
                e.Entity.SecretId == slot.SecretId).Select(e => e.Entity).ToArray();
            if (!valid || secret.CreatedAt is null || audits.Length != 1 || audits[0].CreatedAt != secret.CreatedAt ||
                audits[0].TechnicalActor != PostgreSqlGeneratedCredentialSlots.TechnicalActor)
                throw new InvalidOperationException("Invalid generated credential association.");
        }
        if (ChangeTracker.Entries<GeneratedCredentialCreationAuditRow>().Any(e => e.State == EntityState.Added &&
            !addedSlots.Any(s => s.SecretId == e.Entity.SecretId)))
            throw new InvalidOperationException("Invalid generated credential association.");
    }
}
