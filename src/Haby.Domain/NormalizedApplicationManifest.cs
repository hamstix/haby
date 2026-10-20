using System.Collections.Immutable;

namespace Haby.Domain;

/// <summary>The immutable, typed declaration accepted from one manifest API version.</summary>
public sealed class NormalizedApplicationManifest : IEquatable<NormalizedApplicationManifest>
{
    internal NormalizedApplicationManifest(string inputApiVersion, NormalizedManifestMetadata metadata, NormalizedManifestSpec spec)
    {
        InputApiVersion = inputApiVersion; Metadata = metadata; Spec = spec;
    }
    public const int CurrentModelVersion = 1;
    public int NormalizedModelVersion => CurrentModelVersion;
    public string InputApiVersion { get; }
    public ApplicationId ApplicationId => Metadata.Id;
    public ApplicationManifestKind Kind => ApplicationManifestKind.Application;
    public NormalizedManifestMetadata Metadata { get; }
    public NormalizedManifestSpec Spec { get; }
    public bool Equals(NormalizedApplicationManifest? other) => NormalizedManifestStructuralComparer.Equals(this, other);
    public override bool Equals(object? obj) => obj is NormalizedApplicationManifest other && Equals(other);
    public override int GetHashCode() => NormalizedManifestStructuralComparer.GetHashCode(this);
}

public enum ApplicationManifestKind { Application }
public enum ResourceDeletionPolicy { Retain, Orphan, Delete }
public enum StructuredDocumentFormat { Json, Yaml }

public sealed class NormalizedManifestMetadata
{
    internal NormalizedManifestMetadata(ApplicationId id, string? displayName, ImmutableDictionary<string, string> labels)
    {
        Id = id; DisplayName = displayName; Labels = labels;
    }
    public ApplicationId Id { get; }
    public string? DisplayName { get; }
    public IReadOnlyDictionary<string, string> Labels { get; }
}

public sealed class NormalizedManifestSpec
{
    internal NormalizedManifestSpec(
        ImmutableDictionary<ManifestLocalId, NormalizedParameterDefinition> parameters,
        ImmutableDictionary<ManifestLocalId, NormalizedResource> resources,
        ImmutableDictionary<ManifestLocalId, NormalizedResourceExport> exports,
        ImmutableDictionary<ManifestLocalId, NormalizedComponent> components)
    {
        Parameters = parameters; Resources = resources; Exports = exports; Components = components;
    }
    public IReadOnlyDictionary<ManifestLocalId, NormalizedParameterDefinition> Parameters { get; }
    public IReadOnlyDictionary<ManifestLocalId, NormalizedResource> Resources { get; }
    public IReadOnlyDictionary<ManifestLocalId, NormalizedResourceExport> Exports { get; }
    public IReadOnlyDictionary<ManifestLocalId, NormalizedComponent> Components { get; }
}

public sealed class NormalizedResource
{
    internal NormalizedResource(ResourceTypeId type, ProviderAlias providerRef, ConfigurationObject properties, ResourceDeletionPolicy deletionPolicy)
    {
        Type = type; ProviderRef = providerRef; Properties = properties; DeletionPolicy = deletionPolicy;
    }
    public ResourceTypeId Type { get; }
    public ProviderAlias ProviderRef { get; }
    public ConfigurationObject Properties { get; }
    public ResourceDeletionPolicy DeletionPolicy { get; }
}

public sealed class NormalizedResourceExport
{
    internal NormalizedResourceExport(ManifestLocalId resourceRef, OutputContractId contract, ManifestLocalId? accessProfile)
    {
        ResourceRef = resourceRef; Contract = contract; AccessProfile = accessProfile;
    }
    public ManifestLocalId ResourceRef { get; }
    public OutputContractId Contract { get; }
    public ManifestLocalId? AccessProfile { get; }
}

