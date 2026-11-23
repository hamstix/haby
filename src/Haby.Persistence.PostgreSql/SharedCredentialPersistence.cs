using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed class SharedCredentialBindingRow
{
    public Guid ProviderInstanceId { get; set; }
    public long RevisionNumber { get; set; }
    public required string Slot { get; set; }
    public Guid EnvironmentRecordId { get; set; }
    public Guid SecretId { get; set; }
    public Guid SecretVersionId { get; set; }
    public required string Use { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
internal sealed class SharedCredentialRevocationRow
{
    public Guid ProviderInstanceId { get; set; }
    public long RevisionNumber { get; set; }
    public required string Slot { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
internal static class SharedCredentialPersistence
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SharedCredentialBindingRow>(e =>
        {
            e.ToTable("shared_credential_bindings", t =>
            {
                t.HasCheckConstraint("ck_shared_credential_use", "use IN ('SystemAccess', 'ApplicationDelivery')");
                t.HasCheckConstraint("ck_shared_credential_actor", "length(actor) BETWEEN 1 AND 256");
            });
            e.HasKey(x => new { x.ProviderInstanceId, x.RevisionNumber, x.Slot });
            e.Property(x => x.ProviderInstanceId).HasColumnName("provider_instance_id");
            e.Property(x => x.RevisionNumber).HasColumnName("revision_number");
            e.Property(x => x.Slot).HasColumnName("slot").HasMaxLength(128);
            e.Property(x => x.EnvironmentRecordId).HasColumnName("environment_record_id");
            e.Property(x => x.SecretId).HasColumnName("secret_id");
            e.Property(x => x.SecretVersionId).HasColumnName("secret_version_id");
            e.Property(x => x.Use).HasColumnName("use").HasMaxLength(64);
            e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(256);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<ProviderRevisionSecretRefRow>().WithMany().HasForeignKey(x => new { x.ProviderInstanceId, x.RevisionNumber, x.Slot })
                .OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<SharedCredentialRevocationRow>(e =>
        {
            e.ToTable("shared_credential_revocations", t => t.HasCheckConstraint("ck_shared_revocation_actor", "length(actor) BETWEEN 1 AND 256"));
            e.HasKey(x => new { x.ProviderInstanceId, x.RevisionNumber, x.Slot });
            e.Property(x => x.ProviderInstanceId).HasColumnName("provider_instance_id");
            e.Property(x => x.RevisionNumber).HasColumnName("revision_number");
            e.Property(x => x.Slot).HasColumnName("slot").HasMaxLength(128);
            e.Property(x => x.Actor).HasColumnName("actor").HasMaxLength(256);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<SharedCredentialBindingRow>().WithMany().HasForeignKey(x => new { x.ProviderInstanceId, x.RevisionNumber, x.Slot })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
    internal static void Issue(ManifestCatalogDbContext db, Guid environment, Guid provider, long revision,
        PreparedProviderConfiguration prepared, string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 256)
            throw new ArgumentException("Shared credential issuance requires a trusted actor.");
        foreach (var reference in prepared.SecretReferences.Where(x => x.SharedUse is not null))
            db.SharedCredentialBindings.Add(new()
            {
                EnvironmentRecordId = environment,
                ProviderInstanceId = provider,
                RevisionNumber = revision,
                Slot = reference.Slot,
                SecretId = Guid.Parse(reference.Reference.SecretId.ToString()),
                SecretVersionId = Guid.Parse(reference.Reference.SecretVersionId.ToString()),
                Use = reference.SharedUse!.Value.ToString(),
                Actor = actor,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }
    internal static async Task<bool> MatchesAsync(ManifestCatalogDbContext db, Guid environment, Guid provider, long revision,
        PreparedProviderConfiguration prepared, CancellationToken token)
    {
        var rows = await db.SharedCredentialBindings.AsNoTracking().Where(x => x.ProviderInstanceId == provider && x.RevisionNumber == revision).ToArrayAsync(token);
        var references = prepared.SecretReferences.Where(x => x.SharedUse is not null).ToArray();
        return rows.Length == references.Length && references.All(r => rows.Any(x => x.EnvironmentRecordId == environment &&
            x.Slot == r.Slot && x.Use == r.SharedUse!.Value.ToString() && x.SecretId.ToString() == r.Reference.SecretId.ToString() &&
            x.SecretVersionId.ToString() == r.Reference.SecretVersionId.ToString()));
    }
}
