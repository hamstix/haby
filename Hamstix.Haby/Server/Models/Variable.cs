using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Models
{
    public class Variable
    {
        /// <summary>
        /// The variable name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The variable value.
        /// </summary>
        public JsonNode Value { get; set; }

        /// <summary>
        /// The variable value source - service or configuration unit.
        /// </summary>
        public VariableTypes Type { get; private set; }

        public ConfigurationUnitAtService ConfigurationUnitAtService { get; private set; }
        public long ServiceId { get; private set; }
        public long ConfigurationUnitId { get; private set; }
        public string Key { get; private set; }

        public Variable(string name, JsonNode value, VariableTypes type)
        {
            Name = name;
            Value = value;
            Type = type;
        }

        Variable() { } // For serializers.

        public void SetCuAtService(ConfigurationUnitAtService microserviceAtService)
        {
            ConfigurationUnitAtService = microserviceAtService;
            ServiceId = microserviceAtService.ServiceId;
            ConfigurationUnitId = microserviceAtService.ConfigurationUnitId;
            Key = microserviceAtService.Key;
        }
    }
}
