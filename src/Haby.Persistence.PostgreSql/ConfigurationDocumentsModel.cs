using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<ConfigurationDocumentRow> ConfigurationDocuments => Set<ConfigurationDocumentRow>();
    private static void ConfigureConfigurationDocuments(ModelBuilder builder)
    {
        builder.Entity<ConfigurationDocumentRow>(e =>
        {
            e.ToTable("configuration_document_revisions", t =>
            {
                t.HasCheckConstraint("ck_configuration_document_payload_size", "octet_length(payload) <= 4194304");
                t.HasCheckConstraint("ck_configuration_document_generation", "generation > 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            e.Property(x => x.Generation).HasColumnName("generation");
            e.Property(x => x.Component).HasColumnName("component").HasMaxLength(128);
            e.Property(x => x.Document).HasColumnName("document").HasMaxLength(128);
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("text");
            e.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(128);
            e.HasIndex(x => new { x.DeploymentId, x.Generation, x.Component, x.Document }).IsUnique();
            e.HasOne<DeploymentGenerationRow>().WithMany().HasForeignKey(x => new { x.DeploymentId, x.Generation }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class ConfigurationDocumentRow
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public long Generation { get; set; }
    public required string Component { get; set; }
    public required string Document { get; set; }
    public required string Payload { get; set; }
    public required string Fingerprint { get; set; }
}