public sealed class NormalizedComponent
{
    internal NormalizedComponent(string? displayName, ImmutableDictionary<string, string> labels,
        ImmutableDictionary<ManifestLocalId, NormalizedBinding> bindings,
        ImmutableDictionary<ManifestLocalId, NormalizedConfigurationDocument> documents,
        ImmutableDictionary<ManifestLocalId, NormalizedWorkload> workloads)
    {
        DisplayName = displayName; Labels = labels; Bindings = bindings; Documents = documents; Workloads = workloads;
    }
    public string? DisplayName { get; }
    public IReadOnlyDictionary<string, string> Labels { get; }
    public IReadOnlyDictionary<ManifestLocalId, NormalizedBinding> Bindings { get; }
    public IReadOnlyDictionary<ManifestLocalId, NormalizedConfigurationDocument> Documents { get; }
    public IReadOnlyDictionary<ManifestLocalId, NormalizedWorkload> Workloads { get; }
}

public sealed class NormalizedBinding
{
    internal NormalizedBinding(NormalizedBindingTarget target, ConfigurationObject options, ImmutableArray<NormalizedDocumentProjection> documentProjections)
    {
        Target = target; Options = options; DocumentProjections = documentProjections;
    }
    public NormalizedBindingTarget Target { get; }
    public ConfigurationObject Options { get; }
    public IReadOnlyList<NormalizedDocumentProjection> DocumentProjections { get; }
}

public abstract class NormalizedBindingTarget { private protected NormalizedBindingTarget() { } }
public sealed class NormalizedLocalResourceTarget : NormalizedBindingTarget
{
    internal NormalizedLocalResourceTarget(ManifestLocalId resourceRef) => ResourceRef = resourceRef;
    public ManifestLocalId ResourceRef { get; }
}
public sealed class NormalizedApplicationExportTarget : NormalizedBindingTarget
{
    internal NormalizedApplicationExportTarget(ApplicationId applicationId, ManifestLocalId exportRef, OutputContractId contract)
    {
        ApplicationId = applicationId; ExportRef = exportRef; Contract = contract;
    }
    public ApplicationId ApplicationId { get; }
    public ManifestLocalId ExportRef { get; }
    public OutputContractId Contract { get; }
}

public sealed class NormalizedDocumentProjection
{
    internal NormalizedDocumentProjection(ManifestLocalId documentRef, ProjectionPath path, ConfigurationRendererId rendererRef)
    {
        DocumentRef = documentRef; Path = path; RendererRef = rendererRef;
    }
    public ManifestLocalId DocumentRef { get; }
    public ProjectionPath Path { get; }
    public ConfigurationRendererId RendererRef { get; }
}

public abstract class NormalizedConfigurationDocument
{
    private protected NormalizedConfigurationDocument(string fileName) => FileName = fileName;
    public string FileName { get; }
}
public sealed class NormalizedStructuredDocument : NormalizedConfigurationDocument
{
    internal NormalizedStructuredDocument(string fileName, StructuredDocumentFormat format, ConfigurationValue template) : base(fileName)
    {
        Format = format; Template = template;
    }
    public StructuredDocumentFormat Format { get; }
    public ConfigurationValue Template { get; }
}
public sealed class NormalizedTextDocument : NormalizedConfigurationDocument
{
    internal NormalizedTextDocument(string fileName, string template) : base(fileName) => Template = template;
    public string Template { get; }
}

public sealed class NormalizedWorkload
{
    internal NormalizedWorkload(WorkloadTypeId type, ProviderAlias providerRef, ManifestLocalId? profile, ConfigurationObject properties)
    {
        Type = type; ProviderRef = providerRef; Profile = profile; Properties = properties;
    }
    public WorkloadTypeId Type { get; }
    public ProviderAlias ProviderRef { get; }
    public ManifestLocalId? Profile { get; }
    public ConfigurationObject Properties { get; }
}

