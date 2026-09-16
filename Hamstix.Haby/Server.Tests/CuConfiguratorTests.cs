using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Configurator;
using Hamstix.Haby.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using Xunit;
using ConfigurationResult = Hamstix.Haby.Shared.PluginsCore.ConfigurationResult;
using ConfigurationResultStatuses = Hamstix.Haby.Shared.PluginsCore.ConfigurationResultStatuses;
using PluginService = Hamstix.Haby.Shared.PluginsCore.Service;

namespace Hamstix.Haby.Server.Tests;

public class CuConfiguratorTests
{
    [Fact]
    public async Task Configure_RendersServiceTemplateWithConfigurationUnitVariables()
    {
        await using var context = CreateContext();
        var service = new Service("PostgreSql")
        {
            Template = """{"connectionString":"Host={{ host }};Timeout={{ timeout }}"}"""
        };
        var configurationUnit = new ConfigurationUnit("orders", "1.0.0")
        {
            Template = JsonNode.Parse("""
                [
                  {
                    "key": "appsettings.json",
                    "services": {
                      "PostgreSql": {
                        "variables": {
                          "host": "database.local",
                          "timeout": 20
                        }
                      }
                    }
                  }
                ]
                """)!.AsArray()
        };
        context.AddRange(service, configurationUnit);
        await context.SaveChangesAsync();

        var sut = CreateConfigurator(context);

        await sut.Configure(configurationUnit.Id);

        var configuredService = await context.ConfigurationUnitsAtServices.SingleAsync();
        Assert.Equal(
            "Host=database.local;Timeout=20",
            configuredService.RenderedTemplateJson!["connectionString"]!.GetValue<string>());
    }

    [Fact]
    public async Task Configure_ReusesSavedGeneratedVariable()
    {
        var databaseName = Guid.NewGuid().ToString();
        long configurationUnitId;

        await using (var context = CreateContext(databaseName))
        {
            var service = new Service("PostgreSql")
            {
                Template = """{"username":"{{ generate('username', 'databaseUsername') }}"}"""
            };
            var configurationUnit = new ConfigurationUnit("orders", "1.0.0")
            {
                Template = JsonNode.Parse("""
                    [
                      {
                        "key": "appsettings.json",
                        "services": {
                          "PostgreSql": {}
                        }
                      }
                    ]
                    """)!.AsArray()
            };
            context.AddRange(service, configurationUnit, new Generator("username", "first-value"));
            await context.SaveChangesAsync();
            configurationUnitId = configurationUnit.Id;

            await CreateConfigurator(context).Configure(configurationUnitId);
        }

        await using (var context = CreateContext(databaseName))
        {
            var generator = await context.Generators.SingleAsync();
            generator.Template = "second-value";
            await context.SaveChangesAsync();

            await CreateConfigurator(context).Configure(configurationUnitId);
        }

        await using (var context = CreateContext(databaseName))
        {
            var savedVariable = await context.Variables.AsNoTracking().SingleAsync();
            var configuredService = await context.ConfigurationUnitsAtServices.AsNoTracking().SingleAsync();

            Assert.Equal("first-value", savedVariable.Value.GetValue<string>());
            Assert.Equal(
                "first-value",
                configuredService.RenderedTemplateJson!["username"]!.GetValue<string>());
        }
    }

    static CuConfigurator CreateConfigurator(HabbyContext context) =>
        new(
            context,
            new SuccessfulServiceConfigurator(),
            new ForeignKeyConfigurator(context, NullLogger<CuConfigurator>.Instance),
            NullLogger<CuConfigurator>.Instance);

    static HabbyContext CreateContext(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<HabbyContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options;

        return new HabbyContext(options);
    }

    sealed class SuccessfulServiceConfigurator : IServiceConfigurator
    {
        public Task<ConfigurationResult> Configure(
            Service service,
            JsonNode renderedTemplate,
            JsonObject variables) =>
            Task.FromResult(new ConfigurationResult(
                new PluginService(service.Id, service.Name, service.JsonConfig))
            {
                Status = ConfigurationResultStatuses.Ok
            });
    }
}
