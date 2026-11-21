using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<OperatorImportRequestRow> OperatorImportRequestRecords => Set<OperatorImportRequestRow>();

    private static void ConfigureOperatorImportRequests(ModelBuilder model)
    {
        model.Entity<OperatorImportRequestRow>(e =>
        {
            e.ToTable("operator_resource_import_requests", t => t.HasCheckConstraint("CK_operator_import_request_completion",
                "(change_set_id IS NULL AND staged_credential_id IS NULL AND completed_at IS NULL AND completed_by IS NULL) OR " +
                "(change_set_id IS NOT NULL AND staged_credential_id IS NOT NULL AND completed_at IS NOT NULL AND completed_by IS NOT NULL)"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("request_id");
            e.Property(x => x.Environment).HasColumnName("environment_record_id");
            e.Property(x => x.Application).HasColumnName("application_record_id");
            e.Property(x => x.ManifestRevision).HasColumnName("manifest_revision_id");
            e.Property(x => x.Revision).HasColumnName("revision_number");
            e.Property(x => x.CredentialResource).HasColumnName("credential_resource_local_id").HasMaxLength(63);
            e.Property(x => x.Input).HasColumnName("input").HasColumnType("text");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            e.Property(x => x.ChangeSet).HasColumnName("change_set_id");
            e.Property(x => x.StagedCredential).HasColumnName("staged_credential_id");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.CompletedBy).HasColumnName("completed_by").HasMaxLength(256);
            e.HasOne<EnvironmentRow>().WithMany().HasForeignKey(x => x.Environment).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationRow>().WithMany().HasForeignKey(x => x.Application).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ManifestRevisionRow>().WithMany().HasForeignKey(x => x.ManifestRevision).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DurableChangeSetRow>().WithMany().HasForeignKey(x => x.ChangeSet).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProtectedApplicationCredentialRow>().WithMany().HasForeignKey(x => x.StagedCredential).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ChangeSet).IsUnique();
            e.HasIndex(x => x.StagedCredential).IsUnique();
        });
    }
}

internal sealed class OperatorImportRequestRow
{
    public Guid Id { get; set; }
    public Guid Environment { get; set; }
    public Guid Application { get; set; }
    public Guid ManifestRevision { get; set; }
    public long Revision { get; set; }
    public string CredentialResource { get; set; } = "";
    public string Input { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public Guid? ChangeSet { get; set; }
    public Guid? StagedCredential { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
}
