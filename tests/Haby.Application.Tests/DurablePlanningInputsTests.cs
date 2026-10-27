using Haby.Application;
using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class DurablePlanningInputsTests
{
    [Fact]
    public void Every_exact_identity_participates_in_plan_input_equality()
    {
        var environment = EnvironmentRecordId.New();
        var application = ApplicationRecordId.New();
        var instance = ProviderInstanceId.New();
        var inputs = new DurablePlanningInputs(environment, EnvironmentId.Create("pilot"),
            ManifestRevisionRef.Create(application, ManifestRevisionNumber.Create(1)),
            ProviderAlias.Create("postgresql.primary"), instance,
            ProviderConfigurationRevisionNumber.Create(1), "haby.postgresql.database/v1alpha1");

        Assert.Equal(inputs, inputs with { ConfigurationRevision = ProviderConfigurationRevisionNumber.Create(1) });
        Assert.NotEqual(inputs, inputs with { Environment = EnvironmentRecordId.New() });
        Assert.NotEqual(inputs, inputs with { EnvironmentKey = EnvironmentId.Create("other") });
        Assert.NotEqual(inputs, inputs with
        {
            Revision = ManifestRevisionRef.Create(application, ManifestRevisionNumber.Create(2))
        });
        Assert.NotEqual(inputs, inputs with { Alias = ProviderAlias.Create("postgresql.other") });
        Assert.NotEqual(inputs, inputs with { ProviderInstance = ProviderInstanceId.New() });
        Assert.NotEqual(inputs, inputs with { ConfigurationRevision = ProviderConfigurationRevisionNumber.Create(2) });
        Assert.NotEqual(inputs, inputs with { ImplementationId = "other/v1" });
    }
}
