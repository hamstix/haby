using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haby.Manifest;

/// <summary>The transport-independent request shape. Slice 3 validates and dispatches Manifest.</summary>
public sealed class ImportApplicationEnvelope
{
    [JsonRequired] public required string ApplicationVersion { get; init; }
    [JsonRequired] public required JsonElement Manifest { get; init; }
}

public sealed class ApplicationManifest
{
    [JsonPropertyName("$schema")] public string? Schema { get; init; }
    [JsonRequired] public required string ApiVersion { get; init; }
    [JsonRequired] public required string Kind { get; init; }
    [JsonRequired] public required ManifestMetadata Metadata { get; init; }
    [JsonRequired] public required ManifestSpec Spec { get; init; }
}

public sealed class ManifestMetadata
{
    private Dictionary<string, string>? _labels;

    [JsonRequired] public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public Dictionary<string, string> Labels { get => _labels ??= []; init => _labels = value; }
}

public sealed class ManifestSpec
{
    private Dictionary<string, ParameterDefinition>? _parameters;
    private Dictionary<string, ResourceDeclaration>? _resources;
    private Dictionary<string, ResourceExport>? _exports;
    private Dictionary<string, ComponentDeclaration>? _components;

    public Dictionary<string, ParameterDefinition> Parameters { get => _parameters ??= []; init => _parameters = value; }
    public Dictionary<string, ResourceDeclaration> Resources { get => _resources ??= []; init => _resources = value; }
    public Dictionary<string, ResourceExport> Exports { get => _exports ??= []; init => _exports = value; }
    public Dictionary<string, ComponentDeclaration> Components { get => _components ??= []; init => _components = value; }
}

public sealed class ResourceDeclaration
{
    private JsonElement _properties;
    private ResourceLifecycle? _lifecycle;

    [JsonRequired] public required string Type { get; init; }
    [JsonRequired] public required string ProviderRef { get; init; }
    public JsonElement Properties { get => _properties.ValueKind == JsonValueKind.Undefined ? JsonDefaults.EmptyObject : _properties; init => _properties = value; }
    public ResourceLifecycle Lifecycle { get => _lifecycle ??= new(); init => _lifecycle = value; }
}

public sealed class ResourceLifecycle
{
    private string? _deletionPolicy;

    public string DeletionPolicy { get => _deletionPolicy ?? "retain"; init => _deletionPolicy = value; }
}

public sealed class ResourceExport
{
    [JsonRequired] public required string ResourceRef { get; init; }
    [JsonRequired] public required string Contract { get; init; }
    public string? AccessProfile { get; init; }
}

public sealed class ComponentDeclaration
{
    private Dictionary<string, string>? _labels;
    private Dictionary<string, BindingDeclaration>? _bindings;
    private Dictionary<string, ConfigurationDocument>? _documents;
    private Dictionary<string, WorkloadDeclaration>? _workloads;

    public string? DisplayName { get; init; }
    public Dictionary<string, string> Labels { get => _labels ??= []; init => _labels = value; }
    public Dictionary<string, BindingDeclaration> Bindings { get => _bindings ??= []; init => _bindings = value; }
    public Dictionary<string, ConfigurationDocument> Documents { get => _documents ??= []; init => _documents = value; }
    public Dictionary<string, WorkloadDeclaration> Workloads { get => _workloads ??= []; init => _workloads = value; }
}

public sealed class BindingDeclaration
{
    private JsonElement _options;
    private List<DocumentProjection>? _documentProjections;

    [JsonRequired] public required BindingTarget Target { get; init; }
    public JsonElement Options { get => _options.ValueKind == JsonValueKind.Undefined ? JsonDefaults.EmptyObject : _options; init => _options = value; }
    public List<DocumentProjection> DocumentProjections { get => _documentProjections ??= []; init => _documentProjections = value; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LocalResourceTarget), "local-resource")]
[JsonDerivedType(typeof(ApplicationExportTarget), "application-export")]
public abstract class BindingTarget;

public sealed class LocalResourceTarget : BindingTarget
{
    [JsonRequired] public required string ResourceRef { get; init; }
}

public sealed class ApplicationExportTarget : BindingTarget
{
    [JsonRequired] public required string ApplicationId { get; init; }
    [JsonRequired] public required string ExportRef { get; init; }
    [JsonRequired] public required string Contract { get; init; }
}

public sealed class DocumentProjection
{
    [JsonRequired] public required string DocumentRef { get; init; }
    [JsonRequired] public required string Path { get; init; }
    [JsonRequired] public required string RendererRef { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "format")]
[JsonDerivedType(typeof(JsonConfigurationDocument), "json")]
[JsonDerivedType(typeof(YamlConfigurationDocument), "yaml")]
[JsonDerivedType(typeof(TextConfigurationDocument), "text")]
public abstract class ConfigurationDocument
{
    [JsonRequired] public required string FileName { get; init; }
}

public sealed class JsonConfigurationDocument : ConfigurationDocument
{
    [JsonRequired] public required JsonElement Template { get; init; }
}

public sealed class YamlConfigurationDocument : ConfigurationDocument
{
    [JsonRequired] public required JsonElement Template { get; init; }
}

public sealed class TextConfigurationDocument : ConfigurationDocument
{
    [JsonRequired] public required string Template { get; init; }
}

public sealed class WorkloadDeclaration
{
    private JsonElement _properties;

    [JsonRequired] public required string Type { get; init; }
    [JsonRequired] public required string ProviderRef { get; init; }
    public string? Profile { get; init; }
    public JsonElement Properties { get => _properties.ValueKind == JsonValueKind.Undefined ? JsonDefaults.EmptyObject : _properties; init => _properties = value; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StringParameterDefinition), "string")]
[JsonDerivedType(typeof(BooleanParameterDefinition), "boolean")]
[JsonDerivedType(typeof(IntegerParameterDefinition), "integer")]
[JsonDerivedType(typeof(NumberParameterDefinition), "number")]
public abstract class ParameterDefinition
{
    public string? Description { get; init; }
    public JsonElement Default { get; init; }
    public string? DefaultExpression { get; init; }
}

public sealed class StringParameterDefinition : ParameterDefinition
{
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public List<string>? AllowedValues { get; init; }
}

public sealed class BooleanParameterDefinition : ParameterDefinition;

public sealed class IntegerParameterDefinition : ParameterDefinition
{
    public ExactJsonInteger? Minimum { get; init; }
    public ExactJsonInteger? Maximum { get; init; }
    public List<ExactJsonInteger>? AllowedValues { get; init; }
}

public sealed class NumberParameterDefinition : ParameterDefinition
{
    public ExactJsonNumber? Minimum { get; init; }
    public ExactJsonNumber? Maximum { get; init; }
    public List<ExactJsonNumber>? AllowedValues { get; init; }
}

internal static class JsonDefaults
{
    internal static readonly JsonElement EmptyObject = CreateEmptyObject();

    private static JsonElement CreateEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
