using Haby.Application;
using Haby.Domain;
using Haby.PostgreSql;
using System.Collections.Immutable;
using Xunit;

namespace Haby.Application.Tests;

public sealed class PostgreSqlImportedConnectionTests
{
    [Fact]
    public void Imported_resolution_uses_exact_import_producer_and_existing_material_without_weakening_provision()
    {
        var contract = new PostgreSqlConnectionRendererContract();
        Assert.True(contract.SupportsProducer(PostgreSqlImportContracts.Login));
        Assert.False(contract.SupportsProducer(PostgreSqlImportContracts.Database));
        var pin = new ConfigurationRenderPin(ManifestLocalId.Create("worker"), 0, contract.Id,
            PostgreSqlConnectionRendering.Implementation, contract.Formatter, PostgreSqlConnectionRendering.Implementation,
            contract.Resource, ManifestLocalId.Create("login"), contract.OutputContract, contract.OutputKey,
            OperationId.Create("login-access"), PostgreSqlImportContracts.Login, contract.Defaults, contract.Defaults,
            ImmutableDictionary<string, ProfileOptionScope>.Empty);
        IConfigurationResolutionBinding binding = new PostgreSqlConnectionResolutionBinding();
        Assert.True(binding.ValidateMaterial(pin, "existing password;with=punctuation"));
        Assert.False(binding.ValidateMaterial(pin with { ProducerAction = PostgreSqlApplicationAccessAction.RegistrationKey }, "existing password;with=punctuation"));
        Assert.False(binding.ValidateMaterial(pin, "bad\0material"));
    }
    [Fact]
    public void Existing_login_uses_the_ordinary_connection_output_renderer_and_formatter()
    {
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        var connection = new PostgreSqlApplicationConnection("localhost", 5432, "existing_database", "public", "app_login", reference);
        var output = PostgreSqlApplicationConnectionContract.Encode(connection);
        Assert.Equal(connection, PostgreSqlApplicationConnectionContract.Decode(output));
        var options = new PostgreSqlConnectionRendererContract().Defaults;
        var fragment = Assert.IsType<ConfigurationFragment>(new PostgreSqlConnectionRenderer().Render(output, options));
        var deferred = Assert.IsType<ConfigurationDeferredValue>(fragment.Value);
        Assert.Equal(new ConfigurationSecretReference(reference), deferred.Arguments.Members["password"]);
        var formatted = new PostgreSqlConnectionFormatter().Format(deferred.Arguments, "existing password;with=punctuation");
        Assert.NotNull(formatted);
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(formatted.Reveal());
        Assert.Equal("app_login", builder.Username);
        Assert.Equal("existing password;with=punctuation", builder.Password);
    }
}
