using Hamstix.Haby.Shared.PluginsCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hamstix.Haby.Plugins.ClickHouseHttp
{
    public class ClickHouseHttpBootstrap : IPluginBootstrap
    {
        public Plugin Plugin => new Plugin("ClickHouseHttp")
        {
            StrategyType = typeof(ClickHouseHttpStrategy)
        };

        public void RegisterServiceProvider(IServiceCollection services, IConfiguration configuration)
        {
            services.AddTransient<ClickHouseHttpStrategy>();
        }
    }
}