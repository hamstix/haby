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
        var database = new TestDatabase();
        long configurationUnitId;
        await using (var arrangeContext = database.CreateContext())
        {
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
            arrangeContext.AddRange(service, configurationUnit);
            await arrangeContext.SaveChangesAsync();
            configurationUnitId = configurationUnit.Id;
        }

        await ExecuteConfigure(database, configurationUnitId);

        await using var assertContext = database.CreateContext();
        var configuredService = await assertContext.ConfigurationUnitsAtServices
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(
            "Host=database.local;Timeout=20",
            configuredService.RenderedTemplateJson!["connectionString"]!.GetValue<string>());
    }

    [Fact]
    public async Task Configure_ReusesSavedGeneratedVariable()
    {
        var database = new TestDatabase();
        long configurationUnitId;

        await using (var arrangeContext = database.CreateContext())
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
            arrangeContext.AddRange(service, configurationUnit, new Generator("username", "first-value"));
            await arrangeContext.SaveChangesAsync();
            configurationUnitId = configurationUnit.Id;
        }

        await ExecuteConfigure(database, configurationUnitId);

        await using (var updateContext = database.CreateContext())
        {
            var generator = await updateContext.Generators.SingleAsync();
            generator.Template = "second-value";
            await updateContext.SaveChangesAsync();
        }

        await ExecuteConfigure(database, configurationUnitId);

        await using (var assertContext = database.CreateContext())
        {
            var savedVariable = await assertContext.Variables.AsNoTracking().SingleAsync();
            var configuredService = await assertContext.ConfigurationUnitsAtServices.AsNoTracking().SingleAsync();

            Assert.Equal("first-value", savedVariable.Value.GetValue<string>());
            Assert.Equal(
                "first-value",
                configuredService.RenderedTemplateJson!["username"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Configure_MergesVariablesFromLeastToMostSpecific()
    {
        var database = new TestDatabase();
        long configurationUnitId;
        await using (var arrangeContext = database.CreateContext())
        {
            var service = new Service("PostgreSql")
            {
                JsonConfig = new JsonObject
                {
                    ["priority"] = "service",
                    ["serviceOnly"] = true
                },
                Template = """{"priority":"{{ priority }}","systemOnly":"{{ systemOnly }}","serviceOnly":"{{ serviceOnly }}","savedOnly":"{{ savedOnly }}","cuOnly":"{{ cuOnly }}"}"""
            };
            var configurationUnit = new ConfigurationUnit("orders", "1.0.0")
            {
                Template = JsonNode.Parse("""
                    [{
                      "key": "appsettings.json",
                      "services": {
                        "PostgreSql": {
                          "variables": {
                            "priority": "configuration-unit",
                            "cuOnly": "cu"
                          }
                        }
                      }
                    }]
                    """)!.AsArray()
            };
            arrangeContext.AddRange(
                service,
                configurationUnit,
                new SystemVariable("priority", JsonValue.Create("system")!),
                new SystemVariable("systemOnly", JsonValue.Create("system")!));
            await arrangeContext.SaveChangesAsync();
            var association = new ConfigurationUnitAtService(
                configurationUnit,
                service,
                "appsettings.json");
            association.AddVariable(new Variable(
                "priority",
                JsonValue.Create("saved")!,
                VariableTypes.Service));
            association.AddVariable(new Variable(
                "savedOnly",
                JsonValue.Create("saved")!,
                VariableTypes.Service));
            await arrangeContext.SaveChangesAsync();
            configurationUnitId = configurationUnit.Id;
        }

        await ExecuteConfigure(database, configurationUnitId);

        await using var assertContext = database.CreateContext();
        var associationResult = await assertContext.ConfigurationUnitsAtServices
            .AsNoTracking()
            .SingleAsync();
        var rendered = associationResult.RenderedTemplateJson!;
        Assert.Equal("configuration-unit", rendered["priority"]!.GetValue<string>());
        Assert.Equal("system", rendered["systemOnly"]!.GetValue<string>());
        Assert.Equal("True", rendered["serviceOnly"]!.GetValue<string>());
        Assert.Equal("saved", rendered["savedOnly"]!.GetValue<string>());
        Assert.Equal("cu", rendered["cuOnly"]!.GetValue<string>());
    }

    [Fact]
    public async Task Configure_RendersEmptyValueWhenGeneratorDoesNotExist()
    {
        var database = new TestDatabase();
        long configurationUnitId;
        await using (var arrangeContext = database.CreateContext())
        {
            var service = new Service("PostgreSql")
            {
                Template = """{"password":"{{ generate('missing', 'databasePassword') }}"}"""
            };
            var configurationUnit = CreateConfigurationUnitWithService("PostgreSql");
            arrangeContext.AddRange(service, configurationUnit);
            await arrangeContext.SaveChangesAsync();
            configurationUnitId = configurationUnit.Id;
        }

        await ExecuteConfigure(database, configurationUnitId);

        await using var assertContext = database.CreateContext();
        var configuredService = await assertContext.ConfigurationUnitsAtServices
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(string.Empty, configuredService.RenderedTemplateJson!["password"]!.GetValue<string>());
        Assert.Empty(await assertContext.Variables.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Configure_UpdatesParametersAndGeneratedConfigurationKey()
    {
        var database = new TestDatabase();
        long configurationUnitId;
        await using (var arrangeContext = database.CreateContext())
        {
            var configurationUnit = new ConfigurationUnit("orders", "1.0.0")
            {
                Template = CreateParameterTemplate("first")
            };
            arrangeContext.Add(configurationUnit);
            await arrangeContext.SaveChangesAsync();
            configurationUnitId = configurationUnit.Id;
        }

        await ExecuteConfigure(database, configurationUnitId);

        await using (var updateContext = database.CreateContext())
        {
            var configurationUnit = await updateContext.ConfigurationUnits
                .SingleAsync(unit => unit.Id == configurationUnitId);
            configurationUnit.Template = CreateParameterTemplate("second");
            await updateContext.SaveChangesAsync();
        }

        await ExecuteConfigure(database, configurationUnitId);

        await using var assertContext = database.CreateContext();
        var parameter = await assertContext.ConfigurationUnitParameters.AsNoTracking().SingleAsync();
        var key = await assertContext.ConfigurationKeys.AsNoTracking().SingleAsync();
        Assert.Equal("second", parameter.Value.GetValue<string>());
        Assert.Equal("second", key.Configuration["endpoint"]!.GetValue<string>());
    }

    [Fact]
    public async Task Configure_RemovesParametersAndConfigurationKeysMissingFromTemplate()
    {
        var database = new TestDatabase();
        long configurationUnitId;
        await using (var arrangeContext = database.CreateContext())
        {
            var configurationUnit = new ConfigurationUnit("orders", "1.0.0")
            {
                Template = CreateParameterTemplate("first")
            };
            arrangeContext.Add(configurationUnit);
            await arrangeContext.SaveChangesAsync();
            configurationUnitId = configurationUnit.Id;
        }

        await ExecuteConfigure(database, configurationUnitId);

        await using (var updateContext = database.CreateContext())
        {
            var configurationUnit = await updateContext.ConfigurationUnits
                .SingleAsync(unit => unit.Id == configurationUnitId);
            configurationUnit.Template = new JsonArray();
            await updateContext.SaveChangesAsync();
        }

        await ExecuteConfigure(database, configurationUnitId);

        await using var assertContext = database.CreateContext();
        Assert.Empty(await assertContext.ConfigurationUnitParameters.AsNoTracking().ToListAsync());
        Assert.Empty(await assertContext.ConfigurationKeys.AsNoTracking().ToListAsync());
    }

    static ConfigurationUnit CreateConfigurationUnitWithService(string serviceName) =>
        new("orders", "1.0.0")
        {
            Template = JsonNode.Parse($$"""
                [{
                  "key": "appsettings.json",
                  "services": {
                    "{{serviceName}}": {}
                  }
                }]
                """)!.AsArray()
        };

    static JsonArray CreateParameterTemplate(string value) =>
        JsonNode.Parse($$"""
            [{
              "key": "appsettings.json",
              "parameters": [{
                "name": "endpoint",
                "value": "{{value}}"
              }]
            }]
            """)!.AsArray();

    static CuConfigurator CreateConfigurator(HabbyContext context) =>
        new(
            context,
            new SuccessfulServiceConfigurator(),
            new ForeignKeyConfigurator(context, NullLogger<CuConfigurator>.Instance),
            NullLogger<CuConfigurator>.Instance);

    static async Task ExecuteConfigure(TestDatabase database, long configurationUnitId)
    {
        await using var actContext = database.CreateContext();
        await CreateConfigurator(actContext).Configure(configurationUnitId);
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
