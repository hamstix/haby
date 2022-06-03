using Hamstix.Haby.Shared.PluginsCore;

namespace Hamstix.Haby.Server.Models
{
    public class ConfigurationUnitKeyResult
    {
        public string Key { get; private set; }
        public IEnumerable<Shared.PluginsCore.ConfigurationResult> Results { get; set; } = 
            Enumerable.Empty<Shared.PluginsCore.ConfigurationResult>();

        public ConfigurationUnitKeyResult(string key)
        {
            Key = key;
        }

        public ConfigurationUnitKeyResult(string key, IEnumerable<ConfigurationResult> results) 
            : this(key)
        {
            Results = results;
        }
    }
}
