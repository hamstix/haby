using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<ProfileRevisionRow> ProfileRevisions => Set<ProfileRevisionRow>();

    private static void ConfigureProfileRevisions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProfileRevisionRow>(entity =>
        {
            entity.ToTable("configuration_profile_revisions", table =>
            {
                table.HasCheckConstraint("CK_profile_revisions_revision", "revision > 0");
                table.HasCheckConstraint("CK_profile_revisions_id", "profile_id ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'");
                table.HasCheckConstraint("CK_profile_revisions_actor", "length(actor_id) > 0");
            });
            entity.HasKey(row => new { row.ProfileId, row.Revision });
            entity.Property(row => row.ProfileId).HasColumnName("profile_id").HasMaxLength(128);
            entity.Property(row => row.Revision).HasColumnName("revision");
            entity.Property(row => row.ModelVersion).HasColumnName("model_version");
            entity.Property(row => row.SnapshotFormatVersion).HasColumnName("snapshot_format_version");
            entity.Property(row => row.Snapshot).HasColumnName("snapshot").HasColumnType("text").IsRequired();
            entity.Property(row => row.ActorId).HasColumnName("actor_id").HasMaxLength(256).IsRequired();
            entity.Property(row => row.ImportedAt).HasColumnName("imported_at").HasColumnType("timestamp with time zone");
        });
    }
}

internal sealed class ProfileRevisionRow
{
    public required string ProfileId { get; set; }
    public int Revision { get; set; }
    public int ModelVersion { get; set; }
    public int SnapshotFormatVersion { get; set; }
    public required string Snapshot { get; set; }
    public required string ActorId { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}
