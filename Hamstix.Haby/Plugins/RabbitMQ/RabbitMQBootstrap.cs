using Hamstix.Haby.Shared.PluginsCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hamstix.Haby.Plugins.RabbitMQ
{
    public class RabbitMQBootstrap : IPluginBootstrap
    {
        public Plugin Plugin => new Plugin("RabbitMQ")
        {
            StrategyType = typeof(RabbitMQStrategy)
        };

        public void RegisterServiceProvider(IServiceCollection services, IConfiguration configuration)
        {
            services.AddTransient<RabbitMQStrategy>();
        }
    }
}