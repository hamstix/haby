using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<GenerationObservationAttemptRow> ObservationAttempts => Set<GenerationObservationAttemptRow>();
    internal DbSet<GenerationObservationRecordRow> ObservationRecords => Set<GenerationObservationRecordRow>();
    private void ValidateObservationWrites()
    {
        if (ChangeTracker.Entries().Any(e => e.Entity is GenerationObservationAttemptRow or GenerationObservationRecordRow &&
            e.State is EntityState.Modified or EntityState.Deleted)) throw new InvalidOperationException("Observation history is immutable.");
    }
    private static void ConfigureGenerationObservations(ModelBuilder model)
    {
        model.Entity<GenerationObservationAttemptRow>(e =>
        {
            e.ToTable("generation_observation_attempts", t => t.HasCheckConstraint("CK_observation_attempt_bounds",
                "ordinal > 0 AND budget_seconds BETWEEN 1 AND 86400 AND deadline = started_at + budget_seconds * interval '1 second' AND length(subject) BETWEEN 1 AND 256 AND octet_length(origin_json) BETWEEN 1 AND 16384 AND length(actor) BETWEEN 1 AND 256"));
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.Id, x.DeploymentId, x.Generation });
            e.HasIndex(x => new { x.DeploymentId, x.Generation, x.Subject, x.Ordinal }).IsUnique();
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            e.Property(x => x.Generation).HasColumnName("generation");
            e.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(256);
            e.Property(x => x.OperationId).HasColumnName("operation_id").HasMaxLength(128);
            e.Property(x => x.Ordinal).HasColumnName("ordinal");
            e.Property(x => x.Previous).HasColumnName("previous");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.BudgetSeconds).HasColumnName("budget_seconds");
            e.Property(x => x.Deadline).HasColumnName("deadline");
            e.Property(x => x.OriginJson).HasColumnName("origin_json");
            e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(256);
            e.HasOne<DurableActionIntentRow>().WithMany().HasForeignKey(x => new { x.DeploymentId, x.Generation, x.OperationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GenerationObservationAttemptRow>().WithMany().HasForeignKey(x => new { x.Previous, x.DeploymentId, x.Generation })
                .HasPrincipalKey(x => new { x.Id, x.DeploymentId, x.Generation }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GenerationObservationRecordRow>(e =>
        {
            e.ToTable("generation_observation_records", t => t.HasCheckConstraint("CK_observation_record_bounds",
                "kind IN ('Request', 'Read', 'Expired') AND ordinal >= 0 AND length(request) BETWEEN 1 AND 128 AND octet_length(input_json) <= 131072 AND (payload IS NULL OR octet_length(payload) BETWEEN 1 AND 2097152) AND (finished_at IS NULL) = (payload IS NULL)"));
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DeploymentId, x.Generation, x.Request }).IsUnique().HasFilter("kind = 'Request'");
            e.HasIndex(x => x.AttemptId).IsUnique().HasFilter("kind = 'Expired'");
            e.HasIndex(x => new { x.DeploymentId, x.Generation, x.Request, x.Subject, x.Ordinal }).IsUnique().HasFilter("kind = 'Read'");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            e.Property(x => x.Generation).HasColumnName("generation");
            e.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(256);
            e.Property(x => x.AttemptId).HasColumnName("attempt_id");
            e.Property(x => x.Request).HasColumnName("request").HasMaxLength(128);
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(32);
            e.Property(x => x.Ordinal).HasColumnName("ordinal");
            e.Property(x => x.InputJson).HasColumnName("input_json");
            e.Property(x => x.ReservedAt).HasColumnName("reserved_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.Property(x => x.Payload).HasColumnName("payload");
            e.HasOne<DeploymentGenerationRow>().WithMany().HasForeignKey(x => new { x.DeploymentId, x.Generation }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GenerationObservationAttemptRow>().WithMany().HasForeignKey(x => new { x.AttemptId, x.DeploymentId, x.Generation })
                .HasPrincipalKey(x => new { x.Id, x.DeploymentId, x.Generation }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class GenerationObservationAttemptRow
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public long Generation { get; set; }
    public string Subject { get; set; } = "";
    public string OperationId { get; set; } = "";
    public int Ordinal { get; set; }
    public Guid? Previous { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public int BudgetSeconds { get; set; }
    public DateTimeOffset Deadline { get; set; }
    public string OriginJson { get; set; } = "";
    public string Actor { get; set; } = "";
}
internal sealed class GenerationObservationRecordRow
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public long Generation { get; set; }
    public string Subject { get; set; } = "";
    public Guid? AttemptId { get; set; }
    public string Request { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Ordinal { get; set; }
    public string InputJson { get; set; } = "";
    public DateTimeOffset ReservedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Payload { get; set; }
}
