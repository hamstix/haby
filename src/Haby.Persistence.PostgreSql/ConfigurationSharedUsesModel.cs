using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

// Immutable metadata only. One created document/projection retains its own original association and exact use.
internal sealed class ConfigurationSharedUseRow
{
    public Guid Document { get; set; }
    public required string ProjectionHash { get; set; }
    public required string Projection { get; set; }
    public Guid Environment { get; set; }
    public Guid Application { get; set; }
    public required string Resource { get; set; }
    public Guid Association { get; set; }
    public Guid Provider { get; set; }
    public long Revision { get; set; }
    public required string Slot { get; set; }
    public Guid Secret { get; set; }
    public Guid Version { get; set; }
    public string Use { get; set; } = "ApplicationDelivery";
    public required string Producer { get; set; }
    public Guid Attempt { get; set; }
    public required string Formatter { get; set; }
    public required string Implementation { get; set; }
}

public sealed partial class ManifestCatalogDbContext
{
    internal DbSet<ConfigurationSharedUseRow> ConfigurationSharedUses => Set<ConfigurationSharedUseRow>();
    private readonly HashSet<(Guid Transaction, Guid Environment, Guid Provider)> _documentUseGates = [];
    private readonly Dictionary<ConfigurationSharedUseRow, (Guid Transaction, ConfigurationSharedUseIdentity Value)> _documentUsePermits = [];

