using Hamstix.Haby.Shared.PluginsCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hamstix.Haby.Plugins.ClickHouseHttp
{
    public class ClickHouseBootstrap : IPluginBootstrap
    {
        public Plugin Plugin => new Plugin("ClickHouse")
        {
            StrategyType = typeof(ClickHouseStrategy)
        };

        public void RegisterServiceProvider(IServiceCollection services, IConfiguration configuration)
        {
            services.AddTransient<ClickHouseStrategy>();
        }
    }
}