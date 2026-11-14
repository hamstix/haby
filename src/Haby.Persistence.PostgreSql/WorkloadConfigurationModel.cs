using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<WorkloadConfigurationRecipientRow> WorkloadConfigurationRecipients => Set<WorkloadConfigurationRecipientRow>();
    private static void ConfigureWorkloadConfigurationRecipients(ModelBuilder builder)
    {
        builder.Entity<WorkloadConfigurationRecipientRow>(e =>
        {
            e.ToTable("workload_configuration_recipients");
            e.HasKey(x => new { x.DeploymentId, x.Generation, x.Component, x.Workload, x.Document });
            e.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            e.Property(x => x.Generation).HasColumnName("generation");
            e.Property(x => x.Component).HasColumnName("component").HasMaxLength(128);
            e.Property(x => x.Workload).HasColumnName("workload").HasMaxLength(128);
            e.Property(x => x.Document).HasColumnName("document").HasMaxLength(128);
            e.Property(x => x.RevisionId).HasColumnName("revision_id");
            e.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            e.Property(x => x.InputFingerprint).HasColumnName("input_fingerprint").HasMaxLength(128);
            e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(256);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<DeploymentGenerationRow>().WithMany().HasForeignKey(x => new { x.DeploymentId, x.Generation }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConfigurationDocumentRow>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GenerationStageCheckpointRow>().WithMany().HasForeignKey(x => x.CheckpointId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class WorkloadConfigurationRecipientRow
{
    public Guid DeploymentId { get; set; }
    public long Generation { get; set; }
    public required string Component { get; set; }
    public required string Workload { get; set; }
    public required string Document { get; set; }
    public Guid RevisionId { get; set; }
    public Guid CheckpointId { get; set; }
    public required string InputFingerprint { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
