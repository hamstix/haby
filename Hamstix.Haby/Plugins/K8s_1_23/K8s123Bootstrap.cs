using Hamstix.Haby.Plugins.K8s_1_23.Handlers;
using Hamstix.Haby.Shared.PluginsCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hamstix.Haby.Plugins.K8s_1_23
{
    public class K8s123Bootstrap : IPluginBootstrap
    {
        public Plugin Plugin => new Plugin("K8s1_23")
        {
            StrategyType = typeof(K8s123MQStrategy)
        };

        public void RegisterServiceProvider(IServiceCollection services, IConfiguration configuration)
        {
            services.AddTransient<K8s123MQStrategy>();
            services.AddTransient<KubernetesDeploymentHandler>();
            services.AddTransient<KubernetesIngressHandler>();
            services.AddTransient<KubernetesServiceHandler>();
        }
    }
}