using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<ResourceManagementHeadRow> ResourceManagementHeads => Set<ResourceManagementHeadRow>();
    internal IQueryable<ResourceManagementRow> CurrentResourceManagement =>
        from history in ResourceManagement
        join head in ResourceManagementHeads on history.Id equals head.Management
        select history;

    private static void ConfigureResourceManagementHeads(ModelBuilder model)
    {
        model.Entity<ResourceManagementHeadRow>(e =>
        {
            e.ToTable("resource_management_heads");
            e.HasKey(x => new { x.Environment, x.Application, x.Resource });
            e.Property(x => x.Environment).HasColumnName("environment_record_id");
            e.Property(x => x.Application).HasColumnName("application_record_id");
            e.Property(x => x.Resource).HasColumnName("resource_local_id").HasMaxLength(63);
            e.Property(x => x.Management).HasColumnName("management_id");
            e.HasIndex(x => x.Management).IsUnique();
            e.HasOne<ResourceManagementRow>().WithMany().HasForeignKey(x => new { x.Management, x.Environment, x.Application, x.Resource })
                .HasPrincipalKey(x => new { x.Id, x.Environment, x.Application, x.Resource }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class ResourceManagementHeadRow
{
    public Guid Environment { get; set; }
    public Guid Application { get; set; }
    public string Resource { get; set; } = "";
    public Guid Management { get; set; }
}
