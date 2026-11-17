using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

// Original owned-object inventory remains immutable. This projection advances only
// alongside a confirmed action receipt; its action pointers retain the full history.
internal sealed class ManagedObjectStateRow
{
    public Guid ManagedWorkloadId { get; set; }
    public required string Role { get; set; }
    public Guid DeploymentId { get; set; }
    public long OriginGeneration { get; set; }
    public required string OriginOperation { get; set; }
    public long Generation { get; set; }
    public required string OperationId { get; set; }
    public required string Contract { get; set; }
    public int Format { get; set; }
    public required string Payload { get; set; }
}

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<ManagedObjectStateRow> ManagedObjectStates => Set<ManagedObjectStateRow>();
    private static void ConfigureManagedObjectStates(ModelBuilder model)
    {
        model.Entity<ManagedObjectStateRow>(e =>
        {
            e.ToTable("managed_object_states", t => t.HasCheckConstraint("CK_managed_object_state", "format > 0 AND generation > origin_generation AND octet_length(payload) BETWEEN 1 AND 1048576"));
            e.HasKey(x => new { x.ManagedWorkloadId, x.Role });
            e.Property(x => x.ManagedWorkloadId).HasColumnName("managed_workload_id");
            e.Property(x => x.Role).HasColumnName("role").HasMaxLength(128);
            e.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            e.Property(x => x.OriginGeneration).HasColumnName("origin_generation");
            e.Property(x => x.OriginOperation).HasColumnName("origin_operation").HasMaxLength(128);
            e.Property(x => x.Generation).HasColumnName("generation").IsConcurrencyToken();
            e.Property(x => x.OperationId).HasColumnName("operation_id").HasMaxLength(128).IsConcurrencyToken();
            e.Property(x => x.Contract).HasColumnName("contract").HasMaxLength(128);
            e.Property(x => x.Format).HasColumnName("format");
            e.Property(x => x.Payload).HasColumnName("payload");
            e.HasOne<ManagedWorkloadRow>().WithMany().HasForeignKey(x => x.ManagedWorkloadId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DurableActionIntentRow>().WithMany().HasForeignKey(x => new { x.DeploymentId, x.Generation, x.OperationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DurableActionIntentRow>().WithMany().HasForeignKey(x => new { x.DeploymentId, Generation = x.OriginGeneration, OperationId = x.OriginOperation }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
