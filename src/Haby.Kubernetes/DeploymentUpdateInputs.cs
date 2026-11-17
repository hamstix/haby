using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haby.Application;

namespace Haby.Kubernetes;

internal static class WorkloadUpdateInputs
{
    // Compare all selected inputs, including absence/tokens and exact Profile revisions.
    // Only the source revision and adapter-owned image declaration may differ. Layout
    // origins are derived from these declarations, not independently selected inputs.
    internal static bool Same(DurablePlanningInputs previous, DurablePlanningInputs target)
    {
        previous.Validate(); target.Validate();
        if (previous.Environment != target.Environment || previous.EnvironmentKey != target.EnvironmentKey ||
            previous.Revision.ApplicationRecordId != target.Revision.ApplicationRecordId ||
            previous.Providers.Entries.Count != target.Providers.Entries.Count ||
            previous.Providers.Entries.Any(x => !target.Providers.Entries.TryGetValue(x.Key, out var value) || x.Value != value)) return false;
        return Semantic(ConfigurationPlanningSnapshotCodec.Encode(previous.Configuration!)) ==
            Semantic(ConfigurationPlanningSnapshotCodec.Encode(target.Configuration!));
    }

    private static string Semantic(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root.Remove("revision");
        var manifest = root["manifest"]!;
        foreach (var component in manifest["spec"]!["components"]!.AsObject())
            if (component.Value?["workloads"] is JsonObject workloads)
                foreach (var workload in workloads)
                    workload.Value!["properties"]!["members"]!.AsObject().Remove("image");
        // Preserve renderer/formatter/default pins and expanded layouts: selected
        // implementation or effective configuration changes are meaningful inputs.
        return Canonical(root);
    }

    internal static string Canonical(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(node, writer);
        return Encoding.UTF8.GetString(stream.ToArray());
        static void Write(JsonNode? value, Utf8JsonWriter writer)
        {
            if (value is JsonObject obj)
            {
                writer.WriteStartObject();
                foreach (var item in obj.OrderBy(x => x.Key, StringComparer.Ordinal)) { writer.WritePropertyName(item.Key); Write(item.Value, writer); }
                writer.WriteEndObject();
            }
            else if (value is JsonArray array) { writer.WriteStartArray(); foreach (var item in array) Write(item, writer); writer.WriteEndArray(); }
            else if (value is null) writer.WriteNullValue();
            else value.WriteTo(writer);
        }
    }
}
