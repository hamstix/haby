using Haby.Application;
using Haby.Domain;
using Haby.PostgreSql;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ProviderConfigurationIdentityTests
{
    [Fact]
    public void Identity_and_revision_are_typed_and_positive()
    {
        var id = ProviderInstanceId.New();
        Assert.Equal(id, ProviderInstanceId.Parse(id.ToString()));
        Assert.Equal(7, Guid.Parse(id.ToString()).Version);
        Assert.Equal(1, ProviderConfigurationRevisionNumber.Create(1).Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => ProviderConfigurationRevisionNumber.Create(0));
    }

    [Fact]
    public void Configuration_shape_is_closed_versioned_and_value_free()
    {
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        var configuration = new PostgreSqlProviderConfiguration(1, "localhost", 5432, "postgres", "admin", secret,
            "haby.postgresql.database/v1alpha1");
        Assert.Same(secret, configuration.AdministratorPassword);
        Assert.Equal(nameof(PostgreSqlProviderConfiguration), configuration.ToString());
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlProviderConfiguration(2,
            "localhost", 5432, "postgres", "admin", secret, configuration.ImplementationId));
        Assert.DoesNotContain(secret.SecretId.ToString(), error.ToString());
        Assert.Throws<ArgumentException>(() => new PostgreSqlProviderConfiguration(1,
            "not a host", 5432, "postgres", "admin", secret, configuration.ImplementationId));
        var codec = new PostgreSqlProviderConfigurationCodec(new PostgreSqlDatabaseCheckpointCapability());
        var encoded = codec.Encode(configuration);
        Assert.Equal(PostgreSqlProviderConfigurationCodec.Kind, encoded.ProviderKindId);
        Assert.Equal(PostgreSqlProviderConfigurationCodec.Shape, encoded.ShapeId);
        Assert.Equal(secret, Assert.Single(encoded.SecretReferences).Reference);
        Assert.Equal(configuration, codec.DecodeTyped(encoded.PayloadJson));
        Assert.Throws<InvalidDataException>(() => codec.DecodeTyped(encoded.PayloadJson.Insert(
            encoded.PayloadJson.Length - 1, ",\"Unknown\":1")));
    }
}
