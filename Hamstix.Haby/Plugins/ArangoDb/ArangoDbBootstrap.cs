using Hamstix.Haby.Shared.PluginsCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hamstix.Haby.Plugins.ArangoDb
{
    public class ArangoDbBootstrap : IPluginBootstrap
    {
        public Plugin Plugin => new Plugin("ArangoDb")
        {
            StrategyType = typeof(ArangoDbStrategy)
        };

        public void RegisterServiceProvider(IServiceCollection services, IConfiguration configuration)
        {
            services.AddTransient<ArangoDbStrategy>();
        }
    }
}