using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haby.Persistence.PostgreSql;

/// <summary>Environment-scoped, append-only Provider configuration storage.</summary>
public sealed class PostgreSqlProviderConfigurationStore(
    IDbContextFactory<ManifestCatalogDbContext> contexts,
    ICheckpointProviderCapability capabilities) : IProviderConfigurationStore
{
    public async ValueTask<ProviderInstanceId> CreateInstanceAsync(EnvironmentRecordId environment,
        string host, int port, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ValidateEndpoint(host, port);
        cancellationToken.ThrowIfCancellationRequested();
        var environmentId = Guid.Parse(environment.ToString());
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        if (!await context.Environments.AsNoTracking().AnyAsync(row => row.Id == environmentId, cancellationToken))
            throw new InvalidOperationException("Environment is unavailable.");
        var id = ProviderInstanceId.New();
        context.ProviderInstances.Add(new ProviderInstanceRow
        {
            Id = Guid.Parse(id.ToString()),
            EnvironmentRecordId = environmentId,
            Host = host,
            Port = port
        });
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { throw new InvalidOperationException("Provider instance write failed."); }
        return id;
    }

    public async ValueTask AddRevisionAsync(EnvironmentRecordId environment, ProviderInstanceId instanceId,
        ProviderConfigurationRevisionNumber revision, PostgreSqlProviderConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        if (!capabilities.Supports(new ProviderResolution(instanceId.ToString(), revision.Value,
                configuration.ImplementationId), PostgreSqlDatabaseContract.Id))
            throw new InvalidOperationException("Provider implementation is unavailable.");
        var environmentId = Guid.Parse(environment.ToString());
        var id = Guid.Parse(instanceId.ToString());
        var secretId = Guid.Parse(configuration.AdministratorPassword.SecretId.ToString());
        var versionId = Guid.Parse(configuration.AdministratorPassword.SecretVersionId.ToString());
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var instance = await context.ProviderInstances.FromSqlInterpolated(
            $"SELECT * FROM provider_instances WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (instance is null || instance.EnvironmentRecordId != environmentId)
            throw new InvalidOperationException("Provider instance is unavailable in this Environment.");
        if (instance.Host != configuration.Host || instance.Port != configuration.Port)
            throw new InvalidOperationException("Provider endpoint differs from the instance.");
        var maximum = await context.ProviderConfigurationRevisions.AsNoTracking()
            .Where(row => row.ProviderInstanceId == id).MaxAsync(row => (long?)row.RevisionNumber, cancellationToken);
        if (maximum is null ? revision.Value != 1 : revision.Value <= maximum.Value)
            throw new InvalidOperationException("Provider revision number is not increasing.");
        var secretAvailable = await (from secret in context.Secrets.AsNoTracking()
                                     join version in context.SecretVersions.AsNoTracking()
                                         on secret.Id equals version.SecretId
                                     where secret.Id == secretId && secret.EnvironmentRecordId == environmentId &&
                                           version.Id == versionId && secret.Purpose == "ProviderAdministrator"
                                     select version.Id).AnyAsync(cancellationToken);
        if (!secretAvailable) throw new InvalidOperationException("Provider secret reference is unavailable.");
        context.ProviderConfigurationRevisions.Add(new ProviderConfigurationRevisionRow
        {
            ProviderInstanceId = id,
            RevisionNumber = revision.Value,
            EnvironmentRecordId = environmentId,
            Host = configuration.Host,
            Port = configuration.Port,
            ShapeVersion = configuration.ShapeVersion,
            MaintenanceDatabase = configuration.MaintenanceDatabase,
            AdministratorUserName = configuration.AdministratorUserName,
            AdministratorSecretId = secretId,
            AdministratorSecretVersionId = versionId,
            ImplementationId = configuration.ImplementationId
        });
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (DbUpdateException) { throw new InvalidOperationException("Provider revision write failed."); }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    public async ValueTask<ProviderConfigurationRevision?> FindRevisionAsync(EnvironmentRecordId environment,
        ProviderInstanceId instanceId, ProviderConfigurationRevisionNumber revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(revision);
        cancellationToken.ThrowIfCancellationRequested();
        var environmentId = Guid.Parse(environment.ToString());
        var id = Guid.Parse(instanceId.ToString());
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await context.ProviderConfigurationRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProviderInstanceId == id && item.RevisionNumber == revision.Value &&
                item.EnvironmentRecordId == environmentId, cancellationToken);
        if (row is null) return null;
        try
        {
            var configuration = new PostgreSqlProviderConfiguration(row.ShapeVersion, row.Host, row.Port,
                row.MaintenanceDatabase, row.AdministratorUserName,
                new SecretRef(SecretId.Parse(row.AdministratorSecretId.ToString()),
                    SecretVersionId.Parse(row.AdministratorSecretVersionId.ToString())), row.ImplementationId);
            return new ProviderConfigurationRevision(instanceId, revision, environment, configuration);
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        { throw new InvalidDataException("Stored Provider revision is invalid."); }
    }

    private static void ValidateEndpoint(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 255 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("Invalid Provider host.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
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
