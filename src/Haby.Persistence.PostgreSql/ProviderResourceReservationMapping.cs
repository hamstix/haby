using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<ProviderResourceReservationRow> ProviderResourceReservations => Set<ProviderResourceReservationRow>();

    private static void ConfigureProviderResourceReservations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderResourceReservationRow>(e =>
        {
            e.ToTable("provider_resource_reservations");
            e.HasKey(r => new { r.ProviderInstanceId, r.EnvironmentRecordId, r.ApplicationRecordId, r.ResourceLocalId, r.Namespace });
            e.HasIndex(r => new { r.ProviderInstanceId, r.Namespace, r.Value }).IsUnique();
            e.Property(r => r.ProviderInstanceId).HasColumnName("provider_instance_id");
            e.Property(r => r.EnvironmentRecordId).HasColumnName("environment_record_id");
            e.Property(r => r.ApplicationRecordId).HasColumnName("application_record_id");
            e.Property(r => r.ResourceLocalId).HasColumnName("resource_local_id").HasMaxLength(63);
            e.Property(r => r.Namespace).HasColumnName("namespace").HasMaxLength(128).UseCollation("C");
            e.Property(r => r.Value).HasColumnName("value").HasMaxLength(512).UseCollation("C");
            e.HasOne<ProviderInstanceRow>().WithMany().HasForeignKey(r => new { r.ProviderInstanceId, r.EnvironmentRecordId })
                .HasPrincipalKey(r => new { r.Id, r.EnvironmentRecordId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationRow>().WithMany().HasForeignKey(r => r.ApplicationRecordId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class ProviderResourceReservationRow
{
    public Guid ProviderInstanceId { get; set; }
    public Guid EnvironmentRecordId { get; set; }
    public Guid ApplicationRecordId { get; set; }
    public required string ResourceLocalId { get; set; }
    public required string Namespace { get; set; }
    public required string Value { get; set; }
}
