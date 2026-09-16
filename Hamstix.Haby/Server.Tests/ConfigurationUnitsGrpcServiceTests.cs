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
        var database = new TestDatabase();
        long requestedUnitId;
        long otherUnitId;
        long serviceId;
        await using (var arrangeContext = database.CreateContext())
        {
            var service = new Service("PostgreSql");
            var requestedUnit = new ConfigurationUnit("requested", "1.0.0");
            var otherUnit = new ConfigurationUnit("other", "1.0.0");
            arrangeContext.AddRange(service, otherUnit, requestedUnit);
            await arrangeContext.SaveChangesAsync();

            AddVariable(otherUnit, service, "appsettings.json", "password", "other-value");
            AddVariable(requestedUnit, service, "appsettings.json", "password", "requested-value");
            await arrangeContext.SaveChangesAsync();
            requestedUnitId = requestedUnit.Id;
            otherUnitId = otherUnit.Id;
            serviceId = service.Id;
        }

        await using (var actContext = database.CreateContext())
        {
            var sut = new ConfigurationUnitsGrpcService(actContext, null!);
            await sut.DeleteVariable(new DeleteVariableRequest
            {
                Id = requestedUnitId,
                ServiceId = serviceId,
                Key = "appsettings.json",
                Name = "password"
            }, null!);
        }

        await using var assertContext = database.CreateContext();
        var remainingVariables = await assertContext.Variables.AsNoTracking().ToListAsync();
        var remaining = Assert.Single(remainingVariables);
        Assert.Equal(otherUnitId, remaining.ConfigurationUnitId);
        Assert.Equal("other-value", remaining.Value.GetValue<string>());
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
