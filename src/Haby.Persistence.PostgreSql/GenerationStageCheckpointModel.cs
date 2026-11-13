using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<GenerationStageCheckpointRow> GenerationStageCheckpoints => Set<GenerationStageCheckpointRow>();
    private void ValidateCheckpointWrites()
    {
        if (ChangeTracker.Entries<GenerationStageCheckpointRow>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Generation checkpoints are immutable.");
    }
    private static void ConfigureGenerationStageCheckpoints(ModelBuilder model)
    {
        model.Entity<GenerationStageCheckpointRow>(e =>
        {
            e.ToTable("generation_stage_checkpoints", t =>
            {
                t.HasCheckConstraint("CK_generation_stage_checkpoints_identity", "generation > 0 AND format_version = 1 AND stage = 'ResourcesReady'");
                t.HasCheckConstraint("CK_generation_stage_checkpoints_bounds", "length(input_fingerprint) = 67 AND octet_length(evidence_json) <= 2097152");
            });
            e.HasKey(x => new { x.DeploymentId, x.Generation, x.Stage });
            e.HasAlternateKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            e.Property(x => x.Generation).HasColumnName("generation");
            e.Property(x => x.Stage).HasColumnName("stage").HasMaxLength(64);
            e.Property(x => x.FormatVersion).HasColumnName("format_version");
            e.Property(x => x.InputFingerprint).HasColumnName("input_fingerprint").HasMaxLength(67);
            e.Property(x => x.EvidenceJson).HasColumnName("evidence_json").HasColumnType("text");
            e.Property(x => x.EvidenceFingerprint).HasColumnName("evidence_fingerprint").HasMaxLength(64);
            e.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at");
            e.HasOne<DeploymentGenerationRow>().WithMany().HasForeignKey(x => new { x.DeploymentId, x.Generation }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class GenerationStageCheckpointRow
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public long Generation { get; set; }
    public string Stage { get; set; } = "";
    public int FormatVersion { get; set; }
    public string InputFingerprint { get; set; } = "";
    public string EvidenceJson { get; set; } = "";
    public string EvidenceFingerprint { get; set; } = "";
    public DateTimeOffset ConfirmedAt { get; set; }
}
