using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<DurableActionIntentRow> ActionIntents => Set<DurableActionIntentRow>();
    internal DbSet<DurableActionObservationRow> ActionObservations => Set<DurableActionObservationRow>();
    internal DbSet<InternalPlanInputRow> InternalPlanInputs => Set<InternalPlanInputRow>();
    private void ValidateInternalActionWrites()
    {
        if (ChangeTracker.Entries().Any(x => x.Entity is DurableActionIntentRow or DurableActionObservationRow or InternalPlanInputRow or WorkloadConfigurationRecipientRow or InstallationIdentityRow or ManagedWorkloadRow or OwnedObjectRow or RuntimeIdentityBindingRow &&
            x.State is EntityState.Modified or EntityState.Deleted)) throw new InvalidOperationException("Action history is immutable.");
    }
    private static void ConfigureInternalActions(ModelBuilder model)
    {
        model.Entity<InternalPlanInputRow>(e =>
        {
            e.ToTable("internal_plan_inputs");
            e.HasKey(x => new { x.PlanId, x.Subject });
            e.Property(x => x.PlanId).HasColumnName("plan_id");
            e.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(256);
            e.Property(x => x.PlacementId).HasColumnName("placement_id");
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.ConfigurationRevision).HasColumnName("configuration_revision");
            e.HasOne<ProviderConfigurationRevisionRow>().WithMany().HasForeignKey(x => new { x.ProviderId, x.ConfigurationRevision }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DurablePlanRow>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PlacementRevisionRow>().WithMany().HasForeignKey(x => x.PlacementId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<DurableActionIntentRow>(e =>
        {
            e.ToTable("durable_action_intents", t =>
            {
                t.HasCheckConstraint("CK_action_intent_bounds", "format_version = 1 AND octet_length(payload) BETWEEN 1 AND 1048576 AND length(request_id) BETWEEN 1 AND 128 AND marker <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("CK_action_intent_subject", "subject_kind IN ('Resource', 'Workload') AND (subject_kind = 'Workload' OR component = '')");
            });
            e.HasKey(x => new { x.DeploymentId, x.Generation, x.OperationId });
            e.HasIndex(x => new { x.DeploymentId, x.Generation, x.RequestId }).IsUnique();
            e.HasIndex(x => x.Marker).IsUnique();
            e.HasIndex(x => new { x.DeploymentId, x.Generation, x.SubjectKind, x.Component, x.LocalId, x.ActionContract }).IsUnique();
            e.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            e.Property(x => x.Generation).HasColumnName("generation");
            e.Property(x => x.OperationId).HasColumnName("operation_id").HasMaxLength(128);
            e.Property(x => x.RequestId).HasColumnName("request_id").HasMaxLength(128);
            e.Property(x => x.Marker).HasColumnName("marker");
            e.Property(x => x.ManagedIdentity).HasColumnName("managed_identity");
            e.Property(x => x.FormatVersion).HasColumnName("format_version");
            e.Property(x => x.SubjectKind).HasColumnName("subject_kind").HasMaxLength(16);
            e.Property(x => x.Component).HasColumnName("component").HasMaxLength(63);
            e.Property(x => x.LocalId).HasColumnName("local_id").HasMaxLength(63);
            e.Property(x => x.SubjectContract).HasColumnName("subject_contract").HasMaxLength(128);
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.ConfigurationRevision).HasColumnName("configuration_revision");
            e.Property(x => x.ProviderKind).HasColumnName("provider_kind").HasMaxLength(128);
            e.Property(x => x.ConfigurationContract).HasColumnName("configuration_contract").HasMaxLength(128);
            e.Property(x => x.Implementation).HasColumnName("implementation").HasMaxLength(128);
            e.Property(x => x.ActionContract).HasColumnName("action_contract").HasMaxLength(128);
            e.Property(x => x.InputFormat).HasColumnName("input_format").HasMaxLength(128);
            e.Property(x => x.OutputContract).HasColumnName("output_contract").HasMaxLength(128);
            e.Property(x => x.Payload).HasColumnName("payload");
            e.Property(x => x.ObservationRecovery).HasColumnName("observation_recovery");
            e.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            e.Property(x => x.InputFingerprint).HasColumnName("input_fingerprint").HasMaxLength(67);
            e.Property(x => x.PlacementId).HasColumnName("placement_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<DurableOperationRow>().WithOne().HasForeignKey<DurableActionIntentRow>(x => new { x.DeploymentId, x.Generation, x.OperationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProviderConfigurationRevisionRow>().WithMany().HasForeignKey(x => new { x.ProviderId, x.ConfigurationRevision }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PlacementRevisionRow>().WithMany().HasForeignKey(x => x.PlacementId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GenerationStageCheckpointRow>().WithMany().HasForeignKey(x => x.CheckpointId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<DurableActionObservationRow>(e =>
        {
            e.ToTable("durable_operation_observations", t => t.HasCheckConstraint("CK_action_observation_bounds", "format_version = 1 AND outcome IN ('Confirmed', 'Unavailable', 'NotConfirmed', 'Refused') AND confirmed = (outcome = 'Confirmed') AND ordinal > 0 AND length(code) BETWEEN 1 AND 128 AND (receipt IS NULL OR octet_length(receipt) BETWEEN 1 AND 1048576) AND confirmed = (receipt IS NOT NULL)"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            e.Property(x => x.Generation).HasColumnName("generation");
            e.Property(x => x.OperationId).HasColumnName("operation_id").HasMaxLength(128);
            e.Property(x => x.AttemptId).HasColumnName("attempt_id");
            e.Property(x => x.Ordinal).HasColumnName("ordinal");
            e.Property(x => x.At).HasColumnName("observed_at");
            e.Property(x => x.Confirmed).HasColumnName("confirmed");
            e.Property(x => x.FormatVersion).HasColumnName("format_version");
            e.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(32);
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(128);
            e.Property(x => x.Receipt).HasColumnName("receipt");
            e.HasIndex(x => new { x.DeploymentId, x.Generation, x.OperationId, x.Ordinal }).IsUnique();
            e.HasIndex(x => new { x.DeploymentId, x.Generation, x.OperationId }).IsUnique().HasFilter("confirmed");
            e.HasOne<DurableActionIntentRow>().WithMany().HasForeignKey(x => new { x.DeploymentId, x.Generation, x.OperationId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DurableOperationAttemptRow>().WithMany().HasForeignKey(x => new { x.AttemptId, x.DeploymentId, x.Generation, x.OperationId })
                .HasPrincipalKey(x => new { x.AttemptId, x.DeploymentId, x.Generation, x.OperationId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class InternalPlanInputRow
{
    public Guid PlanId { get; set; }
    public string Subject { get; set; } = "";
    public Guid? PlacementId { get; set; }
    public Guid ProviderId { get; set; }
    public long ConfigurationRevision { get; set; }
}
internal sealed class DurableActionIntentRow
{
    public Guid DeploymentId { get; set; }
    public long Generation { get; set; }
    public string OperationId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public Guid Marker { get; set; }
    public Guid ManagedIdentity { get; set; }
    public int FormatVersion { get; set; }
    public string SubjectKind { get; set; } = "";
    public string Component { get; set; } = "";
    public string LocalId { get; set; } = "";
    public string SubjectContract { get; set; } = "";
    public Guid ProviderId { get; set; }
    public long ConfigurationRevision { get; set; }
    public string ProviderKind { get; set; } = "";
    public string ConfigurationContract { get; set; } = "";
    public string Implementation { get; set; } = "";
    public string ActionContract { get; set; } = "";
    public string InputFormat { get; set; } = "";
    public string OutputContract { get; set; } = "";
    public string Payload { get; set; } = "";
    public bool ObservationRecovery { get; set; }
    public Guid CheckpointId { get; set; }
    public string InputFingerprint { get; set; } = "";
    public Guid? PlacementId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
internal sealed class DurableActionObservationRow
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public long Generation { get; set; }
    public string OperationId { get; set; } = "";
    public Guid AttemptId { get; set; }
    public long Ordinal { get; set; }
    public DateTimeOffset At { get; set; }
    public bool Confirmed { get; set; }
    public int FormatVersion { get; set; }
    public string Outcome { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Receipt { get; set; }
}
