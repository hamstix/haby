using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Models
{
    /// <summary>
    /// The Service model.
    /// </summary>
    public class Service
    {
        /// <summary>
        /// Service Id.
        /// </summary>
        public long Id { get; private set; }

        /// <summary>
        /// Service name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Service Parameters.
        /// </summary>
        public JsonObject JsonConfig { get; set; } = new JsonObject();

        /// <summary>
        /// The template, that will be generated as service configuration for the microservice.
        /// </summary>
        public string? Template { get; set; }

        /// <summary>
        /// The Plugin to be used as default service configurator. Can be null is no plugins used.
        /// </summary>
        public string? PluginName { get; set; }

        /// <summary>
        /// Список микросервисов для сервиса.
        /// </summary>
        public IEnumerable<ConfigurationUnitAtService> ConfigurationUnits { get; private set; } = 
            new List<ConfigurationUnitAtService>();

        private Service() // For serializers.
        {
        }

        public Service(string name)
        {
            Name = name;
        }
    }
}
