using System.Collections.Concurrent;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: CollectionBehavior("Haby.PostgreSql.IntegrationTests.PostgreSqlCollections", "Haby.PostgreSql.IntegrationTests")]

namespace Haby.PostgreSql.IntegrationTests;

[CollectionDefinition("Catalog database group")]
public sealed class CatalogDatabaseCollection : ICollectionFixture<CatalogServerFixture>;

[CollectionDefinition("Owned infrastructure controls", DisableParallelization = true)]
public sealed class OwnedInfrastructureCollection;

// Use xUnit's collection lifecycle, not permits held across framework scheduling.
// One catalog collection plus (bound - 1) sequential class-fixture lanes. Existing
// exclusive collections run outside those lanes and retain their definitions.
public sealed class PostgreSqlCollections(ITestAssembly assembly, IMessageSink sink) : IXunitTestCollectionFactory
{
    private readonly CollectionPerClassTestCollectionFactory _ordinary = new(assembly, sink);
    private readonly ConcurrentDictionary<int, ITestCollection> _lanes = new();
    internal static readonly string[] OwnedClasses =
    [
        "PostgreSqlApplicationLoginTests", "ApplicationConfigurationTests", "ApplicationGenerationTests",
        "OperatorHostIntegrationTests", "DurableDeploymentPlanningTests", "ExactActionAdmissionTests",
        "PostgreSqlPairPersistenceTests", "GeneratedCredentialSlotTests", "ProviderConfigurationStoreTests",
        "PostgreSqlIntegrationTests", "DurableExecutionTests", "PostgreSqlRestrictTests", "TestCatalogPoolTests", "ProviderDeclarationTests"
    ];
    internal static readonly string[] HeavyHostClasses =
        ["ApplicationConfigurationTests", "ApplicationGenerationTests", "OperatorHostIntegrationTests", "PostgreSqlIntegrationTests"];

    internal static int Lane(string name, int servers, int hosts)
    {
        var heavy = Array.IndexOf(HeavyHostClasses, name);
        return heavy >= 0 ? heavy % hosts : Array.IndexOf(OwnedClasses, name) % (servers - 1);
    }

    public string DisplayName => "bounded PostgreSQL collection lanes";

    public ITestCollection Get(ITypeInfo testClass)
    {
        if (Environment.GetEnvironmentVariable("HABY_POSTGRESQL_EPHEMERAL_EXTERNAL") == "1") return _ordinary.Get(testClass);
        var index = Array.IndexOf(OwnedClasses, testClass.Name.Split('.')[^1]);
        if (index < 0) return _ordinary.Get(testClass);
        var lane = Lane(testClass.Name.Split('.')[^1], PostgreSqlResources.Limit, PostgreSqlResources.HostLimit);
        return _lanes.GetOrAdd(lane, value => new TestCollection(assembly, null, "PostgreSQL owned lane " + value));
    }
}
