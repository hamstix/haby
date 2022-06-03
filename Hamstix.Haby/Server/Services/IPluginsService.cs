using Hamstix.Haby.Shared.PluginsCore;

namespace Hamstix.Haby.Server.Services
{
    public interface IPluginsService
    {
        /// <summary>
        /// List of plugins registered in the system.
        /// </summary>
        IEnumerable<Plugin> Plugins { get; }

        /// <summary>
        /// Get plugin with task by name.
        /// </summary>
        /// <param name="name">Название </param>
        /// <returns></returns>
        Plugin? GetPluginByName(string name);
    }
}
