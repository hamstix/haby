using System.Text.Json.Nodes;
using Haby.Domain;
using Haby.PostgreSql;
using Npgsql;
using Xunit;

namespace Haby.Application.Tests;

public sealed class PostgreSqlProviderTransportTests
{
    [Fact]
    public void Fresh_default_is_verify_full_and_only_explicit_disable_is_saved_without_fallback()
    {
        var codec = new PostgreSqlProviderConfigurationCodec(new PostgreSqlDatabaseCheckpointCapability());
        var configuration = new PostgreSqlProviderConfiguration(PostgreSqlProviderConfiguration.CurrentShapeVersion,
            "localhost", 5432, "postgres", "admin", new(SecretId.New(), SecretVersionId.New()), PostgreSqlDatabaseCheckpointCapability.ImplementationId);
        Assert.Equal(PostgreSqlTransportMode.VerifyFull, configuration.TransportMode);
        foreach (var mode in new[] { PostgreSqlTransportMode.VerifyFull, PostgreSqlTransportMode.Disable })
        {
            var selected = new PostgreSqlProviderConfiguration(PostgreSqlProviderConfiguration.CurrentShapeVersion,
                configuration.Host, configuration.Port, configuration.MaintenanceDatabase, configuration.AdministratorUserName,
                configuration.AdministratorPassword, configuration.ImplementationId, mode);
            var encoded = codec.Encode(selected);
            Assert.Equal(2, encoded.PayloadFormatVersion);
            Assert.Equal(selected, codec.DecodeTyped(encoded.PayloadJson));
            var builder = new NpgsqlConnectionStringBuilder { SslMode = SslMode.Prefer, GssEncryptionMode = GssEncryptionMode.Prefer };
            new PostgreSqlProviderTransport().Apply(builder, selected);
            Assert.Equal(mode == PostgreSqlTransportMode.Disable ? SslMode.Disable : SslMode.VerifyFull, builder.SslMode);
            Assert.Equal(GssEncryptionMode.Disable, builder.GssEncryptionMode);
            Assert.Null(builder.RootCertificate);
        }
        var payload = JsonNode.Parse(codec.Encode(configuration).PayloadJson)!.AsObject();
        payload.Remove("TransportMode");
        Assert.Throws<InvalidDataException>(() => codec.DecodeTyped(payload.ToJsonString()));
        foreach (var invalid in new[] { "prefer", "allow", "require", "verify-ca", "", "DISABLE" })
        {
            payload["TransportMode"] = invalid;
            Assert.Throws<InvalidDataException>(() => codec.DecodeTyped(payload.ToJsonString()));
        }
    }
}
