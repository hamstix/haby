using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<InstallationIdentityRow> InstallationIdentities => Set<InstallationIdentityRow>();
    internal DbSet<ManagedWorkloadRow> ManagedWorkloads => Set<ManagedWorkloadRow>();
    internal DbSet<OwnedObjectRow> OwnedObjects => Set<OwnedObjectRow>();
    internal DbSet<RuntimeIdentityBindingRow> RuntimeIdentityBindings => Set<RuntimeIdentityBindingRow>();
    private static void ConfigureRuntimeIdentities(ModelBuilder model)
    {
        model.Entity<InstallationIdentityRow>(e =>
        {
            e.ToTable("installation_identity", t => t.HasCheckConstraint("CK_installation_singleton", "singleton = 1 AND id <> '00000000-0000-0000-0000-000000000000'::uuid"));
            e.HasKey(x => x.Singleton);
            e.Property(x => x.Singleton).HasColumnName("singleton").ValueGeneratedNever();
            e.Property(x => x.Id).HasColumnName("id");
            e.HasAlternateKey(x => x.Id);
        });
        model.Entity<ManagedWorkloadRow>(e =>
        {
            e.ToTable("managed_workloads");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            e.Property(x => x.EnvironmentId).HasColumnName("environment_id");
            e.Property(x => x.ApplicationId).HasColumnName("application_id");
            e.Property(x => x.Component).HasColumnName("component").HasMaxLength(63);
            e.Property(x => x.Workload).HasColumnName("workload").HasMaxLength(63);
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.TargetContract).HasColumnName("target_contract").HasMaxLength(128);
            e.Property(x => x.TargetFormat).HasColumnName("target_format");
            e.Property(x => x.TargetPayload).HasColumnName("target_payload").HasMaxLength(16384);
            e.HasIndex(x => new { x.DeploymentId, x.Component, x.Workload }).IsUnique();
            e.HasOne<ApplicationDeploymentRow>().WithMany().HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<EnvironmentRow>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationRow>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProviderInstanceRow>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<OwnedObjectRow>(e =>
        {
            e.ToTable("owned_objects", t => t.HasCheckConstraint("CK_owned_object_payload", "format > 0 AND octet_length(payload) BETWEEN 1 AND 1048576"));
            e.HasKey(x => new { x.ManagedWorkloadId, x.Role });
            e.Property(x => x.ManagedWorkloadId).HasColumnName("managed_workload_id");
            e.Property(x => x.Role).HasColumnName("role").HasMaxLength(128);
            e.Property(x => x.Contract).HasColumnName("contract").HasMaxLength(128);
            e.Property(x => x.Format).HasColumnName("format");
            e.Property(x => x.Payload).HasColumnName("payload");
            e.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            e.Property(x => x.Generation).HasColumnName("generation");
            e.Property(x => x.OperationId).HasColumnName("operation_id").HasMaxLength(128);
            e.HasOne<ManagedWorkloadRow>().WithMany().HasForeignKey(x => x.ManagedWorkloadId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DurableActionIntentRow>().WithMany().HasForeignKey(x => new { x.DeploymentId, x.Generation, x.OperationId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<RuntimeIdentityBindingRow>(e =>
        {
            e.ToTable("runtime_identity_bindings", t => t.HasCheckConstraint("CK_runtime_binding", "format > 0 AND status IN ('Prepared', 'Revoked') AND concurrency_token <> '00000000-0000-0000-0000-000000000000'::uuid AND octet_length(validation_payload) BETWEEN 1 AND 1048576 AND ((status = 'Prepared' AND revoked_at IS NULL AND revoked_by IS NULL) OR (status = 'Revoked' AND revoked_at IS NOT NULL AND revoked_by IS NOT NULL))"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.InstallationId).HasColumnName("installation_id");
            e.Property(x => x.EnvironmentId).HasColumnName("environment_id");
            e.Property(x => x.ManagedWorkloadId).HasColumnName("managed_workload_id");
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.ConfigurationRevision).HasColumnName("configuration_revision");
            e.Property(x => x.Authenticator).HasColumnName("authenticator").HasMaxLength(128);
            e.Property(x => x.Audience).HasColumnName("audience").HasMaxLength(256);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(32);
            e.Property(x => x.ConcurrencyToken).HasColumnName("concurrency_token").IsConcurrencyToken();
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.RevokedBy).HasColumnName("revoked_by").HasMaxLength(256);
            e.Property(x => x.Contract).HasColumnName("contract").HasMaxLength(128);
            e.Property(x => x.Format).HasColumnName("format");
            e.Property(x => x.ValidationPayload).HasColumnName("validation_payload");
            e.Property(x => x.Role).HasColumnName("role").HasMaxLength(128);
            e.HasIndex(x => x.ManagedWorkloadId).IsUnique();
            e.HasOne<OwnedObjectRow>().WithOne().HasForeignKey<RuntimeIdentityBindingRow>(x => new { x.ManagedWorkloadId, x.Role }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<InstallationIdentityRow>().WithMany().HasForeignKey(x => x.InstallationId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<EnvironmentRow>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProviderConfigurationRevisionRow>().WithMany().HasForeignKey(x => new { x.ProviderId, x.ConfigurationRevision }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class InstallationIdentityRow { public int Singleton { get; set; } public Guid Id { get; set; } }
internal sealed class ManagedWorkloadRow
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid ApplicationId { get; set; }
    public required string Component { get; set; }
    public required string Workload { get; set; }
    public Guid ProviderId { get; set; }
    public required string TargetContract { get; set; }
    public int TargetFormat { get; set; }
    public required string TargetPayload { get; set; }
}
internal sealed class OwnedObjectRow
{
    public Guid ManagedWorkloadId { get; set; }
    public required string Role { get; set; }
    public required string Contract { get; set; }
    public int Format { get; set; }
    public required string Payload { get; set; }
    public Guid DeploymentId { get; set; }
    public long Generation { get; set; }
    public required string OperationId { get; set; }
}
internal sealed class RuntimeIdentityBindingRow
{
    public Guid Id { get; set; }
    public Guid InstallationId { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid ManagedWorkloadId { get; set; }
    public Guid ProviderId { get; set; }
    public long ConfigurationRevision { get; set; }
    public required string Authenticator { get; set; }
    public required string Audience { get; set; }
    public required string Status { get; set; }
    public Guid ConcurrencyToken { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public required string Role { get; set; }
    public required string Contract { get; set; }
    public int Format { get; set; }
    public required string ValidationPayload { get; set; }
}
