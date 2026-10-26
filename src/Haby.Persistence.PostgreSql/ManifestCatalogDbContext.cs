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
            entity.HasIndex(row => new { row.ApplicationRecordId, row.LocalRevisionNumber }).IsUnique();
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
            entity.Property(row => row.Host).HasColumnName("host").HasMaxLength(255).IsRequired();
            entity.Property(row => row.Port).HasColumnName("port");
            entity.HasOne<EnvironmentRow>().WithMany().HasForeignKey(row => row.EnvironmentRecordId).OnDelete(DeleteBehavior.Restrict);
            entity.HasAlternateKey(row => new { row.Id, row.EnvironmentRecordId, row.Host, row.Port });
        });
        modelBuilder.Entity<ProviderConfigurationRevisionRow>(entity =>
        {
            entity.ToTable("provider_configuration_revisions");
            entity.HasKey(row => new { row.ProviderInstanceId, row.RevisionNumber });
            entity.Property(row => row.ProviderInstanceId).HasColumnName("provider_instance_id");
            entity.Property(row => row.RevisionNumber).HasColumnName("revision_number");
            entity.Property(row => row.EnvironmentRecordId).HasColumnName("environment_record_id");
            entity.Property(row => row.Host).HasColumnName("host").HasMaxLength(255).IsRequired();
            entity.Property(row => row.Port).HasColumnName("port");
            entity.Property(row => row.ShapeVersion).HasColumnName("shape_version");
            entity.Property(row => row.MaintenanceDatabase).HasColumnName("maintenance_database").HasMaxLength(63).IsRequired();
            entity.Property(row => row.AdministratorUserName).HasColumnName("administrator_user_name").HasMaxLength(63).IsRequired();
            entity.Property(row => row.AdministratorSecretId).HasColumnName("administrator_secret_id");
            entity.Property(row => row.AdministratorSecretVersionId).HasColumnName("administrator_secret_version_id");
            entity.Property(row => row.ImplementationId).HasColumnName("implementation_id").HasMaxLength(128).IsRequired();
            entity.HasOne<ProviderInstanceRow>().WithMany()
                .HasForeignKey(row => new { row.ProviderInstanceId, row.EnvironmentRecordId, row.Host, row.Port })
                .HasPrincipalKey(row => new { row.Id, row.EnvironmentRecordId, row.Host, row.Port })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SecretRow>().WithMany()
                .HasForeignKey(row => new { row.AdministratorSecretId, row.EnvironmentRecordId })
                .HasPrincipalKey(row => new { row.Id, row.EnvironmentRecordId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SecretVersionRow>().WithMany()
                .HasForeignKey(row => new { row.AdministratorSecretId, row.AdministratorSecretVersionId })
                .HasPrincipalKey(row => new { row.SecretId, row.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
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
    public required string Host { get; set; }
    public int Port { get; set; }
}

internal sealed class ProviderConfigurationRevisionRow
{
    public Guid ProviderInstanceId { get; set; }
    public long RevisionNumber { get; set; }
    public Guid EnvironmentRecordId { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; }
    public int ShapeVersion { get; set; }
    public required string MaintenanceDatabase { get; set; }
    public required string AdministratorUserName { get; set; }
    public Guid AdministratorSecretId { get; set; }
    public Guid AdministratorSecretVersionId { get; set; }
    public required string ImplementationId { get; set; }
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
