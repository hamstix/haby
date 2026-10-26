using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haby.Persistence.PostgreSql;

/// <summary>Provider-neutral, Environment-scoped append-only configuration storage.</summary>
public sealed class PostgreSqlProviderConfigurationStore : IProviderConfigurationStore
{
    private readonly IDbContextFactory<ManifestCatalogDbContext> _contexts;
    private readonly IProviderConfigurationCodec[] _codecs;

    public PostgreSqlProviderConfigurationStore(IDbContextFactory<ManifestCatalogDbContext> contexts,
        IEnumerable<IProviderConfigurationCodec> codecs)
    {
        _contexts = contexts;
        _codecs = codecs.ToArray();
        if (_codecs.GroupBy(codec => codec.ConfigurationType).Any(group => group.Count() != 1) ||
            _codecs.GroupBy(codec => (codec.ProviderKindId, codec.ShapeId, codec.PayloadFormatVersion))
                .Any(group => group.Count() != 1))
            throw new InvalidOperationException("Provider configuration codec registration is ambiguous.");
    }

    public async ValueTask<ProviderInstanceId> CreateInstanceAsync<TConfiguration>(EnvironmentRecordId environment,
        TConfiguration configuration, CancellationToken cancellationToken) where TConfiguration : class
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = Prepare(configuration);
        var environmentId = Guid.Parse(environment.ToString());
        await using var context = await _contexts.CreateDbContextAsync(cancellationToken);
        if (!await context.Environments.AsNoTracking().AnyAsync(row => row.Id == environmentId, cancellationToken))
            throw new InvalidOperationException("Environment is unavailable.");
        var id = ProviderInstanceId.New();
        context.ProviderInstances.Add(new ProviderInstanceRow
        {
            Id = Guid.Parse(id.ToString()),
            EnvironmentRecordId = environmentId,
            ProviderKindId = prepared.ProviderKindId,
            TargetFormatVersion = prepared.TargetIdentity.FormatVersion,
            TargetIdentity = prepared.TargetIdentity.Value
        });
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { throw new InvalidOperationException("Provider instance write failed."); }
        return id;
    }

    public async ValueTask AddRevisionAsync<TConfiguration>(EnvironmentRecordId environment, ProviderInstanceId instanceId,
        ProviderConfigurationRevisionNumber revision, TConfiguration configuration,
        CancellationToken cancellationToken) where TConfiguration : class
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = Prepare(configuration);
        var environmentId = Guid.Parse(environment.ToString());
        var id = Guid.Parse(instanceId.ToString());
        await using var context = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var instance = await context.ProviderInstances.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (instance is null || instance.EnvironmentRecordId != environmentId)
            throw new InvalidOperationException("Provider instance is unavailable in this Environment.");
        if (instance.ProviderKindId != prepared.ProviderKindId ||
            instance.TargetFormatVersion != prepared.TargetIdentity.FormatVersion ||
            instance.TargetIdentity != prepared.TargetIdentity.Value)
            throw new InvalidOperationException("Provider target differs from the instance.");
        var maximum = await context.ProviderConfigurationRevisions.AsNoTracking()
            .Where(row => row.ProviderInstanceId == id).MaxAsync(row => (long?)row.RevisionNumber, cancellationToken);
        if (maximum is null ? revision.Value != 1 : revision.Value <= maximum.Value)
            throw new InvalidOperationException("Provider revision number is not increasing.");
        foreach (var reference in prepared.SecretReferences)
        {
            var secretId = Guid.Parse(reference.Reference.SecretId.ToString());
            var versionId = Guid.Parse(reference.Reference.SecretVersionId.ToString());
            var available = await (from secret in context.Secrets.AsNoTracking()
                                   join version in context.SecretVersions.AsNoTracking() on secret.Id equals version.SecretId
                                   where secret.Id == secretId && secret.EnvironmentRecordId == environmentId &&
                                         version.Id == versionId
                                   select version.Id).AnyAsync(cancellationToken);
            if (!available) throw new InvalidOperationException("Provider secret reference is unavailable.");
        }
        context.ProviderConfigurationRevisions.Add(new ProviderConfigurationRevisionRow
        {
            ProviderInstanceId = id,
            RevisionNumber = revision.Value,
            EnvironmentRecordId = environmentId,
            ImplementationId = prepared.ImplementationId,
            ConfigurationShapeId = prepared.ShapeId,
            PayloadFormatVersion = prepared.PayloadFormatVersion,
            PayloadJson = prepared.PayloadJson
        });
        foreach (var reference in prepared.SecretReferences)
            context.ProviderRevisionSecretRefs.Add(new ProviderRevisionSecretRefRow
            {
                ProviderInstanceId = id,
                RevisionNumber = revision.Value,
                EnvironmentRecordId = environmentId,
                Slot = reference.Slot,
                SecretId = Guid.Parse(reference.Reference.SecretId.ToString()),
                SecretVersionId = Guid.Parse(reference.Reference.SecretVersionId.ToString())
            });
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (DbUpdateException) { throw new InvalidOperationException("Provider revision write failed."); }
        cancellationToken.ThrowIfCancellationRequested();
        var sealedRows = await context.ProviderConfigurationRevisions
            .Where(row => row.ProviderInstanceId == id && row.RevisionNumber == revision.Value && !row.IsSealed)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsSealed, true), cancellationToken);
        if (sealedRows != 1) throw new InvalidOperationException("Provider revision sealing failed.");
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    public async ValueTask<ProviderConfigurationRevision<TConfiguration>?> FindRevisionAsync<TConfiguration>(
        EnvironmentRecordId environment, ProviderInstanceId instanceId,
        ProviderConfigurationRevisionNumber revision, CancellationToken cancellationToken) where TConfiguration : class
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(revision);
        cancellationToken.ThrowIfCancellationRequested();
        var environmentId = Guid.Parse(environment.ToString());
        var id = Guid.Parse(instanceId.ToString());
        await using var context = await _contexts.CreateDbContextAsync(cancellationToken);
        var row = await (from item in context.ProviderConfigurationRevisions.AsNoTracking()
                         join instance in context.ProviderInstances.AsNoTracking() on item.ProviderInstanceId equals instance.Id
                         where item.ProviderInstanceId == id && item.RevisionNumber == revision.Value &&
                               item.EnvironmentRecordId == environmentId
                         select new { Revision = item, Instance = instance }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        if (!row.Revision.IsSealed) throw new InvalidDataException("Stored Provider revision is incomplete.");
        var codec = _codecs.SingleOrDefault(item => item.ProviderKindId == row.Instance.ProviderKindId &&
            item.ShapeId == row.Revision.ConfigurationShapeId &&
            item.PayloadFormatVersion == row.Revision.PayloadFormatVersion);
        if (codec is null || codec.ConfigurationType != typeof(TConfiguration))
            throw new InvalidDataException("Provider configuration shape is unavailable.");
        var associated = await context.ProviderRevisionSecretRefs.AsNoTracking()
            .Where(item => item.ProviderInstanceId == id && item.RevisionNumber == revision.Value)
            .Select(item => new { item.Slot, item.SecretId, item.SecretVersionId })
            .ToListAsync(cancellationToken);
        try
        {
            var decoded = codec.Decode(row.Revision.PayloadJson);
            var prepared = codec.Encode(decoded);
            ValidatePrepared(codec, prepared);
            if (prepared.ProviderKindId != row.Instance.ProviderKindId ||
                prepared.TargetIdentity.FormatVersion != row.Instance.TargetFormatVersion ||
                prepared.TargetIdentity.Value != row.Instance.TargetIdentity ||
                prepared.ImplementationId != row.Revision.ImplementationId ||
                prepared.ShapeId != row.Revision.ConfigurationShapeId ||
                prepared.PayloadFormatVersion != row.Revision.PayloadFormatVersion ||
                prepared.PayloadJson != row.Revision.PayloadJson ||
                prepared.SecretReferences.Count != associated.Count ||
                prepared.SecretReferences.Any(reference => !associated.Any(item =>
                    item.Slot == reference.Slot && item.SecretId == Guid.Parse(reference.Reference.SecretId.ToString()) &&
                    item.SecretVersionId == Guid.Parse(reference.Reference.SecretVersionId.ToString()))))
                throw new InvalidDataException("Stored Provider configuration is inconsistent.");
            return new ProviderConfigurationRevision<TConfiguration>(instanceId, revision, environment,
                (TConfiguration)decoded);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or FormatException or JsonException)
        { throw new InvalidDataException("Stored Provider configuration is invalid."); }
    }

    private PreparedProviderConfiguration Prepare<TConfiguration>(TConfiguration configuration) where TConfiguration : class
    {
        var codec = _codecs.SingleOrDefault(item => item.ConfigurationType == typeof(TConfiguration))
            ?? throw new InvalidOperationException("Provider configuration shape is unavailable.");
        var prepared = codec.Encode(configuration);
        ValidatePrepared(codec, prepared);
        return prepared;
    }

    private static void ValidatePrepared(IProviderConfigurationCodec codec, PreparedProviderConfiguration prepared)
    {
        if (prepared.ProviderKindId != codec.ProviderKindId || prepared.ShapeId != codec.ShapeId ||
            prepared.PayloadFormatVersion != codec.PayloadFormatVersion ||
            string.IsNullOrWhiteSpace(prepared.ProviderKindId) || prepared.ProviderKindId.Length > 128 ||
            string.IsNullOrWhiteSpace(prepared.ShapeId) || prepared.ShapeId.Length > 128 ||
            prepared.PayloadFormatVersion <= 0 || string.IsNullOrWhiteSpace(prepared.ImplementationId) ||
            prepared.ImplementationId.Length > 128 || string.IsNullOrWhiteSpace(prepared.PayloadJson) ||
            prepared.TargetIdentity is null || prepared.SecretReferences is null ||
            prepared.SecretReferences.Any(item => item is null || string.IsNullOrWhiteSpace(item.Slot) ||
                item.Slot.Length > 128 || item.Reference is null) ||
            prepared.SecretReferences.Select(item => item.Slot).Distinct(StringComparer.Ordinal).Count() !=
                prepared.SecretReferences.Count)
            throw new InvalidOperationException("Provider configuration codec returned an invalid snapshot.");
        try
        {
            using var document = JsonDocument.Parse(prepared.PayloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Provider configuration codec returned an invalid snapshot.");
        }
        catch (JsonException) { throw new InvalidOperationException("Provider configuration codec returned an invalid snapshot."); }
    }
}

public static class ProviderConfigurationStoreServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlProviderConfigurationStore(this IServiceCollection services)
    {
        services.AddSingleton<IProviderConfigurationStore, PostgreSqlProviderConfigurationStore>();
        return services;
    }
}
