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

    public async ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsSupported) throw new ArgumentException("Unsupported secret owner or purpose.");
        if (definition.Owner is not SecretOwner.Environment)
            throw new ArgumentException("Resource credentials require the atomic generated-slot service.");
        var environment = definition.Environment;
        Validate(environment, reference, material, cancellationToken);
        var bytes = (byte[])material.Clone();
        try
        {
            var environmentId = Guid.Parse(environment.ToString());
            var secretId = Guid.Parse(reference.SecretId.ToString());
            var versionId = Guid.Parse(reference.SecretVersionId.ToString());
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (!await context.Environments.AsNoTracking().AnyAsync(row => row.Id == environmentId, cancellationToken))
                throw new InvalidOperationException("Environment is unavailable.");
            var secret = SecretRowMapping.Create(definition, secretId);
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
        finally { Array.Clear(bytes); }
    }

    public async ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material,
        CancellationToken cancellationToken)
    {
        Validate(environment, reference, material, cancellationToken);
        var bytes = (byte[])material.Clone();
        try
        {
            var environmentId = Guid.Parse(environment.ToString());
            var secretId = Guid.Parse(reference.SecretId.ToString());
            var versionId = Guid.Parse(reference.SecretVersionId.ToString());
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            // Lock the parent so concurrent rotations serialize; the version key remains a database uniqueness guard.
            var secret = await context.Secrets.FromSqlInterpolated($"SELECT * FROM secrets WHERE id = {secretId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (secret is null || secret.EnvironmentRecordId != environmentId ||
                !SecretRowMapping.IsAdministrative(secret))
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
        finally { Array.Clear(bytes); }
    }

    public async ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsSupported) throw new ArgumentException("Unsupported secret owner or purpose.");
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await ResolveAsync(context, definition, reference, cancellationToken);
    }

    public async ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes,
        CancellationToken cancellationToken)
    {
        try { return await ResolveDeliveryAsync(definition, reference, maximumBytes, cancellationToken); }
        catch (System.Data.Common.DbException) { throw new ConfigurationDeliveryStorageException(); }
    }

    private async ValueTask<SecretMaterial?> ResolveDeliveryAsync(SecretDefinition definition, SecretRef reference, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!definition.IsSupported || maximumBytes < 1) throw new ArgumentException("Invalid scoped secret request.");
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var secretId = Guid.Parse(reference.SecretId.ToString());
        var versionId = Guid.Parse(reference.SecretVersionId.ToString());
        var metadata = await (from secret in context.Secrets.AsNoTracking()
                              join version in context.SecretVersions on secret.Id equals version.SecretId
                              join payload in context.SecretPayloads on version.Id equals payload.SecretVersionId
                              where secret.Id == secretId && version.Id == versionId
                              select new { Secret = secret, Length = payload.Bytes.Length, version.ProtectionFormat, version.FormatVersion }).SingleOrDefaultAsync(cancellationToken);
        if (metadata is null || !SecretRowMapping.Matches(definition, metadata.Secret)) return null;
        if (metadata.Length > maximumBytes) throw new ConfigurationDocumentLimitException();
        PlaintextProtectionFormat.ValidateForRead(metadata.ProtectionFormat, metadata.FormatVersion);
        return await ResolveAsync(context, definition, reference, cancellationToken);
    }

    // Administrative acceptance already owns a transaction and its connection. Reuse it for the
    // exact-version read so retries also work with a one-connection pool and retain one boundary.
    internal async ValueTask<SecretMaterial?> ResolveAsync(ManifestCatalogDbContext context,
        SecretDefinition definition, SecretRef reference, CancellationToken cancellationToken)
    {
        var environmentId = Guid.Parse(definition.Environment.ToString());
        var secretId = Guid.Parse(reference.SecretId.ToString());
        var versionId = Guid.Parse(reference.SecretVersionId.ToString());
        var row = await (from payload in context.SecretPayloads.AsNoTracking()
                         join version in context.SecretVersions.AsNoTracking() on payload.SecretVersionId equals version.Id
                         join secret in context.Secrets.AsNoTracking() on version.SecretId equals secret.Id
                         where secret.EnvironmentRecordId == environmentId && secret.Id == secretId && version.Id == versionId
                         select new { Secret = secret, version.ProtectionFormat, version.FormatVersion, payload.Bytes })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        try
        {
            if (!SecretRowMapping.Matches(definition, row.Secret)) return null;
            PlaintextProtectionFormat.ValidateForRead(row.ProtectionFormat, row.FormatVersion);
            return new SecretMaterial(row.Bytes);
        }
        finally { Array.Clear(row.Bytes); }
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
        services.AddSingleton<IGeneratedCredentialSlots, PostgreSqlGeneratedCredentialSlots>();
        services.AddSingleton<IGeneratedCredentialReader, PostgreSqlGeneratedCredentialReader>();
        return services;
    }
}
