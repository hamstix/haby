using System.Globalization;
using System.Text.Json.Nodes;
using k8s;
using k8s.Models;
using Haby.Configuration.Contracts;
using static Haby.Kubernetes.DeploymentIntents;

namespace Haby.Kubernetes;

/// <summary>SDK objects stop here. Haby evidence contains only the explicit owned projection.</summary>
internal static class DeploymentObjects
{
    internal static JsonObject Read(V1Deployment deployment) => JsonNode.Parse(KubernetesJson.Serialize(deployment))!.AsObject();
    internal static string Pointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static JsonObject Labels(DeploymentIntent i) => new() { [OwnerKey] = i.Owner.ToString("N"), [ManagedKey] = i.ManagedWorkload.ToString("N") };
    private static JsonObject Markers(DeploymentIntent i) => new() { [OperationKey] = i.Operation.ToString("N"), [GenerationKey] = i.TargetGeneration.ToString(CultureInfo.InvariantCulture) };
    private static JsonObject Strategy(DeploymentProjection p) => p.Strategy == "Recreate" ? new() { ["type"] = "Recreate" } :
        new() { ["type"] = "RollingUpdate", ["rollingUpdate"] = new JsonObject { ["maxSurge"] = Bound(p.MaxSurge!), ["maxUnavailable"] = Bound(p.MaxUnavailable!) } };
    private static JsonNode Bound(string value) => value.EndsWith('%') ? JsonValue.Create(value)! : JsonValue.Create(int.Parse(value, CultureInfo.InvariantCulture))!;
    internal static V1Deployment Create(DeploymentIntent i)
    {
        var p = i.Target;
        var nodes = new JsonObject(); foreach (var (k, v) in p.NodeSelector) nodes[k] = v;
        var pulls = new JsonArray(); foreach (var name in p.ImagePullSecrets) pulls.Add(new JsonObject { ["name"] = name });
        var body = new JsonObject
        {
            ["apiVersion"] = "apps/v1",
            ["kind"] = "Deployment",
            ["metadata"] = new JsonObject { ["namespace"] = i.Namespace, ["name"] = i.Name, ["labels"] = Labels(i), ["annotations"] = Markers(i) },
            ["spec"] = new JsonObject
            {
                ["replicas"] = p.Replicas,
                ["minReadySeconds"] = p.MinReadySeconds,
                ["progressDeadlineSeconds"] = p.ProgressDeadlineSeconds,
                ["strategy"] = Strategy(p),
                ["selector"] = new JsonObject { ["matchLabels"] = Labels(i) },
                ["template"] = new JsonObject
                {
                    ["metadata"] = new JsonObject { ["labels"] = Labels(i) },
                    ["spec"] = new JsonObject { ["containers"] = new JsonArray(new JsonObject { ["name"] = p.Container, ["image"] = p.Image }), ["nodeSelector"] = nodes, ["imagePullSecrets"] = pulls },
                },
            },
        };
        if (p.Resources is not null)
            body["spec"]!["template"]!["spec"]!["containers"]![0]!["resources"] = DeploymentResources.Json(p.Resources);
        if (p.Runtime is { } runtime)
        {
            var spec = body["spec"]!["template"]!["spec"]!;
            spec["serviceAccountName"] = runtime.ServiceAccount;
            spec["automountServiceAccountToken"] = false;
            spec["volumes"] = new JsonArray(TokenVolume(runtime));
            var container = spec["containers"]![0]!;
            container["env"] = new JsonArray(BootstrapVariable(runtime));
            container["volumeMounts"] = new JsonArray(TokenMount());
        }
        return KubernetesJson.Deserialize<V1Deployment>(body.ToJsonString());
    }
    internal static void Ownership(JsonObject obj, DeploymentIntent i)
    {
        Need(String(obj, "metadata", "namespace") == i.Namespace && String(obj, "metadata", "name") == i.Name, "object-identity");
        foreach (var path in new[] { new[] { "metadata", "labels" }, new[] { "spec", "template", "metadata", "labels" } })
        {
            Need(At(obj, path) is JsonObject labels && String(labels, OwnerKey) == i.Owner.ToString("N") && String(labels, ManagedKey) == i.ManagedWorkload.ToString("N"), "ownership");
            Need(At(obj, path)?[OperationKey] is null && At(obj, path)?[GenerationKey] is null, "reserved-location");
        }
        Need(JsonNode.DeepEquals(At(obj, "spec", "selector"), new JsonObject { ["matchLabels"] = Labels(i) }), "selector-drift");
        Need(At(obj, "metadata", "annotations", OwnerKey) is null && At(obj, "metadata", "annotations", ManagedKey) is null &&
            Reserved.All(key => At(obj, "spec", "template", "metadata", "annotations", key) is null), "reserved-location");
        Text(String(obj, "metadata", "uid")!, 128); Text(String(obj, "metadata", "resourceVersion")!, 128);
    }
    internal static void Matches(JsonObject obj, DeploymentProjection p)
    {
        Need(At(obj, "spec", "template", "spec", "containers") is JsonArray, "container-identity");
        var containers = (JsonArray)At(obj, "spec", "template", "spec", "containers")!;
        Need(containers.All(c => c is JsonObject && !string.IsNullOrEmpty(String(c!, "name"))) && containers.Select(c => String(c!, "name")).Distinct(StringComparer.Ordinal).Count() == containers.Count, "container-identity");
        var found = containers.Where(c => String(c!, "name") == p.Container).ToArray();
        Need(found.Length == 1 && String(found[0]!, "image") == p.Image, "container-drift");
        DeploymentResources.Matches(found[0]!, p.Resources);
        Need(Number(At(obj, "spec", "replicas"), 1) == p.Replicas && Number(At(obj, "spec", "minReadySeconds"), 0) == p.MinReadySeconds &&
            Number(At(obj, "spec", "progressDeadlineSeconds"), 600) == p.ProgressDeadlineSeconds, "scalar-drift");
        var strategy = At(obj, "spec", "strategy"); var kind = strategy?["type"]?.GetValue<string>() ?? "RollingUpdate";
        Need(kind == p.Strategy && (kind == "Recreate" ? strategy?["rollingUpdate"] is null :
            BoundText(strategy?["rollingUpdate"]?["maxSurge"], "25%") == p.MaxSurge && BoundText(strategy?["rollingUpdate"]?["maxUnavailable"], "25%") == p.MaxUnavailable), "strategy-drift");
        foreach (var (k, v) in p.NodeSelector) Need(String(obj, "spec", "template", "spec", "nodeSelector", k) == v, "node-selector-drift");
        var pulls = Pulls(obj);
        foreach (var key in p.ImagePullSecrets) Need(pulls.Any(n => String(n!, "name") == key), "pull-secret-drift");
        if (p.Runtime is { } runtime) MatchesRuntime(obj, found[0]!, containers, runtime);
    }
    private static JsonObject BootstrapVariable(DeploymentRuntime runtime) => new() { ["name"] = RuntimeBootstrap.Variable, ["value"] = runtime.Bootstrap };
    private static JsonObject TokenMount() => new() { ["name"] = "haby-runtime-token", ["mountPath"] = "/var/run/haby/runtime", ["readOnly"] = true };
    private static JsonObject TokenVolume(DeploymentRuntime runtime) => new()
    {
        ["name"] = "haby-runtime-token",
        ["projected"] = new JsonObject
        {
            ["defaultMode"] = 420,
            ["sources"] = new JsonArray(new JsonObject
            {
                ["serviceAccountToken"] = new JsonObject
                { ["audience"] = runtime.Audience, ["expirationSeconds"] = 3600, ["path"] = "token" }
            })
        }
    };
    private static void MatchesRuntime(JsonObject obj, JsonNode main, JsonArray containers, DeploymentRuntime runtime)
    {
        var spec = At(obj, "spec", "template", "spec")!;
        Need(String(spec, "serviceAccountName") == runtime.ServiceAccount &&
            (At(spec, "serviceAccount") is null || String(spec, "serviceAccount") == runtime.ServiceAccount) &&
            At(spec, "automountServiceAccountToken")?.GetValue<bool>() == false, "runtime-account-drift");
        JsonArray Keyed(JsonNode node, string field, string key)
        {
            Need(At(node, field) is null or JsonArray, "runtime-list-shape");
            var items = At(node, field)?.AsArray() ?? new JsonArray();
            Need(items.All(x => x is JsonObject && !string.IsNullOrEmpty(String(x!, key))) &&
                items.Select(x => String(x!, key)).Distinct(StringComparer.Ordinal).Count() == items.Count, "runtime-list-identity"); return items;
        }
        var variables = Keyed(main, "env", "name");
        Need(At(main, "envFrom") is null && variables.Count(x => String(x!, "name") == RuntimeBootstrap.Variable) == 1 &&
            JsonNode.DeepEquals(variables.Single(x => String(x!, "name") == RuntimeBootstrap.Variable), BootstrapVariable(runtime)), "runtime-bootstrap-drift");
        var volumes = Keyed(spec, "volumes", "name");
        Need(volumes.Count(x => String(x!, "name") == "haby-runtime-token") == 1 &&
            JsonNode.DeepEquals(volumes.Single(x => String(x!, "name") == "haby-runtime-token"), TokenVolume(runtime)), "runtime-volume-drift");
        var mounts = Keyed(main, "volumeMounts", "mountPath");
        Need(mounts.Count(x => String(x!, "name") == "haby-runtime-token") == 1 &&
            JsonNode.DeepEquals(mounts.Single(x => String(x!, "name") == "haby-runtime-token"), TokenMount()), "runtime-mount-drift");
        foreach (var mount in mounts.Where(x => String(x!, "name") != "haby-runtime-token"))
        {
            var path = String(mount!, "mountPath")!;
            Need(path != "/" && path != "/var/run/haby/runtime" && !path.StartsWith("/var/run/haby/runtime/", StringComparison.Ordinal) &&
                !"/var/run/haby/runtime".StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal), "runtime-mount-collision");
        }
        var injected = containers.Where(x => !ReferenceEquals(x, main)).Concat(At(spec, "initContainers")?.AsArray() ?? []);
        foreach (var container in injected)
            Need(!Keyed(container!, "volumeMounts", "mountPath").Any(x => String(x!, "name") == "haby-runtime-token"), "runtime-token-injection");
    }
    internal static JsonArray Patch(JsonObject obj, DeploymentIntent i)
    {
        var old = i.LastConfirmed!; var target = i.Target;
        Need(old.Runtime is null && target.Runtime is null, "configured-update-unsupported");
        var patch = new JsonArray();
        void Add(string op, string path, JsonNode? value = null)
        {
            var item = new JsonObject { ["op"] = op, ["path"] = path }; if (op != "remove") item["value"] = value; patch.Add(item);
        }
        Add("test", "/metadata/uid", JsonValue.Create(i.ExpectedUid));
        Add("test", "/metadata/resourceVersion", JsonValue.Create(String(obj, "metadata", "resourceVersion")));
        var containers = At(obj, "spec", "template", "spec", "containers")!.AsArray();
        var index = Enumerable.Range(0, containers.Count).Single(n => String(containers[n]!, "name") == old.Container);
        Add("test", $"/spec/template/spec/containers/{index}/name", JsonValue.Create(old.Container));
        Add("add", $"/spec/template/spec/containers/{index}/image", JsonValue.Create(target.Image));
        DeploymentResources.Patch(containers[index]!, old.Resources, target.Resources, $"/spec/template/spec/containers/{index}/resources", Add);
        Add("add", "/spec/replicas", JsonValue.Create(target.Replicas));
        Add("add", "/spec/minReadySeconds", JsonValue.Create(target.MinReadySeconds));
        Add("add", "/spec/progressDeadlineSeconds", JsonValue.Create(target.ProgressDeadlineSeconds));
        if (At(obj, "spec", "strategy") is null) Add("add", "/spec/strategy", new JsonObject());
        Add("add", "/spec/strategy/type", JsonValue.Create(target.Strategy));
        if (target.Strategy == "Recreate")
        {
            if (At(obj, "spec", "strategy", "rollingUpdate") is not null) Add("remove", "/spec/strategy/rollingUpdate");
        }
        else
        {
            if (At(obj, "spec", "strategy", "rollingUpdate") is null) Add("add", "/spec/strategy/rollingUpdate", new JsonObject());
            Add("add", "/spec/strategy/rollingUpdate/maxSurge", Bound(target.MaxSurge!));
            Add("add", "/spec/strategy/rollingUpdate/maxUnavailable", Bound(target.MaxUnavailable!));
        }
        var nodes = At(obj, "spec", "template", "spec", "nodeSelector");
        foreach (var k in target.NodeSelector.Keys.Except(old.NodeSelector.Keys, StringComparer.Ordinal)) Need(nodes?[k] is null, "node-selector-collision");
        if (nodes is null && target.NodeSelector.Count > 0) Add("add", "/spec/template/spec/nodeSelector", new JsonObject());
        foreach (var k in old.NodeSelector.Keys.Except(target.NodeSelector.Keys, StringComparer.Ordinal)) Add("remove", "/spec/template/spec/nodeSelector/" + Pointer(k));
        foreach (var (k, v) in target.NodeSelector) Add("add", "/spec/template/spec/nodeSelector/" + Pointer(k), JsonValue.Create(v));
        var pulls = Pulls(obj);
        foreach (var k in target.ImagePullSecrets.Except(old.ImagePullSecrets, StringComparer.Ordinal)) Need(!pulls.Any(n => String(n!, "name") == k), "pull-secret-collision");
        var remove = Enumerable.Range(0, pulls.Count).Where(n => old.ImagePullSecrets.Contains(String(pulls[n]!, "name")!) && !target.ImagePullSecrets.Contains(String(pulls[n]!, "name")!)).OrderDescending();
        // Guards precede every mutation of the keyed list. Descending removals keep their indexes stable.
        foreach (var n in Enumerable.Range(0, pulls.Count).Where(n => old.ImagePullSecrets.Contains(String(pulls[n]!, "name")!)))
            Add("test", $"/spec/template/spec/imagePullSecrets/{n}/name", JsonValue.Create(String(pulls[n]!, "name")));
        foreach (var n in remove) Add("remove", $"/spec/template/spec/imagePullSecrets/{n}");
        if (At(obj, "spec", "template", "spec", "imagePullSecrets") is null && target.ImagePullSecrets.Length > 0) Add("add", "/spec/template/spec/imagePullSecrets", new JsonArray());
        foreach (var key in target.ImagePullSecrets.Except(old.ImagePullSecrets, StringComparer.Ordinal)) Add("add", "/spec/template/spec/imagePullSecrets/-", new JsonObject { ["name"] = key });
        if (At(obj, "metadata", "annotations") is null) Add("add", "/metadata/annotations", new JsonObject());
        foreach (var (key, value) in Markers(i)) Add("add", "/metadata/annotations/" + Pointer(key), value!.DeepClone());
        return patch;
    }
    internal static bool HasMarkers(JsonObject obj, DeploymentIntent i) => String(obj, "metadata", "annotations", OperationKey) == i.Operation.ToString("N") &&
        String(obj, "metadata", "annotations", GenerationKey) == i.TargetGeneration.ToString(CultureInfo.InvariantCulture);
    internal static DeploymentReceipt Receipt(JsonObject obj, DeploymentIntent i, ConfirmationSource source)
    {
        Ownership(obj, i); MatchesTarget(obj, i); Need(HasMarkers(obj, i), "operation-marker");
        var result = new DeploymentReceipt(3, i.Binding, i.Namespace, i.Name, i.Owner, i.ManagedWorkload,
            i.TargetGeneration, i.Operation, String(obj, "metadata", "uid")!, long.Parse(At(obj, "metadata", "generation")?.ToJsonString() ?? "0", CultureInfo.InvariantCulture), i.Target, source);
        Validate(result); return result;
    }
    private static void MatchesTarget(JsonObject obj, DeploymentIntent intent)
    {
        Matches(obj, intent.Target);
        if (intent.LastConfirmed is not { } previous) return;
        // Target membership alone cannot prove a requested removal. Only previously owned
        // entries participate; unrelated injected keys/names remain outside Haby ownership.
        var main = At(obj, "spec", "template", "spec", "containers")!.AsArray().Single(c => String(c!, "name") == intent.Target.Container)!;
        DeploymentResources.Removed(main, previous.Resources, intent.Target.Resources);
        var nodes = At(obj, "spec", "template", "spec", "nodeSelector")?.AsObject();
        foreach (var key in previous.NodeSelector.Keys.Except(intent.Target.NodeSelector.Keys, StringComparer.Ordinal))
            Need(nodes is null || !nodes.ContainsKey(key), "removed-node-selector-present");
        var pulls = Pulls(obj);
        foreach (var name in previous.ImagePullSecrets.Except(intent.Target.ImagePullSecrets, StringComparer.Ordinal))
            Need(!pulls.Any(item => String(item!, "name") == name), "removed-pull-secret-present");
    }
    private static JsonArray Pulls(JsonObject obj)
    {
        var value = At(obj, "spec", "template", "spec", "imagePullSecrets"); Need(value is null or JsonArray, "pull-secret-shape");
        var list = value?.AsArray() ?? new JsonArray();
        Need(list.All(n => n is JsonObject && !string.IsNullOrEmpty(String(n!, "name"))) && list.Select(n => String(n!, "name")).Distinct(StringComparer.Ordinal).Count() == list.Count, "pull-secret-identity");
        return list;
    }
    internal static JsonNode? At(JsonNode obj, params string[] path) { JsonNode? current = obj; foreach (var key in path) current = current?[key]; return current; }
    internal static string? String(JsonNode obj, params string[] path) => At(obj, path)?.GetValue<string>();
    private static int Number(JsonNode? node, int absent) => node?.GetValue<int>() ?? absent;
    private static string? BoundText(JsonNode? node, string absent) => node is null ? absent : node.ToJsonString().Trim('"');
}
