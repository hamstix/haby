using Hamstix.Haby.Shared.PluginsCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hamstix.Haby.Plugins.PostgreSql12
{
    public class PostgreSql12Bootstrap : IPluginBootstrap
    {
        public Plugin Plugin => new Plugin("PostgreSql12")
        {
            StrategyType = typeof(PostgreSql12Strategy)
        };

        public void RegisterServiceProvider(IServiceCollection services, IConfiguration configuration)
        {
            services.AddTransient<PostgreSql12Strategy>();
        }
    }
}