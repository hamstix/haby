using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.Extensions.DependencyInjection;

namespace Haby.PostgreSql;

/// <summary>Closed pilot PostgreSQL configuration. It contains an exact reference, never credential bytes.</summary>
public sealed record PostgreSqlProviderConfiguration
{
    public const int CurrentShapeVersion = 2;

    public PostgreSqlProviderConfiguration(int shapeVersion, string host, int port, string maintenanceDatabase,
        string administratorUserName, SecretRef administratorPassword, string implementationId,
        PostgreSqlTransportMode transportMode = PostgreSqlTransportMode.VerifyFull)
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
        _ = PostgreSqlProviderTransport.Encode(transportMode);
        TransportMode = transportMode;
    }

    public int ShapeVersion { get; }
    public string Host { get; }
    public int Port { get; }
    public string MaintenanceDatabase { get; }
    public string AdministratorUserName { get; }
    public SecretRef AdministratorPassword { get; }
    public string ImplementationId { get; }
    public PostgreSqlTransportMode TransportMode { get; }
    public override string ToString() => nameof(PostgreSqlProviderConfiguration);
}

/// <summary>Typed PostgreSQL shape and source-generated JSON snapshot codec.</summary>
public sealed class PostgreSqlProviderConfigurationCodec(ICheckpointProviderCapability capabilities) :
    ProviderConfigurationCodec<PostgreSqlProviderConfiguration>
{
    public const string Kind = "postgresql/v1";
    public const string Shape = "haby.postgresql.connection/v1";
    public const string AdministratorPasswordSlot = "administrator-password";

    public override string ProviderKindId => Kind;
    public override string ShapeId => Shape;
    public override int PayloadFormatVersion => 2;

    public override PreparedProviderConfiguration Encode(PostgreSqlProviderConfiguration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!capabilities.Supports(new ProviderResolution("codec-validation", 1, value.ImplementationId),
                PostgreSqlDatabaseContract.Id))
            throw new InvalidOperationException("Provider implementation is unavailable.");
        var snapshot = new PostgreSqlConfigurationSnapshot
        {
            Host = value.Host,
            Port = value.Port,
            MaintenanceDatabase = value.MaintenanceDatabase,
            AdministratorUserName = value.AdministratorUserName,
            AdministratorSecretId = value.AdministratorPassword.SecretId.ToString(),
            AdministratorSecretVersionId = value.AdministratorPassword.SecretVersionId.ToString(),
            ImplementationId = value.ImplementationId,
            TransportMode = PostgreSqlProviderTransport.Encode(value.TransportMode)
        };
        var json = JsonSerializer.Serialize(snapshot, PostgreSqlConfigurationJsonContext.Default.PostgreSqlConfigurationSnapshot);
        return new PreparedProviderConfiguration(Kind, Target(value), value.ImplementationId, Shape, 2, json,
            [new ProviderConfigurationSecretReference(AdministratorPasswordSlot, value.AdministratorPassword)]);
    }

    public override PostgreSqlProviderConfiguration DecodeTyped(string payloadJson)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize(payloadJson,
                PostgreSqlConfigurationJsonContext.Default.PostgreSqlConfigurationSnapshot)
                ?? throw new InvalidDataException("Stored Provider configuration is invalid.");
            return new PostgreSqlProviderConfiguration(2, snapshot.Host, snapshot.Port,
                snapshot.MaintenanceDatabase, snapshot.AdministratorUserName,
                new SecretRef(SecretId.Parse(snapshot.AdministratorSecretId),
                    SecretVersionId.Parse(snapshot.AdministratorSecretVersionId)), snapshot.ImplementationId,
                PostgreSqlProviderTransport.Parse(snapshot.TransportMode));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException)
        {
            throw new InvalidDataException("Stored Provider configuration is invalid.");
        }
    }

    private static ProviderTargetIdentity Target(PostgreSqlProviderConfiguration value) =>
        new(1, $"{value.Host.Length}:{value.Host}:{value.Port}");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class PostgreSqlConfigurationSnapshot
{
    public required string Host { get; init; }
    public int Port { get; init; }
    public required string MaintenanceDatabase { get; init; }
    public required string AdministratorUserName { get; init; }
    public required string AdministratorSecretId { get; init; }
    public required string AdministratorSecretVersionId { get; init; }
    public required string ImplementationId { get; init; }
    public required string TransportMode { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, AllowDuplicateProperties = false, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(PostgreSqlConfigurationSnapshot))]
internal partial class PostgreSqlConfigurationJsonContext : JsonSerializerContext;

public static class PostgreSqlProviderConfigurationCodecServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlProviderConfigurationCodec(this IServiceCollection services)
    {
        services.AddSingleton<PostgreSqlProviderConfigurationCodec>();
        services.AddSingleton<IProviderConfigurationCodec>(provider => provider.GetRequiredService<PostgreSqlProviderConfigurationCodec>());
        services.AddSingleton<IProviderDeclarationAdapter, PostgreSqlProviderDeclarationAdapter>();
        return services;
    }
}
