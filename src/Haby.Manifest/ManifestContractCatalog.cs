using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Corvus.Text.Json.Validator;
using Haby.Domain;

namespace Haby.Manifest;

/// <summary>Exact metadata used to validate manifests, independent of installed implementations.</summary>
public sealed record ResourceTypeDefinition(ResourceTypeId Id, string PropertiesSchema, string? BindingOptionsSchema = null);
public sealed record WorkloadTypeDefinition(WorkloadTypeId Id, string PropertiesSchema);
public sealed record OutputContractDefinition(OutputContractId Id, string? BindingOptionsSchema = null);
public sealed record ConfigurationRendererDefinition(ConfigurationRendererId Id);

/// <summary>An immutable snapshot of registered, exact contract definitions.</summary>
public sealed class ManifestContractCatalog
{
    private readonly Dictionary<ResourceTypeId, ResourceTypeDefinition> _resources = [];
    private readonly Dictionary<WorkloadTypeId, WorkloadTypeDefinition> _workloads = [];
    private readonly Dictionary<OutputContractId, OutputContractDefinition> _outputs = [];
    private readonly HashSet<ConfigurationRendererId> _renderers = [];
    private readonly Dictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);

    public ManifestContractCatalog(
        IEnumerable<ResourceTypeDefinition> resources,
        IEnumerable<WorkloadTypeDefinition> workloads,
        IEnumerable<OutputContractDefinition> outputs,
        IEnumerable<ConfigurationRendererDefinition> renderers)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(workloads);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(renderers);

        foreach (var definition in resources)
        {
            RequireSchema(definition.PropertiesSchema);
            if (definition.BindingOptionsSchema is not null) RequireSchema(definition.BindingOptionsSchema);
            Register(_resources, definition.Id, definition, (a, b) =>
                Equivalent(a.PropertiesSchema, b.PropertiesSchema) && Equivalent(a.BindingOptionsSchema, b.BindingOptionsSchema));
        }
        foreach (var definition in workloads)
        {
            RequireSchema(definition.PropertiesSchema);
            Register(_workloads, definition.Id, definition, (a, b) => Equivalent(a.PropertiesSchema, b.PropertiesSchema));
        }
        foreach (var definition in outputs)
        {
            if (definition.BindingOptionsSchema is not null) RequireSchema(definition.BindingOptionsSchema);
            Register(_outputs, definition.Id, definition, (a, b) => Equivalent(a.BindingOptionsSchema, b.BindingOptionsSchema));
        }
        foreach (var definition in renderers)
            _renderers.Add(definition.Id);
    }

    public bool TryGet(ResourceTypeId id, out ResourceTypeDefinition definition) => _resources.TryGetValue(id, out definition!);
    public bool TryGet(WorkloadTypeId id, out WorkloadTypeDefinition definition) => _workloads.TryGetValue(id, out definition!);
    public bool TryGet(OutputContractId id, out OutputContractDefinition definition) => _outputs.TryGetValue(id, out definition!);
    public bool Contains(ConfigurationRendererId id) => _renderers.Contains(id);

    internal JsonSchema GetSchema(string text) => _schemas[text];

    private void RequireSchema(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (_schemas.ContainsKey(text)) return;
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A registered schema must be a JSON Schema object.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        _schemas.Add(text, JsonSchema.FromText(text, "https://haby.dev/schemas/extensions/" + hash,
            new JsonSchema.Options(allowFileSystemAndHttpResolution: false)));
    }

    private static bool Equivalent(string? first, string? second)
    {
        if (first is null || second is null) return first == second;
        using var a = JsonDocument.Parse(first);
        using var b = JsonDocument.Parse(second);
        return JsonElement.DeepEquals(a.RootElement, b.RootElement);
    }

    private static void Register<TKey, TDefinition>(Dictionary<TKey, TDefinition> definitions, TKey id,
        TDefinition definition, Func<TDefinition, TDefinition, bool> equivalent) where TKey : notnull
    {
        if (definitions.TryGetValue(id, out var existing))
        {
            if (!equivalent(existing, definition))
                throw new InvalidOperationException("Conflicting registered contract definition: " + id);
            return;
        }
        definitions.Add(id, definition);
    }
}
