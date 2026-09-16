using Hamstix.Haby.Server.Configurator;
using Hamstix.Haby.Server.Services;
using Hamstix.Haby.Shared.PluginsCore;
using System.Text.Json.Nodes;
using Xunit;
using PluginService = Hamstix.Haby.Shared.PluginsCore.Service;
using ServerService = Hamstix.Haby.Server.Models.Service;

namespace Hamstix.Haby.Server.Tests;

public class ServiceConfiguratorTests
{
    [Fact]
    public async Task Configure_InvokesRegisteredPluginStrategy()
    {
        var strategy = new RecordingStrategy();
        var sut = CreateConfigurator(strategy);
        var service = CreateService();
        var renderedTemplate = JsonNode.Parse("""{"enabled":true}""")!;
        var variables = new JsonObject { ["username"] = "application" };

        var result = await sut.Configure(service, renderedTemplate, variables);

        Assert.Equal(ConfigurationResultStatuses.Ok, result.Status);
        Assert.Equal(service.Id, result.Service.Id);
        Assert.Equal(service.Name, result.Service.Name);
        Assert.Equal(service.Template, result.Service.Template);
        Assert.Same(renderedTemplate, strategy.RenderedTemplate);
        Assert.Same(variables, strategy.Variables);
        Assert.True(strategy.CancellationToken.CanBeCanceled);
    }

    [Fact]
    public async Task Configure_ConvertsPluginExceptionToFailedResult()
    {
        var sut = CreateConfigurator(new ThrowingStrategy());

        var result = await sut.Configure(
            CreateService(),
            new JsonObject(),
            new JsonObject());

        Assert.Equal(ConfigurationResultStatuses.Failed, result.Status);
        Assert.Equal("plugin failure", result.ErrorMessage);
    }

    [Fact]
    public async Task Configure_ThrowsWhenRegisteredStrategyCannotBeResolved()
    {
        var plugin = CreatePlugin();
        var sut = new ServiceConfigurator(
            new PluginsService(new[] { plugin }),
            new StrategyServiceProvider(null));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.Configure(CreateService(), new JsonObject(), new JsonObject()));

        Assert.Contains(plugin.Name, exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("MissingPlugin")]
    public async Task Configure_ReturnsDefaultResultWhenPluginCannotBeSelected(string? pluginName)
    {
        var service = CreateService();
        service.PluginName = pluginName;
        var sut = new ServiceConfigurator(
            new PluginsService(Array.Empty<Plugin>()),
            new StrategyServiceProvider(null));

        var result = await sut.Configure(service, new JsonObject(), new JsonObject());

        Assert.Equal(default, result.Status);
        Assert.Null(result.ErrorMessage);
    }

    static ServiceConfigurator CreateConfigurator(IStrategy strategy) =>
        new(
            new PluginsService(new[] { CreatePlugin() }),
            new StrategyServiceProvider(strategy));

    static Plugin CreatePlugin() => new("TestPlugin")
    {
        StrategyType = typeof(RecordingStrategy)
    };

    static ServerService CreateService() => new("PostgreSql")
    {
        JsonConfig = new JsonObject { ["host"] = "database.local" },
        PluginName = "TestPlugin",
        Template = "{}"
    };

    sealed class StrategyServiceProvider : IServiceProvider
    {
        readonly IStrategy? _strategy;

        public StrategyServiceProvider(IStrategy? strategy)
        {
            _strategy = strategy;
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(RecordingStrategy) ? _strategy : null;
    }

    sealed class RecordingStrategy : IStrategy
    {
        public JsonNode? RenderedTemplate { get; private set; }
        public JsonObject? Variables { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task Configure(
            PluginService service,
            JsonNode renderedTemplate,
            JsonObject variables,
            CancellationToken cancellationToken)
        {
            RenderedTemplate = renderedTemplate;
            Variables = variables;
            CancellationToken = cancellationToken;
            return Task.CompletedTask;
        }

        public Task UnConfigure(
            PluginService service,
            JsonNode renderedTemplate,
            JsonObject variables,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    sealed class ThrowingStrategy : IStrategy
    {
        public Task Configure(
            PluginService service,
            JsonNode renderedTemplate,
            JsonObject variables,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("plugin failure");

        public Task UnConfigure(
            PluginService service,
            JsonNode renderedTemplate,
            JsonObject variables,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
