using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haby.Persistence.PostgreSql;

/// <summary>Explicitly selected PostgreSQL Plaintext mode. Database and backup readers can recover values.</summary>
public sealed class PostgreSqlPlaintextSecretStore(IDbContextFactory<ManifestCatalogDbContext> contexts) : ISecretStore
{
    public async ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id,
        CancellationToken cancellationToken)
    {
        var result = await new PostgreSqlEnvironmentCatalog(contexts).CreateOrGetAsync(id, cancellationToken);
        return result.Environment.RecordId;
    }

    public async ValueTask CreateAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material,
        CancellationToken cancellationToken)
    {
        Validate(environment, reference, material, cancellationToken);
        var bytes = (byte[])material.Clone();
        var environmentId = Guid.Parse(environment.ToString());
        var secretId = Guid.Parse(reference.SecretId.ToString());
        var versionId = Guid.Parse(reference.SecretVersionId.ToString());
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await context.Environments.AsNoTracking().AnyAsync(row => row.Id == environmentId, cancellationToken))
            throw new InvalidOperationException("Environment is unavailable.");
        var secret = new SecretRow
        {
            Id = secretId,
            EnvironmentRecordId = environmentId,
            Owner = "Environment",
            Purpose = "ProviderAdministrator",
            Sensitivity = "Credential",
            ContentType = "application/octet-stream",
            StorageBackend = "PostgreSqlPlaintext",
            OwnershipPolicy = "Managed"
        };
        context.Secrets.Add(secret);
        context.SecretVersions.Add(new SecretVersionRow
        {
            Id = versionId,
            SecretId = secretId,
            ProtectionFormat = PlaintextProtectionFormat.Name,
            FormatVersion = PlaintextProtectionFormat.Version
        });
        context.SecretPayloads.Add(new SecretPayloadRow { SecretVersionId = versionId, Bytes = bytes });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            secret.CurrentVersionId = versionId;
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new InvalidOperationException("Secret storage write failed.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    public async ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material,
        CancellationToken cancellationToken)
    {
        Validate(environment, reference, material, cancellationToken);
        var bytes = (byte[])material.Clone();
        var environmentId = Guid.Parse(environment.ToString());
        var secretId = Guid.Parse(reference.SecretId.ToString());
        var versionId = Guid.Parse(reference.SecretVersionId.ToString());
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // Lock the parent so concurrent rotations serialize; the version key remains a database uniqueness guard.
        var secret = await context.Secrets.FromSqlInterpolated($"SELECT * FROM secrets WHERE id = {secretId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (secret is null || secret.EnvironmentRecordId != environmentId)
            throw new InvalidOperationException("Secret is unavailable in this Environment.");
        context.SecretVersions.Add(new SecretVersionRow
        {
            Id = versionId,
            SecretId = secretId,
            ProtectionFormat = PlaintextProtectionFormat.Name,
            FormatVersion = PlaintextProtectionFormat.Version
        });
        context.SecretPayloads.Add(new SecretPayloadRow { SecretVersionId = versionId, Bytes = bytes });
        secret.CurrentVersionId = versionId;
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (DbUpdateException) { throw new InvalidOperationException("Secret storage write failed."); }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    public async ValueTask<SecretMaterial?> ResolveAsync(EnvironmentRecordId environment, SecretRef reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var environmentId = Guid.Parse(environment.ToString());
        var secretId = Guid.Parse(reference.SecretId.ToString());
        var versionId = Guid.Parse(reference.SecretVersionId.ToString());
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await (from payload in context.SecretPayloads.AsNoTracking()
                         join version in context.SecretVersions.AsNoTracking() on payload.SecretVersionId equals version.Id
                         join secret in context.Secrets.AsNoTracking() on version.SecretId equals secret.Id
                         where secret.EnvironmentRecordId == environmentId && secret.Id == secretId && version.Id == versionId
                         select new { version.ProtectionFormat, version.FormatVersion, payload.Bytes })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        PlaintextProtectionFormat.ValidateForRead(row.ProtectionFormat, row.FormatVersion);
        return new SecretMaterial(row.Bytes);
    }

    private static void Validate(EnvironmentRecordId environment, SecretRef reference, byte[] material,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(material);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

internal static class PlaintextProtectionFormat
{
    internal const string Name = "Plaintext";
    internal const int Version = 1;

    internal static void ValidateForRead(string format, int version)
    {
        if (format != Name || version != Version)
            throw new InvalidDataException("Unsupported stored secret protection format.");
    }
}

public static class PlaintextSecretStoreServiceCollectionExtensions
{
    /// <summary>Explicitly selects Plaintext storage for internal callers; no host default is registered.</summary>
    public static IServiceCollection AddPostgreSqlPlaintextSecretStore(this IServiceCollection services)
    {
        services.AddSingleton<ISecretStore, PostgreSqlPlaintextSecretStore>();
        return services;
    }
}
