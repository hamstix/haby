using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<ProtectedCredentialOwnerRow> ProtectedCredentialOwners => Set<ProtectedCredentialOwnerRow>();

    private static void ConfigureProtectedCredentialOwners(ModelBuilder model)
    {
        model.Entity<ProtectedCredentialOwnerRow>(e =>
        {
            e.ToTable("protected_application_credential_owners");
            e.HasKey(x => new { x.Environment, x.Application, x.Resource });
            e.Property(x => x.Environment).HasColumnName("environment_record_id");
            e.Property(x => x.Application).HasColumnName("application_record_id");
            e.Property(x => x.Resource).HasColumnName("resource_local_id").HasMaxLength(63);
            e.Property(x => x.Stage).HasColumnName("staged_credential_id");
            e.Property(x => x.Request).HasColumnName("request_id");
            e.HasIndex(x => x.Stage).IsUnique();
            e.HasOne<ProtectedApplicationCredentialRow>().WithMany()
                .HasForeignKey(x => new { x.Stage, x.Request, x.Environment, x.Application, x.Resource })
                .HasPrincipalKey(x => new { x.Id, x.Request, x.Environment, x.Application, x.Resource })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class ProtectedCredentialOwnerRow
{
    public Guid Environment { get; set; }
    public Guid Application { get; set; }
    public string Resource { get; set; } = "";
    public Guid Stage { get; set; }
    public Guid Request { get; set; }
}
