using System.Text.Json.Nodes;

namespace Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits.Variables
{
    public class ConfigurationUnitVariableViewModel
    {
        public string Key { get; set; } = default!;
        public ServiceInConfigurationUnitViewModel Service { get; set; } = default!;
        public string Name { get; set; } = default!;
        public JsonNode Value { get; set; } = default!;
    }
}
