using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<PlacementRevisionRow> PlacementRevisions => Set<PlacementRevisionRow>();
    private static void ConfigurePlacementRevisions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlacementRevisionRow>(entity =>
        {
            entity.ToTable("placement_revisions", table =>
            {
                table.HasCheckConstraint("CK_placement_revision_positive", "revision > 0");
                table.HasCheckConstraint("CK_placement_name", "name ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$'");
                table.HasCheckConstraint("CK_placement_alias", "provider_alias ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'");
                table.HasCheckConstraint("CK_placement_actor", "length(actor_id) > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(x => x.ProviderAlias).HasColumnName("provider_alias").HasMaxLength(128);
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(63);
            entity.Property(x => x.Revision).HasColumnName("revision");
            entity.Property(x => x.Contract).HasColumnName("contract").HasMaxLength(128);
            entity.Property(x => x.FormatVersion).HasColumnName("format_version");
            entity.Property(x => x.Payload).HasColumnName("payload").HasColumnType("text");
            entity.Property(x => x.ActorId).HasColumnName("actor_id").HasMaxLength(256);
            entity.Property(x => x.ImportedAt).HasColumnName("imported_at");
            entity.HasOne<EnvironmentRow>().WithMany().HasForeignKey(x => x.EnvironmentRecordId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.EnvironmentRecordId, x.ProviderAlias, x.Name, x.Revision }).IsUnique().HasDatabaseName("UX_placement_revision_exact");
        });
    }
}

internal sealed class PlacementRevisionRow
{
    public Guid Id { get; set; }
    public Guid EnvironmentRecordId { get; set; }
    public required string ProviderAlias { get; set; }
    public required string Name { get; set; }
    public int Revision { get; set; }
    public required string Contract { get; set; }
    public int FormatVersion { get; set; }
    public required string Payload { get; set; }
    public required string ActorId { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}
