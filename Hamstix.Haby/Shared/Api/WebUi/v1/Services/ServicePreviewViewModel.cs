using System.Text.Json.Nodes;

namespace Hamstix.Haby.Shared.Api.WebUi.v1.Services
{
    public class ServicePreviewViewModel
    {
        public long Id { get; set; }

        public string Name { get; set; } = default!;

        public PluginInServiceViewModel Plugin { get; set; } = default!;

        public JsonNode JsonConfig { get; set; } = new JsonObject();
    }
}
