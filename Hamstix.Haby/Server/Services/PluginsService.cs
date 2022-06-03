using Hamstix.Haby.Shared.PluginsCore;

namespace Hamstix.Haby.Server.Services
{
    public class PluginsService : IPluginsService
    {
        readonly Dictionary<string, Plugin> _plugins;

        public PluginsService(IEnumerable<Plugin> plugins)
        {
            _plugins = plugins.ToDictionary(x => x.Name);
        }

        public IEnumerable<Plugin> Plugins => _plugins.Select(x => x.Value);

        /// <summary>
        /// Get a plugin from the list of registered plugins.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public Plugin? GetPluginByName(string name)
        {
            if (_plugins.ContainsKey(name))
                return _plugins[name];

            return null;
        }
    }
}
