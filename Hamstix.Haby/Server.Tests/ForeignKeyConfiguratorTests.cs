using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Configurator;
using Hamstix.Haby.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hamstix.Haby.Server.Tests;

public class ForeignKeyConfiguratorTests
{
    [Fact]
    public async Task GetCuForeignKey_ResolvesKeyInSameConfigurationUnit()
    {
        var database = new TestDatabase();
        long configurationUnitId;
        await using (var arrangeContext = database.CreateContext())
        {
            var service = new Service("PostgreSql");
            var configurationUnit = new ConfigurationUnit("orders", "1.0.0");
            arrangeContext.AddRange(service, configurationUnit);
            await arrangeContext.SaveChangesAsync();
            arrangeContext.Add(new ConfigurationUnitAtService(configurationUnit, service, "source.json"));
            await arrangeContext.SaveChangesAsync();
            configurationUnitId = configurationUnit.Id;
        }

        await using var actContext = database.CreateContext();
        var loadedUnit = await actContext.ConfigurationUnits
            .Include(unit => unit.Services)
                .ThenInclude(association => association.Service)
            .SingleAsync(unit => unit.Id == configurationUnitId);

        var result = await CreateSut(actContext).GetCuForeignKey(
            loadedUnit,
            "PostgreSql",
            "source.json");

        Assert.NotNull(result);
        Assert.Equal(configurationUnitId, result.ConfigurationUnitId);
        Assert.Equal("source.json", result.Key);
    }

    [Fact]
    public async Task GetCuForeignKey_ResolvesKeyInAnotherConfigurationUnit()
    {
        var database = new TestDatabase();
        long targetId;
        long sourceId;
        await using (var arrangeContext = database.CreateContext())
        {
            var service = new Service("PostgreSql");
            var source = new ConfigurationUnit("shared", "1.0.0");
            var target = new ConfigurationUnit("orders", "1.0.0");
            arrangeContext.AddRange(service, source, target);
            await arrangeContext.SaveChangesAsync();
            arrangeContext.Add(new ConfigurationUnitAtService(source, service, "source.json"));
            await arrangeContext.SaveChangesAsync();
            targetId = target.Id;
            sourceId = source.Id;
        }

        await using var actContext = database.CreateContext();
        var targetUnit = await actContext.ConfigurationUnits.SingleAsync(unit => unit.Id == targetId);
        var result = await CreateSut(actContext).GetCuForeignKey(
            targetUnit,
            "PostgreSql",
            "shared/source.json");

        Assert.NotNull(result);
        Assert.Equal(sourceId, result.ConfigurationUnitId);
        Assert.Equal("source.json", result.Key);
    }

    static ForeignKeyConfigurator CreateSut(HabbyContext context) =>
        new(context, NullLogger<CuConfigurator>.Instance);

}
