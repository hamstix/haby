using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<ResourceNamingPolicyRow> ResourceNamingPolicies => Set<ResourceNamingPolicyRow>();
    internal DbSet<ResourceNamingSelectionRow> ResourceNamingSelections => Set<ResourceNamingSelectionRow>();

    private static void ConfigureResourceNaming(ModelBuilder model)
    {
        model.Entity<ResourceNamingPolicyRow>(e =>
        {
            e.ToTable("resource_naming_policies", t => t.HasCheckConstraint("CK_resource_naming_revision", "revision > 0"));
            e.HasKey(x => new { x.Environment, x.Name, x.Revision });
            e.Property(x => x.Environment).HasColumnName("environment_record_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(64);
            e.Property(x => x.Revision).HasColumnName("revision");
            e.Property(x => x.Snapshot).HasColumnName("snapshot").IsRequired();
            e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(256).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<EnvironmentRow>().WithMany().HasForeignKey(x => x.Environment).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ResourceNamingSelectionRow>(e =>
        {
            e.ToTable("resource_naming_selections");
            e.HasKey(x => new { x.Environment, x.Alias });
            e.Property(x => x.Environment).HasColumnName("environment_record_id");
            e.Property(x => x.Alias).HasColumnName("alias").HasMaxLength(128);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
            e.Property(x => x.Revision).HasColumnName("revision");
            e.Property(x => x.Token).HasColumnName("token");
            e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(256).IsRequired();
            e.Property(x => x.ChangedAt).HasColumnName("changed_at");
            e.HasOne<ProviderAliasRow>().WithMany().HasForeignKey(x => new { x.Environment, x.Alias }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ResourceNamingPolicyRow>().WithMany().HasForeignKey(x => new { x.Environment, x.Name, x.Revision }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class ResourceNamingPolicyRow
{
    public Guid Environment { get; set; }
    public string Name { get; set; } = null!;
    public long Revision { get; set; }
    public string Snapshot { get; set; } = null!;
    public string Actor { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
internal sealed class ResourceNamingSelectionRow
{
    public Guid Environment { get; set; }
    public string Alias { get; set; } = null!;
    public string Name { get; set; } = null!;
    public long Revision { get; set; }
    public Guid Token { get; set; }
    public string Actor { get; set; } = null!;
    public DateTimeOffset ChangedAt { get; set; }
}
