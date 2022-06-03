using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Models
{
    public class ConfigurationKey
    {
        /// <summary>
        /// The configuration unit key.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The configuration unit to which the key belongs.
        /// </summary>
        public ConfigurationUnit ConfigurationUnit { get; private set; }

        /// <summary>
        /// The configuration unit id to which the key belongs.
        /// </summary>
        public long ConfigurationUnitId { get; private set; }

        /// <summary>
        /// The configuration unit generated configuration.
        /// </summary>
        public JsonNode Configuration { get; set; } = new JsonObject();

        public ConfigurationKey(string name, ConfigurationUnit cu)
        {
            Name = name;
            ConfigurationUnit = cu;
            ConfigurationUnitId = ConfigurationUnit.Id;
        }

        private ConfigurationKey() { } // For serializers.
    }
}
