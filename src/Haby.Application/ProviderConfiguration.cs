using Haby.Domain;

namespace Haby.Application;

/// <summary>Closed pilot PostgreSQL configuration. Credentials are represented only by an exact reference.</summary>
public sealed record PostgreSqlProviderConfiguration
{
    public const int CurrentShapeVersion = 1;

    public PostgreSqlProviderConfiguration(int shapeVersion, string host, int port, string maintenanceDatabase,
        string administratorUserName, SecretRef administratorPassword, string implementationId)
    {
        if (shapeVersion != CurrentShapeVersion) throw new ArgumentOutOfRangeException(nameof(shapeVersion));
        if (string.IsNullOrWhiteSpace(host) || host.Length > 255 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("Invalid Provider host.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrWhiteSpace(maintenanceDatabase) || maintenanceDatabase.Length > 63)
            throw new ArgumentException("Invalid maintenance database.", nameof(maintenanceDatabase));
        if (string.IsNullOrWhiteSpace(administratorUserName) || administratorUserName.Length > 63)
            throw new ArgumentException("Invalid administrator user name.", nameof(administratorUserName));
        ArgumentNullException.ThrowIfNull(administratorPassword);
        if (string.IsNullOrWhiteSpace(implementationId) || implementationId.Length > 128)
            throw new ArgumentException("Invalid Provider implementation.", nameof(implementationId));
        ShapeVersion = shapeVersion;
        Host = host;
        Port = port;
        MaintenanceDatabase = maintenanceDatabase;
        AdministratorUserName = administratorUserName;
        AdministratorPassword = administratorPassword;
        ImplementationId = implementationId;
    }

    public int ShapeVersion { get; }
    public string Host { get; }
    public int Port { get; }
    public string MaintenanceDatabase { get; }
    public string AdministratorUserName { get; }
    public SecretRef AdministratorPassword { get; }
    public string ImplementationId { get; }
    public override string ToString() => nameof(PostgreSqlProviderConfiguration);
}

public sealed record ProviderConfigurationRevision(ProviderInstanceId InstanceId,
    ProviderConfigurationRevisionNumber RevisionNumber, EnvironmentRecordId EnvironmentId,
    PostgreSqlProviderConfiguration Configuration);

/// <summary>Internal asynchronous store; no alias or secret-material resolution occurs here.</summary>
public interface IProviderConfigurationStore
{
    ValueTask<ProviderInstanceId> CreateInstanceAsync(EnvironmentRecordId environment, string host, int port,
        CancellationToken cancellationToken);
    ValueTask AddRevisionAsync(EnvironmentRecordId environment, ProviderInstanceId instanceId,
        ProviderConfigurationRevisionNumber revision, PostgreSqlProviderConfiguration configuration,
        CancellationToken cancellationToken);
    ValueTask<ProviderConfigurationRevision?> FindRevisionAsync(EnvironmentRecordId environment,
        ProviderInstanceId instanceId, ProviderConfigurationRevisionNumber revision, CancellationToken cancellationToken);
}
