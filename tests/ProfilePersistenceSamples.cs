using System.Text;
using System.Text.Json.Nodes;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Tests;

internal static class ProfilePersistenceSamples
{
    internal const string Json = """
        {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"layout","revision":1},"spec":{
          "documents":{"settings":{"fileName":"appsettings.json","format":"json","template":{"seed":[null,true,1.234567890123456789012345678901e999,{"kind":"SecretReference","value":"ordinary"}]}}},
          "staticContributions":{"logging":{"documentRef":"settings","path":"/Logging","value":{"Level":"Information","tiny":1e-999}},
            "telemetry":{"documentRef":"settings","path":"/Telemetry","value":{"Enabled":true},"secretProjections":[
              {"path":"/Auth/Token","format":"string","secretRef":{"name":"future-token","version":3}},
              {"path":"/Otlp","format":"json","secretRef":{"name":"future-otel","version":7}}]}},
          "bindingRules":{"connection":{"slot":"main","rendererRef":"sample.connection/v1","options":{"timeout":30,"nested":{"mode":"safe"}},
            "documentProjections":[{"documentRef":"settings","path":"/Connections/Main"},{"documentRef":"other","path":"/Link"}]}}
        }}
        """;
    internal static JsonObject Node(bool secrets = true)
    {
        var node = JsonNode.Parse(Json)!.AsObject();
        if (!secrets) node["spec"]!["staticContributions"]!.AsObject().Remove("telemetry");
        return node;
    }
    internal static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
    internal static ConfigurationProfileRevision Parse(JsonNode node) => ProfilePayloadParser.ParseProfile(Bytes(node)).Declaration
        ?? throw new InvalidOperationException("Invalid test Profile.");
    internal static JsonObject Equivalent(JsonObject node)
    {
        var copy = node.DeepClone().AsObject();
        Reverse(copy["spec"]!.AsObject());
        Reverse(copy["spec"]!["staticContributions"]!.AsObject());
        var contribution = copy["spec"]!["staticContributions"]!["logging"]!.AsObject();
        contribution["secretProjections"] = new JsonArray();
        Reverse(contribution["value"]!.AsObject());
        var rule = copy["spec"]!["bindingRules"]!["connection"]!;
        var projections = rule["documentProjections"]!.AsArray();
        rule["documentProjections"] = new JsonArray(projections.Reverse().Select(x => x!.DeepClone()).ToArray());
        if (copy["spec"]!["staticContributions"]!["telemetry"] is JsonObject telemetry)
            telemetry["secretProjections"] = new JsonArray(telemetry["secretProjections"]!.AsArray().Reverse().Select(x => x!.DeepClone()).ToArray());
        return copy;
    }
    private static void Reverse(JsonObject obj)
    {
        var pairs = obj.Reverse().ToArray(); obj.Clear();
        foreach (var pair in pairs) obj.Add(pair.Key, pair.Value);
    }
    internal static readonly DateTimeOffset Time = new(2026, 11, 5, 15, 0, 0, TimeSpan.Zero);
    internal sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
