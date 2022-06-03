using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Hamstix.Haby.Shared.Api.WebUi.v1.SystemVariables
{
    public class SystemVariablePreviewViewModel
    {
        /// <summary>
        /// The variable Id.
        /// </summary>
        public long Id { get; private set; }

        /// <summary>
        /// The variable name.
        /// </summary>
        public string Name { get; set; } = default!;

        /// <summary>
        /// The variable value.
        /// </summary>
        public JsonNode Value { get; set; } = new JsonObject();
    }
}
