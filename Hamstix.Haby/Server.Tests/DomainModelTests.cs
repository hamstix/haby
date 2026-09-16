using Hamstix.Haby.Server.Models;
using System.Text.Json.Nodes;
using Xunit;

namespace Hamstix.Haby.Server.Tests;

public class DomainModelTests
{
    [Fact]
    public void ConfigurationUnit_TracksVersionAndOrganizationTransitions()
    {
        var originalOrganization = new OrganizationUnit("platform");
        var replacementOrganization = new OrganizationUnit("applications");
        var configurationUnit = new ConfigurationUnit("orders", "1.0.0");
        originalOrganization.ConfigurationUnits.Add(configurationUnit);

        configurationUnit.UpdateVersion("1.1.0");
        configurationUnit.ChangeOrganizationUnit(replacementOrganization);

        Assert.Equal("1.0.0", configurationUnit.PreviousVersion);
        Assert.Equal("1.1.0", configurationUnit.Version);
        Assert.Same(replacementOrganization, configurationUnit.OrganizationUnit);

        replacementOrganization.ConfigurationUnits.Add(configurationUnit);
        configurationUnit.ResetOrganizationUnit();

        Assert.Null(configurationUnit.OrganizationUnit);
        Assert.Null(configurationUnit.OrganizationUnitId);
        Assert.DoesNotContain(configurationUnit, replacementOrganization.ConfigurationUnits);
    }

    [Fact]
    public void OrganizationUnit_ConnectsChildToParent()
    {
        var parent = new OrganizationUnit("platform");

        var child = new OrganizationUnit("databases", parent);

        Assert.Same(parent, child.Parent);
        Assert.Contains(child, parent.Children);
    }

    [Fact]
    public void Variable_AdoptsConfigurationUnitServiceIdentity()
    {
        var configurationUnit = new ConfigurationUnit("orders", "1.0.0");
        var service = new Service("PostgreSql");
        var association = new ConfigurationUnitAtService(
            configurationUnit,
            service,
            "appsettings.json");
        var variable = new Variable(
            "password",
            JsonValue.Create("secret")!,
            VariableTypes.Service);

        association.AddVariable(variable);

        Assert.Same(association, variable.ConfigurationUnitAtService);
        Assert.Equal(association.ConfigurationUnitId, variable.ConfigurationUnitId);
        Assert.Equal(association.ServiceId, variable.ServiceId);
        Assert.Equal(association.Key, variable.Key);
        Assert.Contains(variable, association.Variables);
    }
}
