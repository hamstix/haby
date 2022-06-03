using System.Text.Json.Nodes;

namespace Hamstix.Haby.Shared.Api.WebUi.v1.Services
{
    public class ServiceViewModel
    {
        public long Id { get; set; }

        public string Name { get; set; } = default!;

        public PluginInServiceViewModel Plugin { get; set; } = default!;

        public JsonNode JsonConfig { get; set; } = new JsonObject();

        /// <summary>
        /// The template, that will be generated as service configuration for the microservice.
        /// </summary>
        public string? Template { get; set; }
    }
}
