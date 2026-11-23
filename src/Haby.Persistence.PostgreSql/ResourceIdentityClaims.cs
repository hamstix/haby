using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed class ResourceIdentityClaimRow
{
    public Guid Provider { get; set; }
    public string Namespace { get; set; } = "";
    public string Value { get; set; } = "";
    public Guid Environment { get; set; }
    public Guid Application { get; set; }
    public string Resource { get; set; } = "";
    public Guid Association { get; set; }
    public string? SharingJson { get; set; }
}

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<ResourceIdentityClaimRow> ResourceIdentityClaims => Set<ResourceIdentityClaimRow>();
    private static void ConfigureResourceIdentityClaims(ModelBuilder model)
    {
        model.Entity<ResourceIdentityClaimRow>(e =>
        {
            e.ToTable("resource_identity_claims");
            e.HasKey(x => new { x.Provider, x.Namespace, x.Value, x.Association });
            e.Property(x => x.Provider).HasColumnName("provider_instance_id");
            e.Property(x => x.Namespace).HasColumnName("namespace").HasMaxLength(128);
            e.Property(x => x.Value).HasColumnName("value").HasMaxLength(512);
            e.Property(x => x.Environment).HasColumnName("environment_record_id");
            e.Property(x => x.Application).HasColumnName("application_record_id");
            e.Property(x => x.Resource).HasColumnName("resource_local_id").HasMaxLength(63);
            e.Property(x => x.Association).HasColumnName("association_id");
            e.Property(x => x.SharingJson).HasColumnName("sharing_json");
            e.HasIndex(x => new { x.Provider, x.Environment, x.Application, x.Resource, x.Namespace }).IsUnique();
            e.HasOne<ProviderInstanceRow>().WithMany().HasForeignKey(x => x.Provider).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ResourceManagementRow>().WithMany().HasForeignKey(x => x.Association).OnDelete(DeleteBehavior.Restrict);
        });
    }
    private bool HasIdentityWrites() => ChangeTracker.Entries<ProviderResourceReservationRow>().Any(x => x.State == EntityState.Added) ||
        ChangeTracker.Entries<ResourceIdentityClaimRow>().Any(x => x.State == EntityState.Added);

    // Every EF reservation/claim writer shares the persisted Provider row, even with no identity rows.
    // No external calls, triggers or check-then-insert window outside the owning transaction.
    private async Task ValidateIdentityWritesAsync(bool asynchronous, CancellationToken token)
    {
        if (ChangeTracker.Entries<ResourceIdentityClaimRow>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Identity claims are immutable in this slice.");
        var claims = ChangeTracker.Entries<ResourceIdentityClaimRow>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToArray();
        var reservations = ChangeTracker.Entries<ProviderResourceReservationRow>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToArray();
        foreach (var provider in claims.Select(x => x.Provider).Concat(reservations.Select(x => x.ProviderInstanceId)).Distinct().Order())
        {
            if (Database.CurrentTransaction is null) throw new InvalidOperationException("Identity writes require a transaction.");
            if (asynchronous) await Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM provider_instances WHERE id = {provider} FOR UPDATE", token);
            else Database.ExecuteSqlInterpolated($"SELECT id FROM provider_instances WHERE id = {provider} FOR UPDATE");
        }
        foreach (var claim in claims)
        {
            if (claim.Namespace is not { Length: > 0 and <= 128 } || claim.Value is not { Length: > 0 and <= 512 } || claim.Namespace.Contains('\0') || claim.Value.Contains('\0'))
                throw new InvalidOperationException("Invalid identity bounds.");
            var associationQuery = ResourceManagement.AsNoTracking().Where(x => x.Id == claim.Association && x.Environment == claim.Environment &&
                x.Application == claim.Application && x.Resource == claim.Resource && x.Origin == ResourceOrigin.Referenced && x.Authority == ResourceLifecycleAuthority.ConsumptionOnly);
            var providerQuery = ProviderInstances.AsNoTracking().Where(x => x.Id == claim.Provider && x.EnvironmentRecordId == claim.Environment);
            var association = await SharingSingleAsync(associationQuery, asynchronous, token);
            if (association is null || !(asynchronous ? await providerQuery.AnyAsync(token) : providerQuery.Any()))
                throw new InvalidOperationException("Identity has no exact Reference authority.");
            var source = await SharingSingleAsync(DeploymentGenerations.AsNoTracking().Where(x => x.DeploymentId == association.Deployment &&
                x.Generation == association.SourceGeneration && x.EnvironmentRecordId == claim.Environment && x.ApplicationRecordId == claim.Application), asynchronous, token);
            var original = source?.Inputs();
            var resource = Haby.Domain.ManifestLocalId.Create(claim.Resource);
            if (original is null || original.Acquisitions.Resource(resource).Mode != ResourceAcquisitionMode.Reference ||
                original.Providers.Resource(resource).Instance.ToString() != claim.Provider.ToString())
                throw new InvalidOperationException("Identity differs from its own pinned Provider association.");
            var conflicts = ProviderResourceReservations.AsNoTracking().Where(x => x.ProviderInstanceId == claim.Provider && x.Namespace == claim.Namespace &&
                (x.Value == claim.Value || x.EnvironmentRecordId == claim.Environment && x.ApplicationRecordId == claim.Application && x.ResourceLocalId == claim.Resource));
            var duplicates = ResourceIdentityClaims.AsNoTracking().Where(x => x.Provider == claim.Provider && x.Namespace == claim.Namespace &&
                (x.Value == claim.Value || x.Environment == claim.Environment && x.Application == claim.Application && x.Resource == claim.Resource));
            if ((asynchronous ? await conflicts.AnyAsync(token) : conflicts.Any()) ||
                reservations.Any(x => x.ProviderInstanceId == claim.Provider && x.Namespace == claim.Namespace && (x.Value == claim.Value || x.EnvironmentRecordId == claim.Environment && x.ApplicationRecordId == claim.Application && x.ResourceLocalId == claim.Resource)))
                throw new ResourceIdentityConflictException();
            var own = claim.SharingJson is null ? null : await ReadSharingParticipantAsync(claim, true, asynchronous, token);
            if (claim.SharingJson is not null && own is null) throw new ResourceIdentityConflictException();
            var stored = asynchronous ? await duplicates.ToArrayAsync(token) : duplicates.ToArray();
            foreach (var peer in stored.Concat(claims.Where(x => !ReferenceEquals(x, claim) && x.Provider == claim.Provider && x.Namespace == claim.Namespace &&
                (x.Value == claim.Value || x.Environment == claim.Environment && x.Application == claim.Application && x.Resource == claim.Resource))))
            {
                if (peer.Environment == claim.Environment && peer.Application == claim.Application && peer.Resource == claim.Resource ||
                    own is null || peer.SharingJson is null || peer.Environment != claim.Environment)
                    throw new ResourceIdentityConflictException();
                var other = await ReadSharingParticipantAsync(peer, claims.Contains(peer), asynchronous, token);
                if (other is null || !CompatibleSharing(own, other)) throw new ResourceIdentityConflictException();
            }
        }
        foreach (var reservation in reservations)
        {
            var conflicts = ResourceIdentityClaims.AsNoTracking().Where(x => x.Provider == reservation.ProviderInstanceId && x.Namespace == reservation.Namespace &&
                (x.Value == reservation.Value || x.Environment == reservation.EnvironmentRecordId && x.Application == reservation.ApplicationRecordId && x.Resource == reservation.ResourceLocalId));
            if (asynchronous ? await conflicts.AnyAsync(token) : conflicts.Any()) throw new ResourceIdentityConflictException();
        }
    }
}

internal sealed class ResourceIdentityConflictException() : InvalidOperationException("Identity association conflicts.");
