using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<RuntimePublicationReceiptRow> RuntimePublicationReceipts => Set<RuntimePublicationReceiptRow>();
    internal DbSet<RuntimeConfigurationPermissionRow> RuntimeConfigurationPermissions => Set<RuntimeConfigurationPermissionRow>();
    private static void ConfigureRuntimePublication(ModelBuilder model)
    {
        model.Entity<RuntimePublicationReceiptRow>(e =>
        {
            e.ToTable("runtime_publication_receipts", t => t.HasCheckConstraint("CK_runtime_publication_receipt",
                "length(request_key) BETWEEN 1 AND 128 AND octet_length(payload) BETWEEN 1 AND 16384"));
            e.HasKey(x => x.RequestKey);
            e.HasIndex(x => x.Id).IsUnique();
            e.Property(x => x.RequestKey).HasColumnName("request_key").HasMaxLength(128);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.BindingId).HasColumnName("binding_id");
            e.Property(x => x.RevisionId).HasColumnName("revision_id");
            e.Property(x => x.Payload).HasColumnName("payload");
            e.HasOne<RuntimeIdentityBindingRow>().WithMany().HasForeignKey(x => x.BindingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConfigurationDocumentRow>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<RuntimeConfigurationPermissionRow>(e =>
        {
            e.ToTable("runtime_configuration_permissions", t => t.HasCheckConstraint("CK_runtime_permission",
                "status IN ('Published', 'Revoked') AND concurrency_token <> '00000000-0000-0000-0000-000000000000'::uuid AND octet_length(origin_payload) BETWEEN 1 AND 16384"));
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.BindingId, x.RevisionId }).IsUnique();
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.BindingId).HasColumnName("binding_id");
            e.Property(x => x.RevisionId).HasColumnName("revision_id");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(32);
            e.Property(x => x.ConcurrencyToken).HasColumnName("concurrency_token").IsConcurrencyToken();
            e.Property(x => x.OriginPayload).HasColumnName("origin_payload");
            e.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ChangedBy).HasColumnName("changed_by").HasMaxLength(256);
            e.Property(x => x.ChangedAt).HasColumnName("changed_at");
            e.Property(x => x.LastRequest).HasColumnName("last_request").HasMaxLength(128);
            e.HasOne<RuntimeIdentityBindingRow>().WithMany().HasForeignKey(x => x.BindingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConfigurationDocumentRow>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<RuntimePublicationReceiptRow>().WithMany().HasForeignKey(x => x.LastRequest).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class RuntimePublicationReceiptRow
{
    public required string RequestKey { get; set; }
    public Guid Id { get; set; }
    public Guid BindingId { get; set; }
    public Guid? RevisionId { get; set; }
    public required string Payload { get; set; }
}
internal sealed class RuntimeConfigurationPermissionRow
{
    public Guid Id { get; set; }
    public Guid BindingId { get; set; }
    public Guid RevisionId { get; set; }
    public required string Status { get; set; }
    public Guid ConcurrencyToken { get; set; }
    public required string OriginPayload { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string ChangedBy { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public required string LastRequest { get; set; }
}
