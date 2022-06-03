using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Models
{
    public class SystemVariable
    {
        /// <summary>
        /// The variable Id.
        /// </summary>
        public long Id { get; private set; }

        /// <summary>
        /// The variable name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The variable value.
        /// </summary>
        public JsonNode Value { get; set; }

        public SystemVariable(string name, JsonNode value)
        {
            Name = name;
            Value = value;
        }

        SystemVariable() { } // For serializers.
    }
}
