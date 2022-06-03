using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hamstix.Haby.Shared.PluginsCore
{
    public interface IPluginBootstrap
    {
        /// <summary>
        /// Register methods for working with the task in the DI container.
        /// </summary>
        /// <param name="services">The DI service configuration.</param>
        /// <param name="configuration">The IConfiguration provider.</param>
        void RegisterServiceProvider(IServiceCollection services, IConfiguration configuration);

        /// <summary>
        /// The Plugin.
        /// </summary>
        Plugin Plugin { get; }
    }
}
