using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<PlacementSelectionRow> PlacementSelections => Set<PlacementSelectionRow>();
    private static void ConfigurePlacementSelections(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlacementSelectionRow>(entity =>
        {
            entity.ToTable("placement_selections", table =>
            {
                table.HasCheckConstraint("CK_placement_selection_target", "(target_kind = 'named' AND selector ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$' AND application_id = '' AND component_ref = '') OR (target_kind = 'environment' AND selector = '' AND application_id = '' AND component_ref = '') OR (target_kind = 'application' AND selector = '' AND application_id ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$' AND component_ref = '') OR (target_kind = 'component' AND selector = '' AND application_id ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$' AND component_ref ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$')");
                table.HasCheckConstraint("CK_placement_selection_audit", "length(actor_id) > 0 AND token <> '00000000-0000-0000-0000-000000000000'::uuid AND ((target_kind = 'component' AND checked_application_record_id IS NOT NULL AND checked_revision_number > 0 AND checked_revision_number IS NOT NULL) OR (target_kind <> 'component' AND checked_application_record_id IS NULL AND checked_revision_number IS NULL))");
            });
            entity.HasKey(x => new { x.EnvironmentRecordId, x.ProviderAlias, x.TargetKind, x.Selector, x.ApplicationId, x.ComponentRef });
            entity.Property(x => x.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(x => x.ProviderAlias).HasColumnName("provider_alias").HasMaxLength(128);
            entity.Property(x => x.TargetKind).HasColumnName("target_kind").HasMaxLength(16);
            entity.Property(x => x.Selector).HasColumnName("selector").HasMaxLength(63);
            entity.Property(x => x.ApplicationId).HasColumnName("application_id").HasMaxLength(128);
            entity.Property(x => x.ComponentRef).HasColumnName("component_ref").HasMaxLength(63);
            entity.Property(x => x.RevisionId).HasColumnName("revision_id");
            entity.Property(x => x.Token).HasColumnName("token").IsConcurrencyToken();
            entity.Property(x => x.ActorId).HasColumnName("actor_id").HasMaxLength(256);
            entity.Property(x => x.ChangedAtUtc).HasColumnName("changed_at_utc");
            entity.Property(x => x.CheckedApplicationRecordId).HasColumnName("checked_application_record_id");
            entity.Property(x => x.CheckedRevisionNumber).HasColumnName("checked_revision_number");
            entity.HasOne(x => x.Revision).WithMany().HasForeignKey(x => new { x.RevisionId, x.EnvironmentRecordId, x.ProviderAlias })
                .HasPrincipalKey(x => new { x.Id, x.EnvironmentRecordId, x.ProviderAlias }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
internal sealed class PlacementSelectionRow
{
    public Guid EnvironmentRecordId { get; set; }
    public required string ProviderAlias { get; set; }
    public required string TargetKind { get; set; }
    public required string Selector { get; set; }
    public required string ApplicationId { get; set; }
    public required string ComponentRef { get; set; }
    public Guid RevisionId { get; set; }
    public PlacementRevisionRow Revision { get; set; } = null!;
    public Guid Token { get; set; }
    public required string ActorId { get; set; }
    public DateTimeOffset ChangedAtUtc { get; set; }
    public Guid? CheckedApplicationRecordId { get; set; }
    public long? CheckedRevisionNumber { get; set; }
}
