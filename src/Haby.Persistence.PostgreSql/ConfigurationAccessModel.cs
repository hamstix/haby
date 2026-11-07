using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<ConfigurationAccessRow> ConfigurationAccess => Set<ConfigurationAccessRow>();
    private static void ConfigureConfigurationAccess(ModelBuilder builder)
    {
        builder.Entity<ConfigurationAccessRow>(e =>
        {
            e.ToTable("configuration_access_grants", t =>
            {
                t.HasCheckConstraint("ck_configuration_access_hash", "octet_length(credential_hash) = 32");
                t.HasCheckConstraint("ck_configuration_access_expiry", "expires_at IS NULL OR expires_at > created_at");
                t.HasCheckConstraint("ck_configuration_access_revoke", "(revoked_at IS NULL) = (revoked_by IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.RevisionId).HasColumnName("revision_id");
            e.Property(x => x.Scope).HasColumnName("scope").HasColumnType("text");
            e.Property(x => x.CredentialHash).HasColumnName("credential_hash");
            e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(256);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RevokedBy).HasColumnName("revoked_by").HasMaxLength(256);
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.HasOne<ConfigurationDocumentRow>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class ConfigurationAccessRow
{
    public Guid Id { get; set; }
    public Guid RevisionId { get; set; }
    public required string Scope { get; set; }
    public required byte[] CredentialHash { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? RevokedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public override string ToString() => "[Configuration access row]";
}