public abstract class NormalizedParameterDefinition
{
    private protected NormalizedParameterDefinition(string? description, NormalizedParameterDefault? defaultValue)
    {
        Description = description; Default = defaultValue;
    }
    public string? Description { get; }
    public NormalizedParameterDefault? Default { get; }
}
public abstract class NormalizedParameterDefault { private protected NormalizedParameterDefault() { } }
public sealed class NormalizedLiteralDefault : NormalizedParameterDefault
{
    internal NormalizedLiteralDefault(NormalizedParameterScalar value) => Value = value;
    public NormalizedParameterScalar Value { get; }
}
public sealed class NormalizedExpressionDefault : NormalizedParameterDefault
{
    internal NormalizedExpressionDefault(string source) => Source = source;
    public string Source { get; }
}
public abstract class NormalizedParameterScalar { private protected NormalizedParameterScalar() { } }
public sealed class NormalizedStringScalar : NormalizedParameterScalar
{
    internal NormalizedStringScalar(string value) => Value = value;
    public string Value { get; }
}
public sealed class NormalizedBooleanScalar : NormalizedParameterScalar
{
    internal NormalizedBooleanScalar(bool value) => Value = value;
    public bool Value { get; }
}
public sealed class NormalizedIntegerScalar : NormalizedParameterScalar
{
    internal NormalizedIntegerScalar(ExactManifestNumber value)
    {
        if (!value.IsInteger) throw new ArgumentException("An integer scalar is required.", nameof(value));
        Value = value;
    }
    public ExactManifestNumber Value { get; }
}
public sealed class NormalizedNumberScalar : NormalizedParameterScalar
{
    internal NormalizedNumberScalar(ExactManifestNumber value) => Value = value;
    public ExactManifestNumber Value { get; }
}

public sealed class NormalizedStringParameter : NormalizedParameterDefinition
{
    internal NormalizedStringParameter(string? description, NormalizedParameterDefault? defaultValue,
        int? minLength, int? maxLength, ImmutableArray<string>? allowedValues) : base(description, defaultValue)
    {
        MinLength = minLength; MaxLength = maxLength; AllowedValues = allowedValues;
    }
    public int? MinLength { get; }
    public int? MaxLength { get; }
    public IReadOnlyList<string>? AllowedValues { get; }
}
public sealed class NormalizedBooleanParameter : NormalizedParameterDefinition
{
    internal NormalizedBooleanParameter(string? description, NormalizedParameterDefault? defaultValue)
        : base(description, defaultValue) { }
}
public sealed class NormalizedIntegerParameter : NormalizedParameterDefinition
{
    internal NormalizedIntegerParameter(string? description, NormalizedParameterDefault? defaultValue,
        ExactManifestNumber? minimum, ExactManifestNumber? maximum, ImmutableArray<ExactManifestNumber>? allowedValues)
        : base(description, defaultValue)
    {
        if (minimum is { IsInteger: false } || maximum is { IsInteger: false } ||
            allowedValues is { } values && values.Any(value => !value.IsInteger))
            throw new ArgumentException("Integer constraints require exact integer values.");
        Minimum = minimum; Maximum = maximum; AllowedValues = allowedValues;
    }
    public ExactManifestNumber? Minimum { get; }
    public ExactManifestNumber? Maximum { get; }
    public IReadOnlyList<ExactManifestNumber>? AllowedValues { get; }
}
public sealed class NormalizedNumberParameter : NormalizedParameterDefinition
{
    internal NormalizedNumberParameter(string? description, NormalizedParameterDefault? defaultValue,
        ExactManifestNumber? minimum, ExactManifestNumber? maximum, ImmutableArray<ExactManifestNumber>? allowedValues)
        : base(description, defaultValue)
    {
        Minimum = minimum; Maximum = maximum; AllowedValues = allowedValues;
    }
    public ExactManifestNumber? Minimum { get; }
    public ExactManifestNumber? Maximum { get; }
    public IReadOnlyList<ExactManifestNumber>? AllowedValues { get; }
}
