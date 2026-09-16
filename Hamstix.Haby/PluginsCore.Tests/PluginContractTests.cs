using Hamstix.Haby.Plugins.ArangoDb;
using Hamstix.Haby.Plugins.ClickHouseHttp;
using Hamstix.Haby.Plugins.IdentityServer4EFApiResource;
using Hamstix.Haby.Plugins.K8s_1_23;
using Hamstix.Haby.Plugins.PostgreSql12;
using Hamstix.Haby.Plugins.RabbitMQ;
using Hamstix.Haby.Shared.PluginsCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hamstix.Haby.Shared.PluginsCore.Tests;

public class PluginContractTests
{
    public static IEnumerable<object[]> StandardPluginBootstraps()
    {
        yield return new object[] { new ArangoDbBootstrap() };
        yield return new object[] { new ClickHouseBootstrap() };
        yield return new object[] { new IdentityServer4Bootstrap() };
        yield return new object[] { new K8s123Bootstrap() };
        yield return new object[] { new PostgreSql12Bootstrap() };
        yield return new object[] { new RabbitMQBootstrap() };
    }

    [Theory]
    [MemberData(nameof(StandardPluginBootstraps))]
    public void StandardPlugin_SatisfiesBootstrapContract(IPluginBootstrap bootstrap)
    {
        var plugin = bootstrap.Plugin;

        Assert.False(string.IsNullOrWhiteSpace(plugin.Name));
        Assert.True(typeof(IStrategy).IsAssignableFrom(plugin.StrategyType));

        var services = new ServiceCollection();
        bootstrap.RegisterServiceProvider(services, null!);

        var strategyRegistration = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == plugin.StrategyType);
        Assert.Equal(plugin.StrategyType, strategyRegistration.ImplementationType);
        Assert.Equal(ServiceLifetime.Transient, strategyRegistration.Lifetime);
    }

    [Fact]
    public void StandardPlugins_ExposeUniqueNames()
    {
        var names = StandardPluginBootstraps()
            .Select(data => ((IPluginBootstrap)data[0]).Plugin.Name)
            .ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task StrategyContract_PropagatesCancellationToken()
    {
        var strategy = new ContractStrategy();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            strategy.Configure(
                new Service(1, "service", new()),
                new System.Text.Json.Nodes.JsonObject(),
                new System.Text.Json.Nodes.JsonObject(),
                cancellation.Token));

        Assert.Equal(cancellation.Token, strategy.ReceivedCancellationToken);
    }

    sealed class ContractStrategy : IStrategy
    {
        public CancellationToken ReceivedCancellationToken { get; private set; }

        public Task Configure(
            Service service,
            System.Text.Json.Nodes.JsonNode renderedTemplate,
            System.Text.Json.Nodes.JsonObject variables,
            CancellationToken cancellationToken)
        {
            ReceivedCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnConfigure(
            Service service,
            System.Text.Json.Nodes.JsonNode renderedTemplate,
            System.Text.Json.Nodes.JsonObject variables,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
