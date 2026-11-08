using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<OperatorAccessRow> OperatorAccess => Set<OperatorAccessRow>();
    private static void ConfigureOperatorAccess(ModelBuilder builder)
    {
        builder.Entity<OperatorAccessRow>(e =>
        {
            e.ToTable("operator_access_grants", t =>
            {
                t.HasCheckConstraint("ck_operator_access_hash", "octet_length(credential_hash) = 32");
                t.HasCheckConstraint("ck_operator_access_purpose", "purpose = 'operator-admin/v1'");
                t.HasCheckConstraint("ck_operator_access_expiry", "expires_at IS NULL OR expires_at > created_at");
                t.HasCheckConstraint("ck_operator_access_revoke", "(revoked_at IS NULL) = (revoked_by IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Purpose).HasColumnName("purpose").HasMaxLength(64);
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(128);
            e.Property(x => x.CredentialHash).HasColumnName("credential_hash");
            e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(256);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RevokedBy).HasColumnName("revoked_by").HasMaxLength(256);
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        });
    }
}

internal sealed class OperatorAccessRow
{
    public Guid Id { get; set; }
    public required string Purpose { get; set; }
    public string? DisplayName { get; set; }
    public required byte[] CredentialHash { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? RevokedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public override string ToString() => "[Operator access row]";
}
