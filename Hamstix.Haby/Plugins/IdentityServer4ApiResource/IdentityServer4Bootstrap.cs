using Hamstix.Haby.Shared.PluginsCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hamstix.Haby.Plugins.IdentityServer4EFApiResource
{
    public class IdentityServer4Bootstrap : IPluginBootstrap
    {
        public Plugin Plugin => new Plugin("IdentityServer4EFPgSqlApiResource")
        {
            StrategyType = typeof(IdentityServer4Strategy)
        };

        public void RegisterServiceProvider(IServiceCollection services, IConfiguration configuration)
        {
            services.AddTransient<IdentityServer4Strategy>();
        }
    }
}