using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<ImportSupersessionRow> ImportSupersessions => Set<ImportSupersessionRow>();

    private static void ConfigureImportSupersessions(ModelBuilder model)
    {
        model.Entity<ImportSupersessionRow>(e =>
        {
            e.ToTable("resource_import_supersessions");
            e.HasKey(x => x.PreviousRequest);
            e.Property(x => x.PreviousRequest).HasColumnName("previous_request_id");
            e.Property(x => x.SuccessorRequest).HasColumnName("successor_request_id");
            e.Property(x => x.PreviousStage).HasColumnName("previous_staged_credential_id");
            e.Property(x => x.SuccessorStage).HasColumnName("successor_staged_credential_id");
            e.Property(x => x.ChangeSet).HasColumnName("change_set_id");
            e.Property(x => x.Deployment).HasColumnName("deployment_id");
            e.Property(x => x.Generation).HasColumnName("generation");
            e.Property(x => x.Actor).HasColumnName("actor_id").HasMaxLength(256);
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(32);
            e.Property(x => x.AcceptedAt).HasColumnName("accepted_at");
            e.HasIndex(x => x.SuccessorRequest).IsUnique();
            e.HasIndex(x => x.ChangeSet);
            e.HasOne<OperatorImportRequestRow>().WithMany().HasForeignKey(x => x.PreviousRequest).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OperatorImportRequestRow>().WithMany().HasForeignKey(x => x.SuccessorRequest).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProtectedApplicationCredentialRow>().WithMany().HasForeignKey(x => x.PreviousStage).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProtectedApplicationCredentialRow>().WithMany().HasForeignKey(x => x.SuccessorStage).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ChangeSetCommitRow>().WithMany().HasForeignKey(x => x.ChangeSet).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DeploymentGenerationRow>().WithMany().HasForeignKey(x => new { x.Deployment, x.Generation }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class ImportSupersessionRow
{
    public Guid PreviousRequest { get; set; }
    public Guid SuccessorRequest { get; set; }
    public Guid? PreviousStage { get; set; }
    public Guid SuccessorStage { get; set; }
    public Guid ChangeSet { get; set; }
    public Guid Deployment { get; set; }
    public long Generation { get; set; }
    public string Actor { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset AcceptedAt { get; set; }
}