    private static void ConfigureConfigurationSharedUses(ModelBuilder model)
    {
        model.Entity<ConfigurationSharedUseRow>(e =>
        {
            e.ToTable("configuration_shared_credential_uses", t =>
                t.HasCheckConstraint("ck_configuration_shared_use", "use = 'ApplicationDelivery' AND revision_number > 0"));
            e.HasKey(x => new { x.Document, x.ProjectionHash });
            e.Property(x => x.Document).HasColumnName("document_revision_id");
            e.Property(x => x.ProjectionHash).HasColumnName("projection_hash").HasMaxLength(64);
            e.Property(x => x.Projection).HasColumnName("projection").HasColumnType("text");
            e.Property(x => x.Environment).HasColumnName("environment_record_id");
            e.Property(x => x.Application).HasColumnName("application_record_id");
            e.Property(x => x.Resource).HasColumnName("resource_local_id").HasMaxLength(63);
            e.Property(x => x.Association).HasColumnName("association_id");
            e.Property(x => x.Provider).HasColumnName("provider_instance_id");
            e.Property(x => x.Revision).HasColumnName("revision_number");
            e.Property(x => x.Slot).HasColumnName("slot").HasMaxLength(128);
            e.Property(x => x.Secret).HasColumnName("secret_id");
            e.Property(x => x.Version).HasColumnName("secret_version_id");
            e.Property(x => x.Use).HasColumnName("use").HasMaxLength(64);
            e.Property(x => x.Producer).HasColumnName("producer").HasMaxLength(128);
            e.Property(x => x.Attempt).HasColumnName("attempt_id");
            e.Property(x => x.Formatter).HasColumnName("formatter").HasMaxLength(256);
            e.Property(x => x.Implementation).HasColumnName("formatter_implementation").HasMaxLength(128);
            e.HasOne<ConfigurationDocumentRow>().WithMany().HasForeignKey(x => x.Document).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ResourceManagementRow>().WithMany()
                .HasForeignKey(x => new { x.Association, x.Environment, x.Application, x.Resource })
                .HasPrincipalKey(x => new { x.Id, x.Environment, x.Application, x.Resource }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SharedCredentialBindingRow>().WithMany()
                .HasForeignKey(x => new { x.Provider, x.Revision, x.Slot, x.Environment, x.Secret, x.Version, x.Use })
                .HasPrincipalKey(x => new { x.ProviderInstanceId, x.RevisionNumber, x.Slot, x.EnvironmentRecordId, x.SecretId, x.SecretVersionId, x.Use })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DurableOperationAttemptRow>().WithMany().HasForeignKey(x => x.Attempt).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.Provider, x.Revision, x.Slot });
        });
    }

    // Provider-row SHARE locks serialize new provenance with C's exact per-use revocation.
    internal async Task<bool> AcquireDocumentUseGatesAsync(Guid environment, IEnumerable<Guid> providers, CancellationToken token)
    {
        var transaction = Database.CurrentTransaction ?? throw new InvalidOperationException("Document provenance requires a transaction.");
        foreach (var provider in providers.Distinct().Order())
        {
            if (await ProviderInstances.FromSqlInterpolated($"SELECT * FROM provider_instances WHERE id = {provider} AND environment_record_id = {environment} FOR SHARE")
                .SingleOrDefaultAsync(token) is null) return false;
            _documentUseGates.Add((transaction.TransactionId, environment, provider));
        }
        return true;
    }
    internal void AuthorizeDocumentSharedUse(ConfigurationSharedUseRow row)
    {
        var transaction = Database.CurrentTransaction ?? throw new InvalidOperationException("Document provenance requires a transaction.");
        if (!_documentUseGates.Contains((transaction.TransactionId, row.Environment, row.Provider)))
            throw new InvalidOperationException("Document provenance has no owning Provider gate.");
        _documentUsePermits.Add(row, (transaction.TransactionId, ConfigurationSharedUseIdentity.From(row)));
    }
    private async Task ValidateDocumentSharedUsesAsync(bool asynchronous, CancellationToken token)
    {
        if (ChangeTracker.Entries<ConfigurationSharedUseRow>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Document shared-use provenance is immutable.");
        foreach (var entry in ChangeTracker.Entries<ConfigurationSharedUseRow>().Where(x => x.State == EntityState.Added))
        {
            var row = entry.Entity;
            if (Database.CurrentTransaction is not { } transaction || !_documentUsePermits.TryGetValue(row, out var permit) ||
                permit.Transaction != transaction.TransactionId || permit.Value != ConfigurationSharedUseIdentity.From(row) ||
                !_documentUseGates.Contains((transaction.TransactionId, row.Environment, row.Provider)) || row.Use != "ApplicationDelivery" ||
                row.ProjectionHash != ResourcesReadyCheckpointCodec.Hash(row.Projection))
                throw new InvalidOperationException("Document shared-use provenance has no exact owning acceptance.");
            var document = ChangeTracker.Entries<ConfigurationDocumentRow>().SingleOrDefault(x => x.State == EntityState.Added && x.Entity.Id == row.Document)?.Entity;
            if (document is null) throw new InvalidOperationException("Shared-use provenance requires the new exact document.");
            var sourceQuery = DeploymentGenerations.AsNoTracking().Where(x => x.DeploymentId == document.DeploymentId && x.Generation == document.Generation);
            var source = asynchronous ? await sourceQuery.SingleOrDefaultAsync(token) : sourceQuery.SingleOrDefault();
            if (source is null || source.EnvironmentRecordId != row.Environment || source.ApplicationRecordId != row.Application)
                throw new InvalidOperationException("Document shared-use owner differs from its source.");
        }
    }
    internal sealed record ConfigurationSharedUseIdentity(Guid Document, string ProjectionHash, string Projection, Guid Environment, Guid Application,
        string Resource, Guid Association, Guid Provider, long Revision, string Slot, Guid Secret, Guid Version, string Use,
        string Producer, Guid Attempt, string Formatter, string Implementation)
    {
        internal static ConfigurationSharedUseIdentity From(ConfigurationSharedUseRow r) => new(r.Document, r.ProjectionHash, r.Projection,
            r.Environment, r.Application, r.Resource, r.Association, r.Provider, r.Revision, r.Slot, r.Secret, r.Version, r.Use,
            r.Producer, r.Attempt, r.Formatter, r.Implementation);
    }
}
