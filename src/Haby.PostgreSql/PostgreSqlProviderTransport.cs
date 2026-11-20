using Npgsql;

namespace Haby.PostgreSql;

public enum PostgreSqlTransportMode { VerifyFull, Disable }

/// <summary>Closed exact-revision transport settings. Consumer Binding options never enter this boundary.</summary>
public sealed class PostgreSqlProviderTransport
{
    private readonly string? fixtureRootCertificate;
    public PostgreSqlProviderTransport() { }
    // Trusted owned-fixture seam only. Production configuration cannot select a CA file or bypass validation.
    internal PostgreSqlProviderTransport(string fixtureRootCertificate) => this.fixtureRootCertificate = fixtureRootCertificate;

    internal void Apply(NpgsqlConnectionStringBuilder builder, PostgreSqlProviderConfiguration configuration)
    {
        builder.GssEncryptionMode = GssEncryptionMode.Disable;
        builder.SslMode = configuration.TransportMode switch
        {
            PostgreSqlTransportMode.VerifyFull => SslMode.VerifyFull,
            PostgreSqlTransportMode.Disable => SslMode.Disable,
            _ => throw new InvalidDataException("Unsupported PostgreSQL transport mode.")
        };
        builder.RootCertificate = configuration.TransportMode == PostgreSqlTransportMode.VerifyFull ? fixtureRootCertificate : null;
    }

    internal static PostgreSqlTransportMode Parse(string mode) => mode switch
    {
        "verify-full" => PostgreSqlTransportMode.VerifyFull,
        "disable" => PostgreSqlTransportMode.Disable,
        _ => throw new ArgumentException("Unsupported PostgreSQL transport mode.")
    };
    internal static string Encode(PostgreSqlTransportMode mode) => mode switch
    {
        PostgreSqlTransportMode.VerifyFull => "verify-full",
        PostgreSqlTransportMode.Disable => "disable",
        _ => throw new ArgumentException("Unsupported PostgreSQL transport mode.")
    };
}
