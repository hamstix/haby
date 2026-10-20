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
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$ref", "$anchor", "$dynamicRef", "$dynamicAnchor", "$comment", "$defs",
        "type", "const", "enum", "multipleOf", "maximum", "exclusiveMaximum", "minimum", "exclusiveMinimum",
        "maxLength", "minLength", "pattern", "maxItems", "minItems", "uniqueItems", "maxContains", "minContains",
        "maxProperties", "minProperties", "required", "dependentRequired", "format",
        "prefixItems", "items", "contains", "additionalProperties", "properties", "patternProperties",
        "dependentSchemas", "propertyNames", "if", "then", "else", "allOf", "anyOf", "oneOf", "not",
        "unevaluatedItems", "unevaluatedProperties", "title", "description", "default", "deprecated",
        "readOnly", "writeOnly", "examples", "contentEncoding", "contentMediaType", "contentSchema"
    };
    private static readonly HashSet<string> SchemaMaps = new(StringComparer.Ordinal)
    { "$defs", "properties", "patternProperties", "dependentSchemas" };
    private static readonly HashSet<string> SchemaArrays = new(StringComparer.Ordinal)
    { "prefixItems", "allOf", "anyOf", "oneOf" };
    private static readonly HashSet<string> SchemaValues = new(StringComparer.Ordinal)
    {
        "items", "contains", "additionalProperties", "propertyNames", "if", "then", "else", "not",
        "unevaluatedItems", "unevaluatedProperties", "contentSchema"
    };
    private static readonly Lazy<JsonSchema> MetaSchema = new(() => JsonSchema.FromUri(Draft202012,
        new JsonSchema.Options(allowFileSystemAndHttpResolution: false)));
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
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                (document.RootElement.TryGetProperty("$schema"u8, out var dialect) &&
                 (dialect.ValueKind != JsonValueKind.String || dialect.GetString() != Draft202012)) ||
                !MetaSchema.Value.Validate(text))
                throw new ArgumentException("A registered schema must satisfy the Draft 2020-12 meta-schema.", nameof(text));
            RequireKnownKeywords(document.RootElement);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            _schemas.Add(text, JsonSchema.FromText(text, "https://haby.dev/schemas/extensions/" + hash,
                new JsonSchema.Options(allowFileSystemAndHttpResolution: false)));
        }
        catch (JsonException)
        {
            throw new ArgumentException("A registered schema must be valid JSON.", nameof(text));
        }
        catch (Corvus.Text.Json.RuntimeEvaluator.JsonSchemaCompilationException)
        {
            throw new ArgumentException("A registered schema contains an unresolved or invalid reference.", nameof(text));
        }
    }

    private static void RequireKnownKeywords(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.True || schema.ValueKind == JsonValueKind.False) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var keyword in schema.EnumerateObject())
        {
            if (!Keywords.Contains(keyword.Name) || !seen.Add(keyword.Name))
                throw new ArgumentException("A registered schema contains an unsupported or duplicate keyword.");
            if (SchemaMaps.Contains(keyword.Name))
            {
                foreach (var item in keyword.Value.EnumerateObject()) RequireKnownKeywords(item.Value);
            }
            else if (SchemaArrays.Contains(keyword.Name))
            {
                foreach (var item in keyword.Value.EnumerateArray()) RequireKnownKeywords(item);
            }
            else if (SchemaValues.Contains(keyword.Name))
                RequireKnownKeywords(keyword.Value);
        }
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
