using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Durable catalog schema, independent of manifest and domain objects.</summary>
public sealed class ManifestCatalogDbContext(DbContextOptions<ManifestCatalogDbContext> options) : DbContext(options)
{
    internal DbSet<ApplicationRow> Applications => Set<ApplicationRow>();
    internal DbSet<ManifestRevisionRow> Revisions => Set<ManifestRevisionRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationRow>(entity =>
        {
            entity.ToTable("applications");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.ApplicationId).HasColumnName("application_id").HasMaxLength(128).IsRequired();
            entity.HasIndex(row => row.ApplicationId).IsUnique();
        });
        modelBuilder.Entity<ManifestRevisionRow>(entity =>
        {
            entity.ToTable("manifest_revisions");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.ApplicationRecordId).HasColumnName("application_record_id");
            entity.Property(row => row.ApplicationId).HasColumnName("application_id").HasMaxLength(128).IsRequired();
            entity.Property(row => row.ApplicationVersion).HasColumnName("application_version").HasMaxLength(128).IsRequired();
            entity.Property(row => row.LocalRevisionNumber).HasColumnName("local_revision_number");
            entity.Property(row => row.InputApiVersion).HasColumnName("input_api_version").IsRequired();
            entity.Property(row => row.NormalizedModelVersion).HasColumnName("normalized_model_version");
            entity.Property(row => row.SnapshotFormatVersion).HasColumnName("snapshot_format_version");
            entity.Property(row => row.Snapshot).HasColumnName("snapshot").HasColumnType("text").IsRequired();
            entity.Property(row => row.HistoricalWarnings).HasColumnName("historical_warnings").HasColumnType("text").IsRequired();
            entity.Property(row => row.ActorId).HasColumnName("actor_id").HasMaxLength(256).IsRequired();
            entity.Property(row => row.ImportedAt).HasColumnName("imported_at").HasColumnType("timestamp with time zone");
            entity.HasOne<ApplicationRow>().WithMany().HasForeignKey(row => row.ApplicationRecordId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(row => new { row.ApplicationId, row.ApplicationVersion }).IsUnique();
            entity.HasIndex(row => new { row.ApplicationRecordId, row.LocalRevisionNumber }).IsUnique();
        });
    }
}

internal sealed class ApplicationRow
{
    public Guid Id { get; set; }
    public string ApplicationId { get; set; } = null!;
}

internal sealed class ManifestRevisionRow
{
    public Guid Id { get; set; }
    public Guid ApplicationRecordId { get; set; }
    public string ApplicationId { get; set; } = null!;
    public string ApplicationVersion { get; set; } = null!;
    public long LocalRevisionNumber { get; set; }
    public string InputApiVersion { get; set; } = null!;
    public int NormalizedModelVersion { get; set; }
    public int SnapshotFormatVersion { get; set; }
    public string Snapshot { get; set; } = null!;
    public string HistoricalWarnings { get; set; } = null!;
    public string ActorId { get; set; } = null!;
    public DateTimeOffset ImportedAt { get; set; }
}
