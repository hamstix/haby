using System.Text.Json.Nodes;

namespace Hamstix.Haby.Shared.Api.WebUi.v1.Keys
{
    public class ConfigurationCodeViewModel
    {
        public JsonNode Configuration { get; set; } = new JsonObject();
    }
}
