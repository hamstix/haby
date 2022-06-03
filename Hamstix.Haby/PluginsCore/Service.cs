using System.Text.Json.Nodes;

namespace Hamstix.Haby.Shared.PluginsCore
{
    public class Service
    {
        /// <summary>
        /// Service Id.
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// Service name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Service Parameters.
        /// </summary>
        public JsonObject JsonConfig { get; set; }

        /// <summary>
        /// The template, that will be generated as service configuration for the microservice.
        /// </summary>
        public string? Template { get; set; }

        public Service(long id, string name, JsonObject jsonConfig)
        {
            Id = id;
            Name = name;
            JsonConfig = jsonConfig;
        }
    }
}
