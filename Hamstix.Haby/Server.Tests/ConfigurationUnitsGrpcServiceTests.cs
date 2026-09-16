using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Grpc;
using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Grpc.ConfigurationUnits;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using Xunit;

namespace Hamstix.Haby.Server.Tests;

public class ConfigurationUnitsGrpcServiceTests
{
    [Fact]
    public async Task DeleteVariable_DeletesOnlyVariableOwnedByRequestedConfigurationUnit()
    {
        await using var context = CreateContext();
        var service = new Service("PostgreSql");
        var requestedUnit = new ConfigurationUnit("requested", "1.0.0");
        var otherUnit = new ConfigurationUnit("other", "1.0.0");
        context.AddRange(service, otherUnit, requestedUnit);
        await context.SaveChangesAsync();

        AddVariable(otherUnit, service, "appsettings.json", "password", "other-value");
        AddVariable(requestedUnit, service, "appsettings.json", "password", "requested-value");
        await context.SaveChangesAsync();

        var sut = new ConfigurationUnitsGrpcService(context, null!);

        await sut.DeleteVariable(new DeleteVariableRequest
        {
            Id = requestedUnit.Id,
            ServiceId = service.Id,
            Key = "appsettings.json",
            Name = "password"
        }, null!);

        var remainingVariables = await context.Variables.AsNoTracking().ToListAsync();
        var remaining = Assert.Single(remainingVariables);
        Assert.Equal(otherUnit.Id, remaining.ConfigurationUnitId);
        Assert.Equal("other-value", remaining.Value.GetValue<string>());
    }

    static HabbyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<HabbyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new HabbyContext(options);
    }

    static void AddVariable(
        ConfigurationUnit configurationUnit,
        Service service,
        string key,
        string name,
        string value)
    {
        var configurationUnitAtService = new ConfigurationUnitAtService(configurationUnit, service, key);
        configurationUnitAtService.AddVariable(
            new Variable(name, JsonValue.Create(value)!, VariableTypes.Service));
    }
}
