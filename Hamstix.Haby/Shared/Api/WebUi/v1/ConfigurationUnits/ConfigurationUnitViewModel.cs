using System.Text.Json.Nodes;

namespace Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits
{
    public class ConfigurationUnitViewModel
    {
        /// <summary>
        /// The configuration unit id.
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// The configuration unit name.
        /// </summary>
        public string Name { get; set; } = default!;

        /// <summary>
        /// The current configuration unit version.
        /// </summary>
        public string Version { get; set; } = default!;

        /// <summary>
        /// The configuration unit template.
        /// </summary>
        public JsonNode Template { get; set; } = new JsonArray();
    }
}
