using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class FixtureLayoutTests
{
    [Theory]
    [InlineData(null, 4)]
    [InlineData("2", 2)]
    [InlineData("8", 8)]
    public void Server_bound_accepts_only_supported_explicit_values(string? value, int expected) => Assert.Equal(expected, PostgreSqlResources.Parse(value));

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("9")]
    [InlineData("04")]
    [InlineData("PROTECTED-invalid")]
    public void Invalid_bound_is_refused_without_echoing_its_value(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PostgreSqlResources.Parse(value));
        Assert.Equal("HABY_TEST_POSTGRESQL_SERVERS must be an integer from 2 through 8.", error.Message);
    }

    [Fact]
    public void Catalog_candidates_do_not_allocate_one_server_per_class()
    {
        foreach (var type in new[] { typeof(ManifestCatalogPersistenceTests), typeof(ConfigurationProfilePersistenceTests),
            typeof(AdministrativeSecretTests), typeof(PlaintextSecretStoreTests), typeof(PlacementRevisionPersistenceTests) })
        {
            Assert.DoesNotContain(typeof(IClassFixture<PostgreSqlFixture>), type.GetInterfaces());
            Assert.True(typeof(CatalogTestCase).IsAssignableFrom(type));
            var collection = type.GetCustomAttributesData().Single(a => a.AttributeType == typeof(CollectionAttribute));
            Assert.Equal("Catalog database group", collection.ConstructorArguments[0].Value);
        }
    }

    [Fact]
    public void Every_nonexclusive_owned_fixture_has_a_bounded_lane()
    {
        var exclusive = new[] { typeof(ApplicationLoginLoggingTests), typeof(ProviderAliasTests),
            typeof(ProviderPreflightCompatibilityTests), typeof(OperatorProviderAccessTests) };
        foreach (var type in typeof(PostgreSqlFixture).Assembly.GetTypes()
            .Where(t => t.GetInterfaces().Contains(typeof(IClassFixture<PostgreSqlFixture>)) && !exclusive.Contains(t)))
            Assert.Contains(type.Name, PostgreSqlCollections.OwnedClasses);
    }

    [Fact]
    public void Catalog_labels_do_not_make_role_effects_eligible_for_reuse()
    {
        foreach (var type in new[] { typeof(PostgreSqlPairPersistenceTests), typeof(GeneratedCredentialSlotTests), typeof(ProviderConfigurationStoreTests) })
            Assert.Contains(typeof(IClassFixture<PostgreSqlFixture>), type.GetInterfaces());
    }
}
