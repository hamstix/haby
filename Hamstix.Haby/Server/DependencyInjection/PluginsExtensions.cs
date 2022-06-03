using Hamstix.Haby.Server.Services;
using Hamstix.Haby.Shared.PluginsCore;
using Microsoft.Extensions.DependencyModel;
using System.Reflection;

namespace Hamstix.Haby.Server.DependencyInjection
{
    public static class PluginsExtensions
    {
        /// <summary>
        /// Perform registration of all plugins from loaded libraries.
        /// </summary>
        /// <param name="services">The DI service configuration.</param>
        /// <param name="configuration">The IConfiguration provider.</param>
        /// <returns></returns>
        public static IServiceCollection RegisterPlugins(this IServiceCollection services, IConfiguration configuration)
        {
            var platform = Environment.OSVersion.Platform.ToString();
            var runtimeAssemblyNames = DependencyContext.Default.GetRuntimeAssemblyNames(platform);

            var pluginTypes = runtimeAssemblyNames
                .Select(Assembly.Load)
                .SelectMany(a => a.ExportedTypes)
                .Where(t => typeof(IPluginBootstrap).IsAssignableFrom(t) && t.IsClass);

            Console.WriteLine("Loading plugins ....");
            List<IPluginBootstrap> plugins = new();
            foreach (var plugin in pluginTypes)
            {
                var obj = Activator.CreateInstance(plugin) as IPluginBootstrap;
                if (obj != null)
                {
                    obj.RegisterServiceProvider(services, configuration);
                    Console.WriteLine($"[{obj.Plugin.Name}] was loaded.");
                    plugins.Add(obj);
                }
                else
                    Console.Error.WriteLine($"Plugin from {plugin.FullName} was not loaded due to type mismatch.");
            }
            Console.WriteLine($"{plugins.Count} plugins was loaded.");

            // Singleton is required, otherwise there will be a large number of alocations.
            services.AddSingleton<IPluginsService, PluginsService>(
                resolver => new PluginsService(plugins.Select(x => x.Plugin)));

            return services;
        }
    }
}
