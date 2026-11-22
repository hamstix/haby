using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<ResourceManagementRow> ResourceManagement => Set<ResourceManagementRow>();

    private static void ConfigureResourceManagement(ModelBuilder model)
    {
        model.Entity<ResourceManagementRow>(e =>
        {
            e.ToTable("resource_management", t => t.HasCheckConstraint("CK_resource_management_identity",
                "source_generation > 0 AND length(resource_local_id) > 0 AND ((origin = 'Provisioned' AND authority = 'Provisioned') OR (origin = 'Imported' AND authority = 'UseAndRetain'))"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Environment).HasColumnName("environment_record_id");
            e.Property(x => x.Application).HasColumnName("application_record_id");
            e.Property(x => x.Resource).HasColumnName("resource_local_id").HasMaxLength(63);
            e.Property(x => x.Deployment).HasColumnName("deployment_id");
            e.Property(x => x.SourceGeneration).HasColumnName("source_generation");
            e.Property(x => x.Origin).HasColumnName("origin").HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.Authority).HasColumnName("authority").HasConversion<string>().HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.Environment, x.Application, x.Resource, x.SourceGeneration }).IsUnique();
            e.HasOne<DeploymentGenerationRow>().WithMany().HasForeignKey(x => new { DeploymentId = x.Deployment, Generation = x.SourceGeneration })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<EnvironmentRow>().WithMany().HasForeignKey(x => x.Environment).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationRow>().WithMany().HasForeignKey(x => x.Application).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

// Created at initial desired acceptance, never inferred from object names or backfilled.
// The exact source generation supplies immutable accepted inputs and original evidence.
internal sealed class ResourceManagementRow
{
    public Guid Id { get; set; }
    public Guid Environment { get; set; }
    public Guid Application { get; set; }
    public string Resource { get; set; } = "";
    public Guid Deployment { get; set; }
    public long SourceGeneration { get; set; }
    public ResourceOrigin Origin { get; set; }
    public ResourceLifecycleAuthority Authority { get; set; }
}
