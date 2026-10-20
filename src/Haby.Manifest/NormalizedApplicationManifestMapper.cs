using System.Collections.Immutable;
using System.Text.Json;
using Haby.Domain;

namespace Haby.Manifest;

/// <summary>
/// Pure v1alpha1 adapter for DTOs that passed core and registered extension-contract validation.
/// It is intentionally disconnected from the parser and import pipeline until that gate exists.
/// </summary>
internal static class NormalizedApplicationManifestMapper
{
    internal static NormalizedApplicationManifest Map(ApplicationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.ApiVersion != "haby.dev/v1alpha1" || manifest.Kind != "Application")
            throw new ArgumentException("The mapper requires a validated v1alpha1 Application.", nameof(manifest));
        return new NormalizedApplicationManifest(manifest.ApiVersion,
            new NormalizedManifestMetadata(Haby.Domain.ApplicationId.Create(manifest.Metadata.Id),
                manifest.Metadata.DisplayName, Labels(manifest.Metadata.Labels)),
            new NormalizedManifestSpec(
                Entries(manifest.Spec.Parameters, Parameter),
                Entries(manifest.Spec.Resources, Resource),
                Entries(manifest.Spec.Exports, Export),
                Entries(manifest.Spec.Components, Component)));
    }

    private static NormalizedParameterDefinition Parameter(ParameterDefinition value)
    {
        var defaultValue = Default(value);
        return value switch
        {
            StringParameterDefinition item => new NormalizedStringParameter(item.Description, defaultValue,
                item.MinLength, item.MaxLength,
                item.AllowedValues is null ? null : item.AllowedValues.Order(StringComparer.Ordinal).ToImmutableArray()),
            BooleanParameterDefinition item => new NormalizedBooleanParameter(item.Description, defaultValue),
            IntegerParameterDefinition item => new NormalizedIntegerParameter(item.Description, defaultValue,
                OptionalNumber(item.Minimum?.RawText), OptionalNumber(item.Maximum?.RawText),
                item.AllowedValues is null ? null : Numbers(item.AllowedValues.Select(x => x.RawText))),
            NumberParameterDefinition item => new NormalizedNumberParameter(item.Description, defaultValue,
                OptionalNumber(item.Minimum?.RawText), OptionalNumber(item.Maximum?.RawText),
                item.AllowedValues is null ? null : Numbers(item.AllowedValues.Select(x => x.RawText))),
            _ => throw new ArgumentException("The parameter must be a validated v1alpha1 alternative.", nameof(value))
        };
    }

    private static NormalizedParameterDefault? Default(ParameterDefinition value)
    {
        if (value.DefaultExpression is not null) return new NormalizedExpressionDefault(value.DefaultExpression);
        if (value.Default.ValueKind == JsonValueKind.Undefined) return null;
        var literal = value switch
        {
            StringParameterDefinition => new NormalizedStringScalar(value.Default.GetString()!),
            BooleanParameterDefinition => (NormalizedParameterScalar)new NormalizedBooleanScalar(value.Default.GetBoolean()),
            IntegerParameterDefinition => new NormalizedIntegerScalar(ExactManifestNumber.Parse(value.Default.GetRawText())),
            NumberParameterDefinition => new NormalizedNumberScalar(ExactManifestNumber.Parse(value.Default.GetRawText())),
            _ => throw new ArgumentException("The parameter must be a validated v1alpha1 alternative.", nameof(value))
        };
        return new NormalizedLiteralDefault(literal);
    }

    private static NormalizedResource Resource(ResourceDeclaration value) => new(
        ResourceTypeId.Create(value.Type), ProviderAlias.Create(value.ProviderRef),
        OpenObject(value.Properties), value.Lifecycle.DeletionPolicy switch
        {
            "retain" => ResourceDeletionPolicy.Retain,
            "orphan" => ResourceDeletionPolicy.Orphan,
            "delete" => ResourceDeletionPolicy.Delete,
            _ => throw new ArgumentException("Unknown resource deletion policy.", nameof(value))
        });

    private static NormalizedResourceExport Export(ResourceExport value) => new(
        ManifestLocalId.Create(value.ResourceRef), OutputContractId.Create(value.Contract),
        value.AccessProfile is null ? null : ManifestLocalId.Create(value.AccessProfile));

    private static NormalizedComponent Component(ComponentDeclaration value) => new(
        value.DisplayName, Labels(value.Labels),
        Entries(value.Bindings, Binding),
        Entries(value.Documents, Document),
        Entries(value.Workloads, Workload));

    private static NormalizedBinding Binding(BindingDeclaration value) => new(
        Target(value.Target), OpenObject(value.Options),
        value.DocumentProjections.Select(Projection)
            .OrderBy(x => x.DocumentRef.Value, StringComparer.Ordinal)
            .ThenBy(x => x.Path.Value, StringComparer.Ordinal)
            .ThenBy(x => x.RendererRef.Value, StringComparer.Ordinal)
            .ToImmutableArray());

    private static NormalizedBindingTarget Target(BindingTarget value) => value switch
    {
        LocalResourceTarget item => new NormalizedLocalResourceTarget(ManifestLocalId.Create(item.ResourceRef)),
        ApplicationExportTarget item => new NormalizedApplicationExportTarget(
            Haby.Domain.ApplicationId.Create(item.ApplicationId), ManifestLocalId.Create(item.ExportRef),
            OutputContractId.Create(item.Contract)),
        _ => throw new ArgumentException("The target must be a validated v1alpha1 alternative.", nameof(value))
    };

    private static NormalizedDocumentProjection Projection(DocumentProjection value) => new(
        ManifestLocalId.Create(value.DocumentRef), ProjectionPath.FromValidated(value.Path),
        ConfigurationRendererId.Create(value.RendererRef));

    private static NormalizedConfigurationDocument Document(ConfigurationDocument value) => value switch
    {
        JsonConfigurationDocument item => new NormalizedStructuredDocument(item.FileName,
            StructuredDocumentFormat.Json, ConfigurationValueMapper.FromValidatedJson(item.Template)),
        YamlConfigurationDocument item => new NormalizedStructuredDocument(item.FileName,
            StructuredDocumentFormat.Yaml, ConfigurationValueMapper.FromValidatedJson(item.Template)),
        TextConfigurationDocument item => new NormalizedTextDocument(item.FileName, item.Template),
        _ => throw new ArgumentException("The document must be a validated v1alpha1 alternative.", nameof(value))
    };

    private static NormalizedWorkload Workload(WorkloadDeclaration value) => new(
        WorkloadTypeId.Create(value.Type), ProviderAlias.Create(value.ProviderRef),
        value.Profile is null ? null : ManifestLocalId.Create(value.Profile),
        OpenObject(value.Properties));

    private static ConfigurationObject OpenObject(JsonElement value) =>
        (ConfigurationObject)ConfigurationValueMapper.FromValidatedJson(value);

    private static ExactManifestNumber? OptionalNumber(string? raw) =>
        raw is null ? null : ExactManifestNumber.Parse(raw);

    private static ImmutableArray<ExactManifestNumber> Numbers(IEnumerable<string> raw) =>
        raw.Select(ExactManifestNumber.Parse).OrderBy(x => x.CanonicalText, StringComparer.Ordinal).ToImmutableArray();

    private static ImmutableDictionary<string, string> Labels(Dictionary<string, string> values) =>
        values.ToImmutableDictionary(StringComparer.Ordinal);

    private static ImmutableDictionary<ManifestLocalId, TValue> Entries<TSource, TValue>(
        Dictionary<string, TSource> values, Func<TSource, TValue> map) =>
        values.ToImmutableDictionary(item => ManifestLocalId.Create(item.Key), item => map(item.Value));
}
