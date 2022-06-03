using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Extensions
{
    public static class JsonNodeExtensions
    {
        /// <summary>
        /// Merge two Json objects created a new json object. If right value is null, it will not be merged.
        /// Keys are case sensitive.
        /// </summary>
        /// <param name="left"></param>
        /// <param name="right"></param>
        /// <returns></returns>
        public static JsonObject Merge(this JsonObject left, JsonObject? right)
        {
            if (right is null) 
                return left;
            var result = JsonNode.Parse(left.ToJsonString()).AsObject(); // Clone json object.

            foreach (var propRight in right)
            {
                if (left.ContainsKey(propRight.Key) && propRight.Value is not null)
                {
                    result[propRight.Key] = CloneJsonNode(propRight.Value);
                }
                else
                    result.Add(propRight.Key, CloneJsonNode(propRight.Value));
            }

            return result;
        }

        public static JsonNode CloneJsonNode(this JsonNode? jsonNode) => jsonNode switch
                    {
                        JsonObject jO => JsonNode.Parse(jO.ToJsonString()),
                        JsonArray jA => JsonNode.Parse(jA.ToJsonString()),
                        JsonValue jV => JsonValue.Create(jV.GetValue<object>())
                    };
}
}
