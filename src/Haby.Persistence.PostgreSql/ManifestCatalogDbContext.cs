using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Durable catalog schema, independent of manifest and domain objects.</summary>
public sealed class ManifestCatalogDbContext(DbContextOptions<ManifestCatalogDbContext> options) : DbContext(options)
{
    internal DbSet<ApplicationRow> Applications => Set<ApplicationRow>();
    internal DbSet<ManifestRevisionRow> Revisions => Set<ManifestRevisionRow>();
    internal DbSet<EnvironmentRow> Environments => Set<EnvironmentRow>();
    internal DbSet<SecretRow> Secrets => Set<SecretRow>();
    internal DbSet<SecretVersionRow> SecretVersions => Set<SecretVersionRow>();
    internal DbSet<SecretPayloadRow> SecretPayloads => Set<SecretPayloadRow>();
    internal DbSet<ProviderInstanceRow> ProviderInstances => Set<ProviderInstanceRow>();
    internal DbSet<ProviderConfigurationRevisionRow> ProviderConfigurationRevisions => Set<ProviderConfigurationRevisionRow>();
    internal DbSet<ProviderRevisionSecretRefRow> ProviderRevisionSecretRefs => Set<ProviderRevisionSecretRefRow>();
    internal DbSet<ProviderAliasRow> ProviderAliases => Set<ProviderAliasRow>();
    internal DbSet<ProviderAliasAuditRow> ProviderAliasAudits => Set<ProviderAliasAuditRow>();
    internal DbSet<DurableChangeSetRow> DurableChangeSets => Set<DurableChangeSetRow>();
    internal DbSet<DurablePlanRow> DurablePlans => Set<DurablePlanRow>();
    internal DbSet<DurablePlanClosureRow> DurablePlanClosures => Set<DurablePlanClosureRow>();
    internal DbSet<ApplicationDeploymentRow> ApplicationDeployments => Set<ApplicationDeploymentRow>();
    internal DbSet<DeploymentGenerationRow> DeploymentGenerations => Set<DeploymentGenerationRow>();
    internal DbSet<ChangeSetCommitRow> ChangeSetCommits => Set<ChangeSetCommitRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationRow>(entity =>
        {
            entity.ToTable("applications");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.ApplicationId).HasColumnName("application_id").HasMaxLength(128).IsRequired();
            entity.HasIndex(row => row.ApplicationId).IsUnique();
        });
        modelBuilder.Entity<ManifestRevisionRow>(entity =>
        {
            entity.ToTable("manifest_revisions");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.ApplicationRecordId).HasColumnName("application_record_id");
            entity.Property(row => row.ApplicationId).HasColumnName("application_id").HasMaxLength(128).IsRequired();
            entity.Property(row => row.ApplicationVersion).HasColumnName("application_version").HasMaxLength(128).IsRequired();
            entity.Property(row => row.LocalRevisionNumber).HasColumnName("local_revision_number");
            entity.Property(row => row.InputApiVersion).HasColumnName("input_api_version").IsRequired();
            entity.Property(row => row.NormalizedModelVersion).HasColumnName("normalized_model_version");
            entity.Property(row => row.SnapshotFormatVersion).HasColumnName("snapshot_format_version");
            entity.Property(row => row.Snapshot).HasColumnName("snapshot").HasColumnType("text").IsRequired();
            entity.Property(row => row.HistoricalWarnings).HasColumnName("historical_warnings").HasColumnType("text").IsRequired();
            entity.Property(row => row.ActorId).HasColumnName("actor_id").HasMaxLength(256).IsRequired();
            entity.Property(row => row.ImportedAt).HasColumnName("imported_at").HasColumnType("timestamp with time zone");
            entity.HasOne<ApplicationRow>().WithMany().HasForeignKey(row => row.ApplicationRecordId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(row => new { row.ApplicationId, row.ApplicationVersion }).IsUnique();
        });
        modelBuilder.Entity<EnvironmentRow>(entity =>
        {
            entity.ToTable("environments");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.EnvironmentId).HasColumnName("environment_id").HasMaxLength(64).IsRequired();
            entity.HasIndex(row => row.EnvironmentId).IsUnique();
        });
        modelBuilder.Entity<SecretRow>(entity =>
        {
            entity.ToTable("secrets");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(row => row.Owner).HasColumnName("owner").HasMaxLength(64).IsRequired();
            entity.Property(row => row.Purpose).HasColumnName("purpose").HasMaxLength(64).IsRequired();
            entity.Property(row => row.Sensitivity).HasColumnName("sensitivity").HasMaxLength(32).IsRequired();
            entity.Property(row => row.ContentType).HasColumnName("content_type").HasMaxLength(64).IsRequired();
            entity.Property(row => row.StorageBackend).HasColumnName("storage_backend").HasMaxLength(64).IsRequired();
            entity.Property(row => row.OwnershipPolicy).HasColumnName("ownership_policy").HasMaxLength(32).IsRequired();
            entity.Property(row => row.CurrentVersionId).HasColumnName("current_version_id");
            entity.HasOne<EnvironmentRow>().WithMany().HasForeignKey(row => row.EnvironmentRecordId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasAlternateKey(row => new { row.Id, row.EnvironmentRecordId });
            entity.HasOne<SecretVersionRow>().WithMany()
                .HasForeignKey(row => new { row.Id, row.CurrentVersionId })
                .HasPrincipalKey(row => new { row.SecretId, row.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SecretVersionRow>(entity =>
        {
            entity.ToTable("secret_versions");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.SecretId).HasColumnName("secret_id");
            entity.Property(row => row.ProtectionFormat).HasColumnName("protection_format").HasMaxLength(32).IsRequired();
            entity.Property(row => row.FormatVersion).HasColumnName("format_version");
            entity.HasOne<SecretRow>().WithMany().HasForeignKey(row => row.SecretId).OnDelete(DeleteBehavior.Restrict);
            entity.HasAlternateKey(row => new { row.SecretId, row.Id });
        });
        modelBuilder.Entity<SecretPayloadRow>(entity =>
        {
            entity.ToTable("secret_payloads");
            entity.HasKey(row => row.SecretVersionId);
            entity.Property(row => row.SecretVersionId).HasColumnName("secret_version_id");
            entity.Property(row => row.Bytes).HasColumnName("bytes").HasColumnType("bytea").IsRequired();
            entity.HasOne<SecretVersionRow>().WithOne().HasForeignKey<SecretPayloadRow>(row => row.SecretVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProviderInstanceRow>(entity =>
        {
            entity.ToTable("provider_instances");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(row => row.ProviderKindId).HasColumnName("provider_kind_id").HasMaxLength(128).IsRequired();
            entity.Property(row => row.TargetFormatVersion).HasColumnName("target_format_version");
            entity.Property(row => row.TargetIdentity).HasColumnName("target_identity").HasMaxLength(512).IsRequired();
            entity.HasOne<EnvironmentRow>().WithMany().HasForeignKey(row => row.EnvironmentRecordId).OnDelete(DeleteBehavior.Restrict);
            entity.HasAlternateKey(row => new { row.Id, row.EnvironmentRecordId });
        });
        modelBuilder.Entity<ProviderConfigurationRevisionRow>(entity =>
        {
            entity.ToTable("provider_configuration_revisions");
            entity.HasKey(row => new { row.ProviderInstanceId, row.RevisionNumber });
            entity.Property(row => row.ProviderInstanceId).HasColumnName("provider_instance_id");
            entity.Property(row => row.RevisionNumber).HasColumnName("revision_number");
            entity.Property(row => row.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(row => row.ImplementationId).HasColumnName("implementation_id").HasMaxLength(128).IsRequired();
            entity.Property(row => row.ConfigurationShapeId).HasColumnName("configuration_shape_id").HasMaxLength(128).IsRequired();
            entity.Property(row => row.PayloadFormatVersion).HasColumnName("payload_format_version");
            entity.Property(row => row.PayloadJson).HasColumnName("payload_json").HasColumnType("text").IsRequired();
            entity.Property(row => row.IsSealed).HasColumnName("is_sealed");
            entity.HasOne<ProviderInstanceRow>().WithMany()
                .HasForeignKey(row => new { row.ProviderInstanceId, row.EnvironmentRecordId })
                .HasPrincipalKey(row => new { row.Id, row.EnvironmentRecordId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasAlternateKey(row => new { row.ProviderInstanceId, row.RevisionNumber, row.EnvironmentRecordId });
        });
        modelBuilder.Entity<ProviderRevisionSecretRefRow>(entity =>
        {
            entity.ToTable("provider_revision_secret_refs");
            entity.HasKey(row => new { row.ProviderInstanceId, row.RevisionNumber, row.Slot });
            entity.Property(row => row.ProviderInstanceId).HasColumnName("provider_instance_id");
            entity.Property(row => row.RevisionNumber).HasColumnName("revision_number");
            entity.Property(row => row.Slot).HasColumnName("slot").HasMaxLength(128).IsRequired();
            entity.Property(row => row.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(row => row.SecretId).HasColumnName("secret_id");
            entity.Property(row => row.SecretVersionId).HasColumnName("secret_version_id");
            entity.HasOne<ProviderConfigurationRevisionRow>().WithMany()
                .HasForeignKey(row => new { row.ProviderInstanceId, row.RevisionNumber, row.EnvironmentRecordId })
                .HasPrincipalKey(row => new { row.ProviderInstanceId, row.RevisionNumber, row.EnvironmentRecordId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SecretRow>().WithMany()
                .HasForeignKey(row => new { row.SecretId, row.EnvironmentRecordId })
                .HasPrincipalKey(row => new { row.Id, row.EnvironmentRecordId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SecretVersionRow>().WithMany()
                .HasForeignKey(row => new { row.SecretId, row.SecretVersionId })
                .HasPrincipalKey(row => new { row.SecretId, row.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProviderAliasRow>(entity =>
        {
            entity.ToTable("provider_aliases");
            entity.HasKey(row => new { row.EnvironmentRecordId, row.Alias });
            entity.Property(row => row.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(row => row.Alias).HasColumnName("alias").HasMaxLength(128).IsRequired();
            entity.Property(row => row.ProviderInstanceId).HasColumnName("provider_instance_id");
            entity.Property(row => row.RevisionNumber).HasColumnName("revision_number");
            entity.HasIndex(row => row.ProviderInstanceId).IsUnique();
            entity.HasOne<EnvironmentRow>().WithMany().HasForeignKey(row => row.EnvironmentRecordId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderConfigurationRevisionRow>().WithMany()
                .HasForeignKey(row => new { row.ProviderInstanceId, row.RevisionNumber, row.EnvironmentRecordId })
                .HasPrincipalKey(row => new { row.ProviderInstanceId, row.RevisionNumber, row.EnvironmentRecordId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProviderAliasAuditRow>(entity =>
        {
            entity.ToTable("provider_alias_audit");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(row => row.Alias).HasColumnName("alias").HasMaxLength(128).IsRequired();
            entity.Property(row => row.PreviousInstanceId).HasColumnName("previous_instance_id");
            entity.Property(row => row.PreviousRevisionNumber).HasColumnName("previous_revision_number");
            entity.Property(row => row.NewInstanceId).HasColumnName("new_instance_id");
            entity.Property(row => row.NewRevisionNumber).HasColumnName("new_revision_number");
            entity.Property(row => row.TechnicalActor).HasColumnName("technical_actor").HasMaxLength(256).IsRequired();
            entity.Property(row => row.ChangedAt).HasColumnName("changed_at").HasColumnType("timestamp with time zone");
            entity.HasOne<EnvironmentRow>().WithMany().HasForeignKey(row => row.EnvironmentRecordId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DurableChangeSetRow>(entity =>
        {
            entity.ToTable("deployment_change_sets");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(row => row.ApplicationRecordId).HasColumnName("application_record_id");
            entity.Property(row => row.RevisionNumber).HasColumnName("revision_number");
            entity.Property(row => row.ExpectedAbsence).HasColumnName("expected_absence");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.ActorId).HasColumnName("actor_id").HasMaxLength(256).IsRequired();
            entity.HasOne<EnvironmentRow>().WithMany().HasForeignKey(row => row.EnvironmentRecordId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationRow>().WithMany().HasForeignKey(row => row.ApplicationRecordId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ManifestRevisionRow>().WithMany()
                .HasForeignKey(row => new { row.ApplicationRecordId, row.RevisionNumber })
                .HasPrincipalKey(row => new { row.ApplicationRecordId, row.LocalRevisionNumber })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DurablePlanRow>(entity =>
        {
            entity.ToTable("deployment_plans");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.ChangeSetId).HasColumnName("change_set_id");
            entity.Property(row => row.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(row => row.EnvironmentKey).HasColumnName("environment_key").HasMaxLength(64).IsRequired();
            entity.Property(row => row.ApplicationRecordId).HasColumnName("application_record_id");
            entity.Property(row => row.RevisionNumber).HasColumnName("revision_number");
            entity.Property(row => row.ProviderAlias).HasColumnName("provider_alias").HasMaxLength(128).IsRequired();
            entity.Property(row => row.ProviderInstanceId).HasColumnName("provider_instance_id");
            entity.Property(row => row.ConfigurationRevisionNumber).HasColumnName("configuration_revision_number");
            entity.Property(row => row.ImplementationId).HasColumnName("implementation_id").HasMaxLength(128).IsRequired();
            entity.Property(row => row.SnapshotFormatVersion).HasColumnName("snapshot_format_version");
            entity.Property(row => row.Fingerprint).HasColumnName("fingerprint").HasMaxLength(67).IsRequired();
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.ExpiresAt).HasColumnName("expires_at");
            entity.Property(row => row.ActorId).HasColumnName("actor_id").HasMaxLength(256).IsRequired();
            entity.HasOne<DurableChangeSetRow>().WithMany().HasForeignKey(row => row.ChangeSetId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderConfigurationRevisionRow>().WithMany()
                .HasForeignKey(row => new { row.ProviderInstanceId, RevisionNumber = row.ConfigurationRevisionNumber, row.EnvironmentRecordId })
                .HasPrincipalKey(row => new { row.ProviderInstanceId, row.RevisionNumber, row.EnvironmentRecordId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DurablePlanClosureRow>(entity =>
        {
            entity.ToTable("deployment_plan_closures");
            entity.HasKey(row => row.PlanId);
            entity.Property(row => row.PlanId).HasColumnName("plan_id");
            entity.Property(row => row.ClosedAt).HasColumnName("closed_at");
            entity.Property(row => row.ActorId).HasColumnName("actor_id").HasMaxLength(256).IsRequired();
            entity.HasOne<DurablePlanRow>().WithOne().HasForeignKey<DurablePlanClosureRow>(row => row.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ApplicationDeploymentRow>(entity =>
        {
            entity.ToTable("application_deployments", table => table.HasCheckConstraint("CK_application_deployments_desired_generation", "desired_generation = 1 AND applied_generation IS NULL"));
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(row => row.ApplicationRecordId).HasColumnName("application_record_id");
            entity.Property(row => row.DesiredGeneration).HasColumnName("desired_generation");
            entity.Property(row => row.AppliedGeneration).HasColumnName("applied_generation");
            entity.Property(row => row.ConcurrencyToken).HasColumnName("concurrency_token");
            entity.HasIndex(row => new { row.EnvironmentRecordId, row.ApplicationRecordId }).IsUnique();
            entity.HasOne<EnvironmentRow>().WithMany().HasForeignKey(row => row.EnvironmentRecordId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationRow>().WithMany().HasForeignKey(row => row.ApplicationRecordId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DeploymentGenerationRow>(entity =>
        {
            entity.ToTable("application_deployment_generations", table => table.HasCheckConstraint("CK_deployment_generations_first", "generation = 1"));
            entity.HasKey(row => new { row.DeploymentId, row.Generation });
            entity.Property(row => row.DeploymentId).HasColumnName("deployment_id");
            entity.Property(row => row.Generation).HasColumnName("generation");
            entity.Property(row => row.PlanId).HasColumnName("plan_id");
            entity.Property(row => row.ChangeSetId).HasColumnName("change_set_id");
            entity.Property(row => row.ApplicationRecordId).HasColumnName("application_record_id");
            entity.Property(row => row.RevisionNumber).HasColumnName("revision_number");
            entity.Property(row => row.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(row => row.EnvironmentKey).HasColumnName("environment_key").HasMaxLength(64).IsRequired();
            entity.Property(row => row.ProviderAlias).HasColumnName("provider_alias").HasMaxLength(128).IsRequired();
            entity.Property(row => row.ProviderInstanceId).HasColumnName("provider_instance_id");
            entity.Property(row => row.ConfigurationRevisionNumber).HasColumnName("configuration_revision_number");
            entity.Property(row => row.ImplementationId).HasColumnName("implementation_id").HasMaxLength(128).IsRequired();
            entity.Property(row => row.Fingerprint).HasColumnName("fingerprint").HasMaxLength(67).IsRequired();
            entity.Property(row => row.AcceptedAt).HasColumnName("accepted_at");
            entity.Property(row => row.ActorId).HasColumnName("actor_id").HasMaxLength(256).IsRequired();
            entity.HasOne<ApplicationDeploymentRow>().WithMany().HasForeignKey(row => row.DeploymentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurablePlanRow>().WithMany().HasForeignKey(row => row.PlanId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableChangeSetRow>().WithMany().HasForeignKey(row => row.ChangeSetId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ManifestRevisionRow>().WithMany().HasForeignKey(row => new { row.ApplicationRecordId, row.RevisionNumber })
                .HasPrincipalKey(row => new { row.ApplicationRecordId, row.LocalRevisionNumber }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderConfigurationRevisionRow>().WithMany()
                .HasForeignKey(row => new { row.ProviderInstanceId, RevisionNumber = row.ConfigurationRevisionNumber, row.EnvironmentRecordId })
                .HasPrincipalKey(row => new { row.ProviderInstanceId, row.RevisionNumber, row.EnvironmentRecordId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ChangeSetCommitRow>(entity =>
        {
            entity.ToTable("change_set_commits", table => table.HasCheckConstraint("CK_change_set_commits_first", "generation = 1"));
            entity.HasKey(row => row.ChangeSetId);
            entity.Property(row => row.ChangeSetId).HasColumnName("change_set_id");
            entity.Property(row => row.PlanId).HasColumnName("plan_id");
            entity.Property(row => row.DeploymentId).HasColumnName("deployment_id");
            entity.Property(row => row.Generation).HasColumnName("generation");
            entity.Property(row => row.CommittedAt).HasColumnName("committed_at");
            entity.Property(row => row.ActorId).HasColumnName("actor_id").HasMaxLength(256).IsRequired();
            entity.HasOne<DurableChangeSetRow>().WithOne().HasForeignKey<ChangeSetCommitRow>(row => row.ChangeSetId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurablePlanRow>().WithMany().HasForeignKey(row => row.PlanId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DeploymentGenerationRow>().WithOne().HasForeignKey<ChangeSetCommitRow>(row => new { row.DeploymentId, row.Generation })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class DurableChangeSetRow
{
    public Guid Id { get; set; }
    public Guid EnvironmentRecordId { get; set; }
    public Guid ApplicationRecordId { get; set; }
    public long RevisionNumber { get; set; }
    public bool ExpectedAbsence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string ActorId { get; set; }
}

internal sealed class DurablePlanRow
{
    public Guid Id { get; set; }
    public Guid ChangeSetId { get; set; }
    public Guid EnvironmentRecordId { get; set; }
    public required string EnvironmentKey { get; set; }
    public Guid ApplicationRecordId { get; set; }
    public long RevisionNumber { get; set; }
    public required string ProviderAlias { get; set; }
    public Guid ProviderInstanceId { get; set; }
    public long ConfigurationRevisionNumber { get; set; }
    public required string ImplementationId { get; set; }
    public int SnapshotFormatVersion { get; set; }
    public required string Fingerprint { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public required string ActorId { get; set; }
}

internal sealed class DurablePlanClosureRow
{
    public Guid PlanId { get; set; }
    public DateTimeOffset ClosedAt { get; set; }
    public required string ActorId { get; set; }
}

internal sealed class ApplicationDeploymentRow
{
    public Guid Id { get; set; }
    public Guid EnvironmentRecordId { get; set; }
    public Guid ApplicationRecordId { get; set; }
    public long DesiredGeneration { get; set; }
    public long? AppliedGeneration { get; set; }
    public long ConcurrencyToken { get; set; }
}

internal sealed class DeploymentGenerationRow
{
    public Guid DeploymentId { get; set; }
    public long Generation { get; set; }
    public Guid PlanId { get; set; }
    public Guid ChangeSetId { get; set; }
    public Guid ApplicationRecordId { get; set; }
    public long RevisionNumber { get; set; }
    public Guid EnvironmentRecordId { get; set; }
    public required string EnvironmentKey { get; set; }
    public required string ProviderAlias { get; set; }
    public Guid ProviderInstanceId { get; set; }
    public long ConfigurationRevisionNumber { get; set; }
    public required string ImplementationId { get; set; }
    public required string Fingerprint { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public required string ActorId { get; set; }
}

internal sealed class ChangeSetCommitRow
{
    public Guid ChangeSetId { get; set; }
    public Guid PlanId { get; set; }
    public Guid DeploymentId { get; set; }
    public long Generation { get; set; }
    public DateTimeOffset CommittedAt { get; set; }
    public required string ActorId { get; set; }
}

internal sealed class EnvironmentRow
{
    public Guid Id { get; set; }
    public required string EnvironmentId { get; set; }
}

internal sealed class SecretRow
{
    public Guid Id { get; set; }
    public Guid EnvironmentRecordId { get; set; }
    public required string Owner { get; set; }
    public required string Purpose { get; set; }
    public required string Sensitivity { get; set; }
    public required string ContentType { get; set; }
    public required string StorageBackend { get; set; }
    public required string OwnershipPolicy { get; set; }
    public Guid? CurrentVersionId { get; set; }
}

internal sealed class SecretVersionRow
{
    public Guid Id { get; set; }
    public Guid SecretId { get; set; }
    public required string ProtectionFormat { get; set; }
    public int FormatVersion { get; set; }
}

internal sealed class SecretPayloadRow
{
    public Guid SecretVersionId { get; set; }
    public required byte[] Bytes { get; set; }
}

internal sealed class ProviderInstanceRow
{
    public Guid Id { get; set; }
    public Guid EnvironmentRecordId { get; set; }
    public required string ProviderKindId { get; set; }
    public int TargetFormatVersion { get; set; }
    public required string TargetIdentity { get; set; }
}

internal sealed class ProviderConfigurationRevisionRow
{
    public Guid ProviderInstanceId { get; set; }
    public long RevisionNumber { get; set; }
    public Guid EnvironmentRecordId { get; set; }
    public required string ImplementationId { get; set; }
    public required string ConfigurationShapeId { get; set; }
    public int PayloadFormatVersion { get; set; }
    public required string PayloadJson { get; set; }
    public bool IsSealed { get; set; }
}

internal sealed class ProviderRevisionSecretRefRow
{
    public Guid ProviderInstanceId { get; set; }
    public long RevisionNumber { get; set; }
    public required string Slot { get; set; }
    public Guid EnvironmentRecordId { get; set; }
    public Guid SecretId { get; set; }
    public Guid SecretVersionId { get; set; }
}

internal sealed class ProviderAliasRow
{
    public Guid EnvironmentRecordId { get; set; }
    public required string Alias { get; set; }
    public Guid ProviderInstanceId { get; set; }
    public long RevisionNumber { get; set; }
}

internal sealed class ProviderAliasAuditRow
{
    public Guid Id { get; set; }
    public Guid EnvironmentRecordId { get; set; }
    public required string Alias { get; set; }
    public Guid? PreviousInstanceId { get; set; }
    public long? PreviousRevisionNumber { get; set; }
    public Guid NewInstanceId { get; set; }
    public long NewRevisionNumber { get; set; }
    public required string TechnicalActor { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

internal sealed class ApplicationRow
{
    public Guid Id { get; set; }
    public required string ApplicationId { get; set; }
}

internal sealed class ManifestRevisionRow
{
    public Guid Id { get; set; }
    public Guid ApplicationRecordId { get; set; }
    public required string ApplicationId { get; set; }
    public required string ApplicationVersion { get; set; }
    public long LocalRevisionNumber { get; set; }
    public required string InputApiVersion { get; set; }
    public int NormalizedModelVersion { get; set; }
    public int SnapshotFormatVersion { get; set; }
    public required string Snapshot { get; set; }
    public required string HistoricalWarnings { get; set; }
    public required string ActorId { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}
