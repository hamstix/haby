using System.Text.Json;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ApplicationId = Haby.Domain.ApplicationId;

namespace Haby.Persistence.PostgreSql;

/// <summary>Atomic PostgreSQL import and asynchronous revision inspection.</summary>
public sealed class PostgreSqlManifestImportStore(IDbContextFactory<ManifestCatalogDbContext> contexts)
    : IManifestImportStore, IManifestRevisionQuery
{
    public async ValueTask<ManifestCompareAndCreateResult> CompareAndCreateAsync(
        ValidatedManifestCandidate candidate, ImportAuditMetadata auditMetadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(auditMetadata);
        cancellationToken.ThrowIfCancellationRequested();
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // The transaction-scoped lock serializes every import of this logical Application, including its first.
        // Hash collisions only serialize unrelated Applications; database unique indexes remain the final guard.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({candidate.ApplicationId.Value}, 0))", cancellationToken);
        var existing = await context.Revisions.AsNoTracking().SingleOrDefaultAsync(row =>
            row.ApplicationId == candidate.ApplicationId.Value && row.ApplicationVersion == candidate.ApplicationVersion.Value,
            cancellationToken);
        if (existing is not null)
            return new ManifestCompareAndCreateResult(Rehydrate(existing), false);

        var application = await context.Applications.AsNoTracking().SingleOrDefaultAsync(row =>
            row.ApplicationId == candidate.ApplicationId.Value, cancellationToken);
        if (application is null)
        {
            application = new ApplicationRow { Id = Guid.CreateVersion7(), ApplicationId = candidate.ApplicationId.Value };
            context.Applications.Add(application);
        }
        var last = await context.Revisions.Where(row => row.ApplicationRecordId == application.Id)
            .MaxAsync(row => (long?)row.LocalRevisionNumber, cancellationToken) ?? 0;
        var row = new ManifestRevisionRow
        {
            Id = Guid.CreateVersion7(),
            ApplicationRecordId = application.Id,
            ApplicationId = candidate.ApplicationId.Value,
            ApplicationVersion = candidate.ApplicationVersion.Value,
            LocalRevisionNumber = checked(last + 1),
            InputApiVersion = candidate.Declaration.InputApiVersion,
            NormalizedModelVersion = candidate.Declaration.NormalizedModelVersion,
            SnapshotFormatVersion = NormalizedSnapshotCodec.FormatVersion,
            Snapshot = NormalizedSnapshotCodec.Encode(candidate.Declaration),
            HistoricalWarnings = JsonSerializer.Serialize(candidate.Warnings,
                ManifestCatalogJsonContext.Default.ManifestValidationReport),
            ActorId = auditMetadata.ActorId,
            ImportedAt = auditMetadata.ImportedAt.ToUniversalTime()
        };
        context.Revisions.Add(row);
        await context.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Once commit starts, do not translate late cancellation into a reported cancellation of a committed import.
        await transaction.CommitAsync(CancellationToken.None);
        return new ManifestCompareAndCreateResult(Rehydrate(row), true);
    }

    public async ValueTask<ManifestRevision?> FindAsync(ManifestRevisionRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var recordId = Guid.Parse(reference.ApplicationRecordId.ToString());
        var number = reference.RevisionNumber.Value;
        var row = await context.Revisions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.ApplicationRecordId == recordId && item.LocalRevisionNumber == number, cancellationToken);
        return row is null ? null : Rehydrate(row);
    }

    private static ManifestRevision Rehydrate(ManifestRevisionRow row)
    {
        try
        {
            var warningNode = JsonNode.Parse(row.HistoricalWarnings) as JsonObject
                ?? throw new InvalidDataException("Stored historical warnings are corrupt.");
            if (warningNode["Diagnostics"] is not JsonArray || warningNode["Truncated"] is null)
                throw new InvalidDataException("Stored historical warnings are corrupt.");
            var warnings = JsonSerializer.Deserialize(row.HistoricalWarnings,
                ManifestCatalogJsonContext.Default.ManifestValidationReport)
                ?? throw new InvalidDataException("Stored historical warnings are corrupt.");
            if (warnings.Diagnostics is null || warnings.Diagnostics.Any(diagnostic => diagnostic is null ||
                diagnostic.Code is null || diagnostic.Path is null || diagnostic.Message is null ||
                diagnostic.Parameters is null || diagnostic.RelatedPaths is null))
                throw new InvalidDataException("Stored historical warnings are corrupt.");
            var declaration = NormalizedSnapshotCodec.Decode(row.Snapshot, row.InputApiVersion,
                row.NormalizedModelVersion, row.ApplicationId, row.SnapshotFormatVersion);
            return new ManifestRevision(ManifestRevisionId.Parse(row.Id.ToString()),
                ManifestRevisionRef.Create(ApplicationRecordId.Parse(row.ApplicationRecordId.ToString()),
                    ManifestRevisionNumber.Create(row.LocalRevisionNumber)), ApplicationId.Create(row.ApplicationId),
                ApplicationVersion.Create(row.ApplicationVersion), row.InputApiVersion, row.NormalizedModelVersion,
                declaration, warnings, new ImportAuditMetadata(row.ActorId, row.ImportedAt));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("Stored manifest revision is corrupt.", error);
        }
    }
}

/// <summary>Register the durable catalog without affecting the in-memory checkpoint planner.</summary>
public static class ManifestCatalogServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlManifestCatalog(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContextFactory<ManifestCatalogDbContext>(options => options.UseNpgsql(connectionString));
        services.AddSingleton<PostgreSqlManifestImportStore>();
        services.AddSingleton<IManifestImportStore>(provider => provider.GetRequiredService<PostgreSqlManifestImportStore>());
        services.AddSingleton<IManifestRevisionQuery>(provider => provider.GetRequiredService<PostgreSqlManifestImportStore>());
        services.AddSingleton<ManifestCatalogMigrationInitializer>();
        services.AddSingleton<IEnvironmentQuery, PostgreSqlEnvironmentQuery>();
        return services;
    }
}
