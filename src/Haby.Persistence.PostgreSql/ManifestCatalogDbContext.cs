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
    internal DbSet<SecretCreationReceiptRow> SecretCreationReceipts => Set<SecretCreationReceiptRow>();
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
    internal DbSet<DurableCreateOperationRow> DurableCreateOperations => Set<DurableCreateOperationRow>();
    internal DbSet<DurableCreateOperationAttemptRow> DurableCreateOperationAttempts =>
        Set<DurableCreateOperationAttemptRow>();
    internal DbSet<PostgreSqlDatabaseRecoveryDecisionRow> PostgreSqlDatabaseRecoveryDecisions =>
        Set<PostgreSqlDatabaseRecoveryDecisionRow>();

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
            entity.ToTable("secrets", table => table.HasCheckConstraint("CK_secrets_name", "name IS NULL OR name ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$'"));
            entity.Property(row => row.Name).HasColumnName("name").HasMaxLength(64);
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(row => new { row.EnvironmentRecordId, row.Name }).IsUnique();
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
            entity.ToTable("secret_versions", table => table.HasCheckConstraint("CK_secret_versions_number", "number IS NULL OR number > 0"));
            entity.Property(row => row.Number).HasColumnName("number");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(row => new { row.SecretId, row.Number }).IsUnique();
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.SecretId).HasColumnName("secret_id");
            entity.Property(row => row.ProtectionFormat).HasColumnName("protection_format").HasMaxLength(32).IsRequired();
            entity.Property(row => row.FormatVersion).HasColumnName("format_version");
            entity.HasOne<SecretRow>().WithMany().HasForeignKey(row => row.SecretId).OnDelete(DeleteBehavior.Restrict);
            entity.HasAlternateKey(row => new { row.SecretId, row.Id });
        });
        modelBuilder.Entity<SecretCreationReceiptRow>(entity =>
        {
            entity.ToTable("secret_creation_receipts");
            entity.HasKey(row => new { row.EnvironmentRecordId, row.Operation, row.RequestId });
            entity.Property(row => row.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(row => row.Operation).HasColumnName("operation").HasMaxLength(64);
            entity.Property(row => row.RequestId).HasColumnName("request_id");
            entity.Property(row => row.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
            entity.Property(row => row.SecretId).HasColumnName("secret_id");
            entity.Property(row => row.VersionId).HasColumnName("version_id");
            entity.Property(row => row.Actor).HasColumnName("actor").HasMaxLength(256).IsRequired();
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.HasOne<SecretRow>().WithMany().HasForeignKey(row => new { row.SecretId, row.EnvironmentRecordId })
                .HasPrincipalKey(row => new { row.Id, row.EnvironmentRecordId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SecretVersionRow>().WithMany().HasForeignKey(row => new { row.SecretId, row.VersionId })
                .HasPrincipalKey(row => new { row.SecretId, row.Id }).OnDelete(DeleteBehavior.Restrict);
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
            entity.Property(row => row.Name).HasColumnName("name").HasMaxLength(64);
            entity.HasIndex(row => new { row.EnvironmentRecordId, row.Name }).IsUnique();
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
            entity.Property(row => row.ConfirmedPreviousRevision).HasColumnName("confirmed_previous_revision");
            entity.Property(row => row.ConfirmationActor).HasColumnName("confirmation_actor").HasMaxLength(256);
            entity.Property(row => row.ConfirmedAt).HasColumnName("confirmed_at");
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
            entity.ToTable("application_deployments", table => table.HasCheckConstraint("CK_application_deployments_desired_generation", "desired_generation = 1 AND (applied_generation IS NULL OR applied_generation = 1)"));
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
            entity.ToTable("application_deployment_generations", table => table.HasCheckConstraint("CK_deployment_generations_first", "generation = 1 AND presence = 'Present'"));
            entity.HasKey(row => new { row.DeploymentId, row.Generation });
            entity.Property(row => row.DeploymentId).HasColumnName("deployment_id");
            entity.Property(row => row.Generation).HasColumnName("generation");
            entity.Property(row => row.Presence).HasColumnName("presence").HasMaxLength(16).IsRequired();
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
        modelBuilder.Entity<DurableCreateOperationRow>(entity =>
        {
            entity.ToTable("durable_create_operations", table =>
            {
                table.HasCheckConstraint("CK_durable_create_operations_action", "action = 'Create'");
                table.HasCheckConstraint("CK_durable_create_operations_state",
                    "state IN ('Pending', 'Preparing', 'Dispatching', 'Succeeded', 'Failed', 'NeedsAttention')");
                table.HasCheckConstraint("CK_durable_create_operations_resource_id", "length(resource_id) > 0");
                table.HasCheckConstraint("CK_durable_create_operations_attempt",
                    "attempt_number >= 0 AND (attempt_number = 0) = (current_attempt_id IS NULL)");
                table.HasCheckConstraint("CK_durable_create_operations_dispatch",
                    "(state = 'Dispatching') = (dispatched_at IS NOT NULL AND finalized_at IS NULL)");
                table.HasCheckConstraint("CK_durable_create_operations_final",
                    "(state IN ('Succeeded', 'Failed', 'NeedsAttention')) = (finalized_at IS NOT NULL)");
                table.HasCheckConstraint("CK_durable_create_operations_reason",
                    "reason_code IS NULL OR reason_code IN ('ProviderInputUnavailable', 'SecretUnavailable', " +
                    "'UnsupportedShape', 'DatabaseNameInvalid', 'Connectivity', 'Conflict', 'ServerRejected', " +
                    "'UncertainAfterDispatch')");
                table.HasCheckConstraint("CK_durable_create_operations_needs_attention",
                    "reason_code IS DISTINCT FROM 'UncertainAfterDispatch' OR state = 'NeedsAttention'");
            });
            entity.HasKey(row => new { row.DeploymentId, row.Generation, row.ResourceId, row.Action });
            entity.Property(row => row.DeploymentId).HasColumnName("deployment_id");
            entity.Property(row => row.Generation).HasColumnName("generation");
            entity.Property(row => row.ResourceId).HasColumnName("resource_id").HasMaxLength(128).IsRequired();
            entity.Property(row => row.Action).HasColumnName("action").HasMaxLength(16).IsRequired();
            entity.Property(row => row.State).HasColumnName("state").HasMaxLength(32).IsRequired();
            entity.Property(row => row.ReasonCode).HasColumnName("reason_code").HasMaxLength(32);
            entity.Property(row => row.CurrentAttemptId).HasColumnName("current_attempt_id");
            entity.Property(row => row.AttemptNumber).HasColumnName("attempt_number");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.UpdatedAt).HasColumnName("updated_at");
            entity.Property(row => row.DispatchedAt).HasColumnName("dispatched_at");
            entity.Property(row => row.FinalizedAt).HasColumnName("finalized_at");
            entity.HasIndex(row => new { row.DeploymentId, row.Generation, row.ResourceId, row.Action, row.State })
                .IsUnique();
            entity.HasIndex(row => new
            {
                row.DeploymentId,
                row.Generation,
                row.ResourceId,
                row.Action,
                row.CurrentAttemptId,
                row.State
            }).IsUnique();
            entity.HasOne<DeploymentGenerationRow>().WithMany()
                .HasForeignKey(row => new { row.DeploymentId, row.Generation })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DurableCreateOperationAttemptRow>(entity =>
        {
            entity.ToTable("durable_create_operation_attempts", table =>
            {
                table.HasCheckConstraint("CK_durable_create_operation_attempts_action", "action = 'Create'");
                table.HasCheckConstraint("CK_durable_create_operation_attempts_number", "attempt_number > 0");
                table.HasCheckConstraint("CK_durable_create_operation_attempts_outcome",
                    "outcome IS NULL OR outcome IN ('Succeeded', 'Conflict', 'ServerRejected', 'Uncertain', " +
                    "'CanceledBeforeDispatch', 'AbandonedBeforeDispatch', 'FailedBeforeDispatch')");
                table.HasCheckConstraint("CK_durable_create_operation_attempts_close",
                    "(outcome IS NULL) = (finished_at IS NULL)");
                table.HasCheckConstraint("CK_durable_create_operation_attempts_uncertain_dispatch",
                    "outcome IS DISTINCT FROM 'Uncertain' OR dispatched_at IS NOT NULL");
            });
            entity.HasKey(row => row.AttemptId);
            entity.Property(row => row.AttemptId).HasColumnName("attempt_id");
            entity.Property(row => row.DeploymentId).HasColumnName("deployment_id");
            entity.Property(row => row.Generation).HasColumnName("generation");
            entity.Property(row => row.ResourceId).HasColumnName("resource_id").HasMaxLength(128).IsRequired();
            entity.Property(row => row.Action).HasColumnName("action").HasMaxLength(16).IsRequired();
            entity.Property(row => row.AttemptNumber).HasColumnName("attempt_number");
            entity.Property(row => row.StartedAt).HasColumnName("started_at");
            entity.Property(row => row.DispatchedAt).HasColumnName("dispatched_at");
            entity.Property(row => row.FinishedAt).HasColumnName("finished_at");
            entity.Property(row => row.Outcome).HasColumnName("outcome").HasMaxLength(32);
            entity.HasIndex(row => new
            {
                row.DeploymentId,
                row.Generation,
                row.ResourceId,
                row.Action,
                row.AttemptNumber
            }).IsUnique();
            entity.HasIndex(row => new { row.DeploymentId, row.Generation, row.ResourceId, row.Action });
            entity.HasIndex(row => new
            {
                row.AttemptId,
                row.DeploymentId,
                row.Generation,
                row.ResourceId,
                row.Action,
                row.AttemptNumber,
                row.Outcome
            }).IsUnique();
            entity.HasOne<DurableCreateOperationRow>().WithMany()
                .HasForeignKey(row => new { row.DeploymentId, row.Generation, row.ResourceId, row.Action })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<PostgreSqlDatabaseRecoveryDecisionRow>(entity =>
        {
            entity.ToTable("postgresql_database_recovery_decisions", table =>
            {
                table.HasCheckConstraint("CK_postgresql_recovery_acknowledged", "acknowledged = true");
                table.HasCheckConstraint("CK_postgresql_recovery_reason",
                    "reason = 'OperatorAcceptedExistingDatabase'");
                table.HasCheckConstraint("CK_postgresql_recovery_note",
                    "length(btrim(note)) > 0 AND length(note) <= 512");
                table.HasCheckConstraint("CK_postgresql_recovery_actor",
                    "length(btrim(technical_actor)) > 0 AND length(technical_actor) <= 256");
                table.HasCheckConstraint("CK_postgresql_recovery_committed_state",
                    "operation_state = 'Succeeded' AND applied_generation = generation");
                table.HasCheckConstraint("CK_postgresql_recovery_uncertain_attempt",
                    "attempt_outcome = 'Uncertain'");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.DeploymentId).HasColumnName("deployment_id");
            entity.Property(row => row.Generation).HasColumnName("generation");
            entity.Property(row => row.ResourceId).HasColumnName("resource_id").HasMaxLength(128).IsRequired();
            entity.Property(row => row.Action).HasColumnName("action").HasMaxLength(16).IsRequired();
            entity.Property(row => row.AttemptId).HasColumnName("attempt_id");
            entity.Property(row => row.AttemptNumber).HasColumnName("attempt_number");
            entity.Property(row => row.AttemptOutcome).HasColumnName("attempt_outcome")
                .HasMaxLength(32).HasDefaultValue("Uncertain").IsRequired();
            entity.Property(row => row.Reason).HasColumnName("reason").HasMaxLength(48).IsRequired();
            entity.Property(row => row.Note).HasColumnName("note").HasMaxLength(512).IsRequired();
            entity.Property(row => row.TechnicalActor).HasColumnName("technical_actor").HasMaxLength(256).IsRequired();
            entity.Property(row => row.Acknowledged).HasColumnName("acknowledged");
            entity.Property(row => row.DecidedAt).HasColumnName("decided_at");
            entity.Property(row => row.OperationState).HasColumnName("operation_state")
                .HasMaxLength(32).HasDefaultValue("Succeeded").IsRequired();
            entity.Property(row => row.AppliedGeneration).HasColumnName("applied_generation")
                .HasDefaultValue(1L);
            entity.HasIndex(row => new { row.DeploymentId, row.Generation, row.ResourceId, row.Action }).IsUnique();
            entity.HasIndex(row => row.AttemptId).IsUnique();
            entity.HasOne<DurableCreateOperationRow>().WithMany()
                .HasForeignKey(row => new { row.DeploymentId, row.Generation, row.ResourceId, row.Action })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DurableCreateOperationAttemptRow>().WithMany()
                .HasForeignKey(row => row.AttemptId).OnDelete(DeleteBehavior.Restrict);
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
    public required string Presence { get; set; }
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

internal sealed class DurableCreateOperationRow
{
    public Guid DeploymentId { get; set; }
    public long Generation { get; set; }
    public required string ResourceId { get; set; }
    public required string Action { get; set; }
    public required string State { get; set; }
    public string? ReasonCode { get; set; }
    public Guid? CurrentAttemptId { get; set; }
    public long AttemptNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
}

internal sealed class DurableCreateOperationAttemptRow
{
    public Guid AttemptId { get; set; }
    public Guid DeploymentId { get; set; }
    public long Generation { get; set; }
    public required string ResourceId { get; set; }
    public required string Action { get; set; }
    public long AttemptNumber { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Outcome { get; set; }
}

internal sealed class PostgreSqlDatabaseRecoveryDecisionRow
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public long Generation { get; set; }
    public required string ResourceId { get; set; }
    public required string Action { get; set; }
    public Guid AttemptId { get; set; }
    public long AttemptNumber { get; set; }
    public required string AttemptOutcome { get; set; }
    public required string Reason { get; set; }
    public required string Note { get; set; }
    public required string TechnicalActor { get; set; }
    public bool Acknowledged { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
    public required string OperationState { get; set; }
    public long AppliedGeneration { get; set; }
}

internal sealed class EnvironmentRow
{
    public Guid Id { get; set; }
    public required string EnvironmentId { get; set; }
}

internal sealed class SecretRow
{
    public string? Name { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
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
    public int? Number { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
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
    public string? Name { get; set; }
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
    public long? ConfirmedPreviousRevision { get; set; }
    public string? ConfirmationActor { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
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

internal sealed class SecretCreationReceiptRow
{
    public Guid EnvironmentRecordId { get; set; }
    public required string Operation { get; set; }
    public Guid RequestId { get; set; }
    public required string Name { get; set; }
    public Guid SecretId { get; set; }
    public Guid VersionId { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
