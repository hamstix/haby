using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Corvus.Text.Json.Validator;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Kubernetes.Contracts;

/// <summary>Closed portable decoding. No cluster, alias resolution or credential access.</summary>
public static class KubernetesContract
{
    public static WorkloadTypeId WorkloadType { get; } = WorkloadTypeId.Create("kubernetes.workload/v1alpha1");
    public static string WorkloadSchema { get; } = Load("workload");
    public static string PlacementSchema { get; } = Load("placement");
    public static WorkloadTypeDefinition Definition { get; } = new(WorkloadType, WorkloadSchema);
    private static readonly Lazy<JsonSchema> WorkloadValidator = new(() => Schema(WorkloadSchema));
    private static readonly Lazy<JsonSchema> PlacementValidator = new(() => Schema(PlacementSchema));

    public static KubernetesWorkload Decode(NormalizedWorkload workload)
    {
        Require(workload.Type == WorkloadType, "workload.type");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, workload.Properties);
        return DecodeWorkload(stream.ToArray());
    }

    public static KubernetesWorkload DecodeWorkload(ReadOnlyMemory<byte> bytes)
    {
        var root = Read(bytes, WorkloadValidator.Value);
        var image = root.GetProperty("image"u8);
        WorkloadImage parsedImage;
        if (image.TryGetProperty("reference"u8, out var complete))
        {
            ImageReference(complete.GetString()!);
            parsedImage = new WorkloadImage.Complete(complete.GetString()!);
        }
        else parsedImage = new WorkloadImage.Composed(Text(image, "name")!, Text(image, "tag"), image.TryGetProperty("tagFrom"u8, out _));
        var ports = Map(root, "ports", p => new ContainerPort(Int(p, "containerPort")!.Value, Text(p, "protocol") ?? "TCP"));
        foreach (var name in ports.Keys) Require(name.Length <= 15 && Match(name, "[a-z](?:[a-z0-9-]*[a-z0-9])?"), "port.name");
        var env = Map<EnvironmentValue>(root, "env", e => e.TryGetProperty("value"u8, out var literal)
            ? new EnvironmentValue.Literal(literal.GetString()!)
            : e.TryGetProperty("configMapKey"u8, out var cm) ? new EnvironmentValue.ConfigMap(Key(cm))
            : new EnvironmentValue.Secret(Key(e.GetProperty("secretKey"u8))));
        foreach (var name in env.Keys) Require(Match(name, "[A-Za-z_][A-Za-z0-9_]*"), "env.name");
        var volumes = Map(root, "volumes", v =>
        {
            var item = v.EnumerateObject().Single();
            var readOnly = item.Name != "persistentVolumeClaim" || Bool(item.Value, "readOnly") == true;
            return new ExistingVolume(item.Name, Dns(Text(item.Value, "name")!), Bool(item.Value, "optional") ?? false, readOnly);
        });
        foreach (var name in volumes.Keys) Label(name);
        var mounts = Items(root, "mounts").Select(m =>
        {
            var volume = Text(m, "volume")!;
            Require(volumes.ContainsKey(volume), "mount.volume");
            var path = Text(m, "mountPath")!; MountPath(path, true);
            var sub = Text(m, "subPath"); if (sub is not null) MountPath(sub, false);
            var readOnly = Bool(m, "readOnly") ?? volumes[volume].ReadOnly;
            Require(!volumes[volume].ReadOnly || readOnly, "mount.read-only");
            return new VolumeMount(volume, path, readOnly, sub);
        }).ToImmutableArray();
        Require(mounts.Select(x => x.MountPath).Distinct(StringComparer.Ordinal).Count() == mounts.Length, "mount.duplicate");
        var routes = Items(root, "routes").Select(r => new Route(Text(r, "hostLabel")!, Text(r, "path")!, Text(r, "port")!, Text(r, "pathType") ?? "Prefix")).ToImmutableArray();
        Require(routes.Select(x => (x.HostLabel, x.Path)).Distinct().Count() == routes.Length, "route.duplicate");
        foreach (var r in routes) { Label(r.HostLabel); Port(ports, r.Port); HttpPath(r.Path); }
        var probes = root.TryGetProperty("probes"u8, out var ps)
            ? new Probes(Probe(ps, "startup", ports), Probe(ps, "liveness", ports), Probe(ps, "readiness", ports)) : new Probes(null, null, null);
        var documents = root.TryGetProperty("configuration"u8, out var config)
            ? config.GetProperty("documents"u8).EnumerateArray().Select(d => ManifestLocalId.Create(d.GetString()!)).ToImmutableArray() : [];
        var nameOverride = Text(root, "nameOverride"); if (nameOverride is not null) Dns(nameOverride);
        return new(parsedImage, Label(Text(root, "containerName") ?? "app"), nameOverride, ports, env, probes, volumes, mounts,
            routes, documents, root.TryGetProperty("overrides"u8, out var overrides) ? Settings(overrides) : EmptySettings);
    }

    public static KubernetesPlacement DecodePlacement(ReadOnlyMemory<byte> bytes)
    {
        var root = Read(bytes, PlacementValidator.Value);
        var metadata = root.GetProperty("metadata"u8); var spec = root.GetProperty("spec"u8);
        var registry = Text(spec, "registry"); if (registry is not null) Registry(registry);
        var selectors = Map(spec, "nodeSelector", v => v.GetString()!);
        foreach (var (key, value) in selectors)
        {
            var parts = key.Split('/'); Require(parts.Length is 1 or 2, "node-selector.key");
            if (parts.Length == 2) Dns(parts[0]);
            Require(Match(parts[^1], "[A-Za-z0-9](?:[A-Za-z0-9_.-]{0,61}[A-Za-z0-9])?"), "node-selector.key");
            Require(value.Length == 0 || Match(value, "[A-Za-z0-9](?:[A-Za-z0-9_.-]{0,61}[A-Za-z0-9])?"), "node-selector.value");
        }
        var secrets = Items(spec, "imagePullSecrets").Select(x => Dns(x.GetString()!)).ToImmutableArray();
        return new(new(ManifestLocalId.Create(Text(metadata, "name")!), Int(metadata, "revision")!.Value), Label(Text(spec, "namespace")!),
            registry, Text(spec, "repositoryPrefix"), selectors, secrets, Settings(spec));
    }

    internal static readonly OperationalSettings EmptySettings = new(null, null, null, null, null, null, null, null);
    private static OperationalSettings Settings(JsonElement root)
    {
        RolloutStrategy? strategy = null;
        if (root.TryGetProperty("strategy"u8, out var s))
        {
            var type = Text(s, "type")!;
            var surge = type == "Recreate" ? null : Bound(s, "maxSurge");
            var unavailable = type == "Recreate" ? null : Bound(s, "maxUnavailable");
            Require(type == "Recreate" || surge!.Value != 0 || unavailable!.Value != 0, "strategy.zero");
            strategy = new(type, surge, unavailable);
        }
        ResourceRequirements? resources = null;
        if (root.TryGetProperty("resources"u8, out var r))
        {
            resources = new(Quantities(r, "requests"), Quantities(r, "limits"));
            if (resources.Requests is { } requests && resources.Limits is { } limits)
            {
                Require(requests.Cpu is null || limits.Cpu is null || Cpu(requests.Cpu) <= Cpu(limits.Cpu), "resources.cpu-order");
                Require(requests.Memory is null || limits.Memory is null || Memory(requests.Memory) <= Memory(limits.Memory), "resources.memory-order");
            }
        }
        IngressPolicy? ingress = null;
        if (root.TryGetProperty("ingress"u8, out var i))
        {
            ingress = new(Bool(i, "enabled")!.Value, Text(i, "className"), Text(i, "domain"), Text(i, "tlsSecret"));
            if (ingress.Enabled) { Dns(ingress.ClassName!); Dns(ingress.Domain!); }
            if (ingress.TlsSecret is not null) Dns(ingress.TlsSecret);
        }
        return new(Int(root, "replicas"), strategy, Int(root, "minReadySeconds"), Int(root, "rolloutSeconds"), resources,
            root.TryGetProperty("service"u8, out var service) ? Bool(service, "enabled") : null,
            root.TryGetProperty("headlessService"u8, out var headless) ? Bool(headless, "enabled") : null, ingress);
    }
    private static ResourceQuantities? Quantities(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var q)) return null;
        var cpu = Text(q, "cpu"); var memory = Text(q, "memory");
        if (cpu is not null) _ = Cpu(cpu); if (memory is not null) _ = Memory(memory);
        return new(cpu, memory);
    }
    private static decimal Cpu(string value)
    {
        var milli = value.EndsWith('m');
        Require(decimal.TryParse(milli ? value[..^1] : value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number), "resources.cpu");
        if (milli) number /= 1000;
        Require(number > 0 && number <= 10000, "resources.cpu"); return number;
    }
    private static decimal Memory(string value)
    {
        var suffix = value.Length >= 2 ? value[^2..] : "";
        var power = suffix switch { "Ki" => 1, "Mi" => 2, "Gi" => 3, "Ti" => 4, _ => 0 };
        Require(decimal.TryParse(power == 0 ? value : value[..^2], NumberStyles.None, CultureInfo.InvariantCulture, out var number), "resources.memory");
        for (var j = 0; j < power; j++) { Require(number <= 1125899906842624m / 1024, "resources.memory"); number *= 1024; }
        Require(number > 0 && number <= 1125899906842624m, "resources.memory"); return number;
    }
    private static RolloutBound Bound(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return new(25, true);
        return value.ValueKind == JsonValueKind.String ? new(int.Parse(value.GetString()![..^1], CultureInfo.InvariantCulture), true)
            : new(checked((int)value.GetDecimal()), false);
    }
    private static Probe? Probe(JsonElement root, string name, ImmutableDictionary<string, ContainerPort> ports)
    {
        if (!root.TryGetProperty(name, out var p)) return null;
        var http = p.TryGetProperty("httpGet"u8, out var action); if (!http) action = p.GetProperty("tcpSocket"u8);
        var port = Text(action, "port")!; Port(ports, port);
        var path = Text(action, "path"); if (path is not null) HttpPath(path);
        var success = Int(p, "successThreshold") ?? 1;
        Require(name == "readiness" || success == 1, "probe.success-threshold");
        return new(port, path, http ? Text(action, "scheme") ?? "HTTP" : null, Int(p, "initialDelaySeconds") ?? 0,
            Int(p, "periodSeconds") ?? 10, Int(p, "timeoutSeconds") ?? 1, success, Int(p, "failureThreshold") ?? 3);
    }
    private static ObjectKey Key(JsonElement key) => new(Dns(Text(key, "name")!), Text(key, "key")!, Bool(key, "optional") ?? false);
    private static void Port(ImmutableDictionary<string, ContainerPort> ports, string name) => Require(ports.TryGetValue(name, out var port) && port.Protocol == "TCP", "port.reference");
    private static void HttpPath(string path) => Require(path.StartsWith('/') && !path.Any(char.IsControl) && !path.Contains('?') && !path.Contains('#'), "http.path");
    private static void MountPath(string path, bool absolute)
    {
        Require(path.StartsWith('/') == absolute && !path.Contains('\\') && !path.Any(char.IsControl), "mount.path");
        Require((absolute ? path[1..] : path).Split('/').All(x => x.Length != 0 && x is not "." and not ".."), "mount.path");
    }
    private static string Label(string value)
    {
        Require(value.Length is > 0 and <= 63 && Match(value, "[a-z0-9](?:[a-z0-9-]*[a-z0-9])?"), "name.label"); return value;
    }
    internal static string Dns(string value)
    {
        Require(value.Length <= 253 && value.Split('.').All(x => x.Length > 0 && Match(x, "[a-z0-9](?:[a-z0-9-]*[a-z0-9])?")), "name.dns"); return value;
    }
    internal static void Registry(string registry)
    {
        var parts = registry.Split(':'); Require(parts.Length is 1 or 2, "image.registry"); Dns(parts[0]);
        if (parts.Length == 2) Require(int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535, "image.registry-port");
    }
    internal static void Tag(string tag) => Require(Match(tag, "[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}"), "image.tag");
    internal static void ImageReference(string value)
    {
        Require(value.Length <= 512, "image.reference");
        var slash = value.IndexOf('/'); Require(slash > 0, "image.reference"); Registry(value[..slash]);
        var rest = value[(slash + 1)..]; var digest = rest.IndexOf('@'); string path;
        if (digest >= 0) { path = rest[..digest]; Require(Match(rest[(digest + 1)..], "sha256:[a-f0-9]{64}"), "image.digest"); }
        else { var colon = rest.LastIndexOf(':'); Require(colon > 0, "image.explicit-tag"); path = rest[..colon]; Tag(rest[(colon + 1)..]); }
        Require(path.Length <= 256 && Match(path, "[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*"), "image.path");
    }
    internal static bool Match(string value, string pattern) => Regex.IsMatch(value, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static void Require(bool condition, string code) { if (!condition) throw new WorkloadContractException(code); }
    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static bool? Bool(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetBoolean() : null;
    private static int? Int(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? checked((int)value.GetDecimal()) : null;
    private static ImmutableDictionary<string, T> Map<T>(JsonElement root, string name, Func<JsonElement, T> read) => root.TryGetProperty(name, out var map)
        ? map.EnumerateObject().ToImmutableDictionary(x => x.Name, x => read(x.Value), StringComparer.Ordinal) : ImmutableDictionary<string, T>.Empty;
    private static IEnumerable<JsonElement> Items(JsonElement root, string name) => root.TryGetProperty(name, out var list) ? list.EnumerateArray() : [];
    private static JsonElement Read(ReadOnlyMemory<byte> bytes, JsonSchema schema)
    {
        Require(bytes.Length <= 1048576, "contract.size");
        try
        {
            var root = JsonSerializer.Deserialize(bytes.Span, KubernetesJsonContext.Default.JsonElement);
            NoDuplicates(root);
            using var collector = Corvus.Text.Json.JsonSchemaResultsCollector.Create(Corvus.Text.Json.JsonSchemaResultsLevel.Detailed, 20000);
            Require(schema.Validate(bytes.ToArray(), collector), "contract.schema"); return root;
        }
        catch (JsonException) { throw new WorkloadContractException("contract.json"); }
    }
    private static void NoDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) { Require(names.Add(p.Name), "contract.duplicate"); NoDuplicates(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) NoDuplicates(item);
    }
    private static JsonSchema Schema(string text) => JsonSchema.FromText(text, "https://haby.dev/kubernetes/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))), new JsonSchema.Options(allowFileSystemAndHttpResolution: false));
    private static string Load(string name)
    {
        using var stream = typeof(KubernetesContract).Assembly.GetManifestResourceStream("Haby.Kubernetes.Contracts." + name + ".json")!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private static void Write(Utf8JsonWriter w, ConfigurationValue value)
    {
        switch (value)
        {
            case ConfigurationObject o: w.WriteStartObject(); foreach (var (key, child) in o.Members.OrderBy(x => x.Key, StringComparer.Ordinal)) { w.WritePropertyName(key); Write(w, child); } w.WriteEndObject(); break;
            case ConfigurationArray a: w.WriteStartArray(); foreach (var child in a.Items) Write(w, child); w.WriteEndArray(); break;
            case ConfigurationString s: w.WriteStringValue(s.Value); break;
            case ConfigurationNumber n: w.WriteRawValue(n.CanonicalText); break;
            case ConfigurationBoolean b: w.WriteBooleanValue(b.Value); break;
            case ConfigurationNull: w.WriteNullValue(); break;
            default: throw new WorkloadContractException("contract.nonportable-value");
        }
    }
}

[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
internal partial class KubernetesJsonContext : JsonSerializerContext;
