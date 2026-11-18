using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Haby.Kubernetes;

// Only outbound JSON Patch bytes. Stored codecs and canonical input hashes do not use this.
internal static class DeploymentPatchTransport
{
    internal static string Serialize(JsonArray patch)
    {
        var json = new StringBuilder();
        Write(patch, json);
        return json.ToString();
    }
    private static void Write(JsonNode? node, StringBuilder json)
    {
        switch (node)
        {
            case null: json.Append("null"); break;
            case JsonArray array:
                json.Append('[');
                for (var index = 0; index < array.Count; index++)
                { if (index != 0) json.Append(','); Write(array[index], json); }
                json.Append(']');
                break;
            case JsonObject obj:
                json.Append('{'); var first = true;
                foreach (var (key, value) in obj)
                {
                    if (!first) json.Append(','); first = false;
                    String(key, json); json.Append(':'); Write(value, json);
                }
                json.Append('}');
                break;
            case JsonValue value when value.TryGetValue<string>(out var text): String(text, json); break;
            default: json.Append(node.ToJsonString()); break;
        }
    }
    private static void String(string value, StringBuilder json)
    {
        // The API server's JSON Patch implementation compares compact scalar bytes.
        // Match Go encoding/json string tokens, including HTML and Unicode separators,
        // without changing decoded strings or removing any test operation.
        json.Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    if (rune.Value < 0x20 || rune.Value is '<' or '>' or '&' or 0x2028 or 0x2029)
                        json.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                    else json.Append(rune.ToString());
                    break;
            }
        }
        json.Append('"');
    }
}
