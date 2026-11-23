using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<ProtectedApplicationCredentialRow> ProtectedApplicationCredentials => Set<ProtectedApplicationCredentialRow>();
    internal DbSet<ResourceObservationReceiptRow> ResourceObservationReceipts => Set<ResourceObservationReceiptRow>();

    private static void ConfigureApplicationCredentials(ModelBuilder model)
    {
        model.Entity<ResourceObservationReceiptRow>(e =>
        {
            e.ToTable("resource_import_receipts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Deployment).HasColumnName("deployment_id");
            e.Property(x => x.Generation).HasColumnName("generation");
            e.Property(x => x.Group).HasColumnName("group_id").HasMaxLength(128);
            e.Property(x => x.Fingerprint).HasColumnName("input_fingerprint").HasMaxLength(128);
            e.Property(x => x.Document).HasColumnName("document").HasColumnType("text");
            e.HasIndex(x => new { x.Deployment, x.Generation, x.Group }).IsUnique();
            e.HasOne<DeploymentGenerationRow>().WithMany().HasForeignKey(x => new { DeploymentId = x.Deployment, Generation = x.Generation })
                .OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ProtectedApplicationCredentialRow>(e =>
        {
            e.ToTable("protected_application_credentials", t =>
                t.HasCheckConstraint("CK_protected_application_credentials_material", "octet_length(material) BETWEEN 1 AND 65536"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Request).HasColumnName("request_id");
            e.Property(x => x.Environment).HasColumnName("environment_record_id");
            e.Property(x => x.Application).HasColumnName("application_record_id");
            e.Property(x => x.Resource).HasColumnName("resource_local_id").HasMaxLength(63);
            e.Property(x => x.Secret).HasColumnName("secret_id");
            e.Property(x => x.Version).HasColumnName("version_id");
            e.Property(x => x.Material).HasColumnName("material");
            e.Property(x => x.ConfirmedReceipt).HasColumnName("confirmed_receipt_id");
            e.HasIndex(x => x.Request).IsUnique();
            e.HasIndex(x => x.Secret).IsUnique();
            e.HasIndex(x => x.Version).IsUnique();
            e.HasOne<EnvironmentRow>().WithMany().HasForeignKey(x => x.Environment).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationRow>().WithMany().HasForeignKey(x => x.Application).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ResourceObservationReceiptRow>().WithMany().HasForeignKey(x => x.ConfirmedReceipt).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class ProtectedApplicationCredentialRow
{
    public Guid Id { get; set; }
    public Guid Request { get; set; }
    public Guid Environment { get; set; }
    public Guid Application { get; set; }
    public string Resource { get; set; } = "";
    public Guid Secret { get; set; }
    public Guid Version { get; set; }
    public byte[] Material { get; set; } = [];
    public Guid? ConfirmedReceipt { get; set; }
}

internal sealed class ResourceObservationReceiptRow
{
    public Guid Id { get; set; }
    public Guid Deployment { get; set; }
    public long Generation { get; set; }
    public string Group { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Document { get; set; } = "";
}
