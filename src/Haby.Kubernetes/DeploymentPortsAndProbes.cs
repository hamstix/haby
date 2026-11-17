using System.Text.Json.Nodes;
using Haby.Kubernetes.Contracts;
using static Haby.Kubernetes.DeploymentIntents;
using static Haby.Kubernetes.DeploymentObjects;

namespace Haby.Kubernetes;

/// <summary>Closed named-port and probe ownership on the stable main container.</summary>
internal static class DeploymentPortsAndProbes
{
    internal static JsonObject Declaration(DeploymentProjection p)
    {
        Need(p.Ports is not null && p.Probes is not null && p.Ports.Count <= 32, "ports-probes-shape");
        var ports = new JsonObject();
        foreach (var (name, port) in p.Ports)
        {
            Need(port is not null, "port-shape");
            ports[name] = new JsonObject { ["containerPort"] = port.Number, ["protocol"] = port.Protocol };
        }
        var probes = new JsonObject();
        foreach (var (slot, probe) in Slots(p.Probes))
        {
            if (probe is null) continue;
            var node = ProbeJson(probe);
            probes[slot[..^5].ToLowerInvariant()] = node;
        }
        return new JsonObject { ["image"] = new JsonObject { ["reference"] = p.Image }, ["containerName"] = p.Container, ["ports"] = ports, ["probes"] = probes };
    }

    private static IEnumerable<(string Slot, Probe? Probe)> Slots(Probes probes)
    {
        yield return ("startupProbe", probes.Startup);
        yield return ("livenessProbe", probes.Liveness);
        yield return ("readinessProbe", probes.Readiness);
    }

    internal static JsonObject ProbeJson(Probe p)
    {
        Need(p.Port is not null && (p.Path is null ? p.Scheme is null : p.Scheme is "HTTP" or "HTTPS"), "probe-action");
        var result = new JsonObject
        {
            ["initialDelaySeconds"] = p.InitialDelaySeconds,
            ["periodSeconds"] = p.PeriodSeconds,
            ["timeoutSeconds"] = p.TimeoutSeconds,
            ["successThreshold"] = p.SuccessThreshold,
            ["failureThreshold"] = p.FailureThreshold
        };
        result[p.Path is null ? "tcpSocket" : "httpGet"] = p.Path is null
            ? new JsonObject { ["port"] = p.Port }
            : new JsonObject { ["port"] = p.Port, ["path"] = p.Path, ["scheme"] = p.Scheme };
        return result;
    }

    internal static void Create(JsonNode main, DeploymentProjection p)
    {
        if (p.Ports.Count > 0) main["ports"] = new JsonArray(p.Ports.Select(x => (JsonNode)PortJson(x.Key, x.Value)).ToArray());
        foreach (var (slot, probe) in Slots(p.Probes)) if (probe is not null) main[slot] = ProbeJson(probe);
    }

    private static JsonObject PortJson(string name, ContainerPort port) => new()
    { ["name"] = name, ["containerPort"] = port.Number, ["protocol"] = port.Protocol };

    private static JsonArray Ports(JsonNode main)
    {
        var value = At(main, "ports"); Need(value is null or JsonArray, "ports-shape");
        var ports = value?.AsArray() ?? [];
        Need(ports.All(x => x is JsonObject), "port-shape");
        var names = ports.Select(x => String(x!, "name")).Where(x => !string.IsNullOrEmpty(x)).ToArray();
        Need(names.Distinct(StringComparer.Ordinal).Count() == names.Length, "port-identity");
        return ports;
    }

    internal static void Matches(JsonNode main, DeploymentProjection p)
    {
        var observed = Ports(main);
        foreach (var (name, port) in p.Ports)
        {
            var entries = observed.Where(x => String(x!, "name") == name).ToArray();
            Need(entries.Length == 1, "port-drift");
            var actual = entries[0]!.DeepClone().AsObject();
            if (!actual.ContainsKey("protocol")) actual["protocol"] = "TCP";
            Need(JsonNode.DeepEquals(actual, PortJson(name, port)), "port-drift");
        }
        foreach (var (slot, probe) in Slots(p.Probes))
        {
            if (probe is null) continue;
            Need(At(main, slot) is JsonObject, "probe-drift");
            var actual = At(main, slot)!.DeepClone().AsObject();
            Default(actual, "initialDelaySeconds", 0); Default(actual, "periodSeconds", 10);
            Default(actual, "timeoutSeconds", 1); Default(actual, "successThreshold", 1); Default(actual, "failureThreshold", 3);
            if (actual["httpGet"] is JsonObject http)
            {
                if (!http.ContainsKey("scheme")) http["scheme"] = "HTTP";
                if (!http.ContainsKey("path")) http["path"] = "/";
            }
            Need(JsonNode.DeepEquals(actual, ProbeJson(probe)), "probe-drift");
        }
    }

    private static void Default(JsonObject obj, string name, int value)
    { if (!obj.ContainsKey(name)) obj[name] = value; }

    internal static void Patch(JsonNode main, DeploymentProjection old, DeploymentProjection target, string path, Action<string, string, JsonNode?> add)
    {
        var ports = Ports(main);
        foreach (var name in target.Ports.Keys.Except(old.Ports.Keys, StringComparer.Ordinal))
            Need(!ports.Any(x => String(x!, "name") == name), "port-collision");
        // Resolve indexes in the just-read named list, guard all old entries before any removal.
        var owned = Enumerable.Range(0, ports.Count).Where(n => old.Ports.ContainsKey(String(ports[n]!, "name") ?? "")).ToArray();
        foreach (var n in owned) add("test", $"{path}/ports/{n}/name", JsonValue.Create(String(ports[n]!, "name")));
        foreach (var n in owned)
        {
            var name = String(ports[n]!, "name")!;
            if (!target.Ports.TryGetValue(name, out var next)) continue;
            add("add", $"{path}/ports/{n}/containerPort", JsonValue.Create(next.Number));
            add("add", $"{path}/ports/{n}/protocol", JsonValue.Create(next.Protocol));
        }
        foreach (var n in owned.Where(n => !target.Ports.ContainsKey(String(ports[n]!, "name")!)).OrderDescending())
            add("remove", $"{path}/ports/{n}", null);
        if (At(main, "ports") is null && target.Ports.Count > 0) add("add", path + "/ports", new JsonArray());
        foreach (var name in target.Ports.Keys.Except(old.Ports.Keys, StringComparer.Ordinal))
            add("add", path + "/ports/-", PortJson(name, target.Ports[name]));
        foreach (var ((slot, previous), (_, next)) in Slots(old.Probes).Zip(Slots(target.Probes)))
        {
            if (previous is null && next is not null) Need(!main.AsObject().ContainsKey(slot), "probe-collision");
            if (next is not null) add("add", path + "/" + slot, ProbeJson(next));
            else if (previous is not null) add("remove", path + "/" + slot, null);
        }
    }

    internal static void Removed(JsonNode main, DeploymentProjection old, DeploymentProjection target)
    {
        var ports = Ports(main);
        foreach (var name in old.Ports.Keys.Except(target.Ports.Keys, StringComparer.Ordinal))
            Need(!ports.Any(x => String(x!, "name") == name), "removed-port-present");
        foreach (var ((slot, previous), (_, next)) in Slots(old.Probes).Zip(Slots(target.Probes)))
            if (previous is not null && next is null) Need(!main.AsObject().ContainsKey(slot), "removed-probe-present");
    }
}
