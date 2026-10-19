using System.Text.Json;
using Haby.Domain;

namespace Haby.Manifest;

/// <summary>Maps a core-validated v1alpha1 DTO without retaining its mutable storage.</summary>
internal static class NormalizedApplicationManifestMapper
{
    internal static NormalizedApplicationManifest Map(ApplicationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new NormalizedApplicationManifest(manifest.ApiVersion, Haby.Domain.ApplicationId.Create(manifest.Metadata.Id), Object(
            ("apiVersion", Text(manifest.ApiVersion)),
            ("kind", Text(manifest.Kind)),
            ("metadata", Object(
                ("id", Text(manifest.Metadata.Id)),
                ("displayName", OptionalText(manifest.Metadata.DisplayName)),
                ("labels", StringMap(manifest.Metadata.Labels)))),
            ("spec", Object(
                ("parameters", Map(manifest.Spec.Parameters, Parameter)),
                ("resources", Map(manifest.Spec.Resources, Resource)),
                ("exports", Map(manifest.Spec.Exports, Export)),
                ("components", Map(manifest.Spec.Components, Component))))));
    }

    private static ConfigurationValue Parameter(ParameterDefinition value)
    {
        var fields = new List<(string, ConfigurationValue?)>
        {
            ("description", OptionalText(value.Description)),
            ("default", value.Default.ValueKind == JsonValueKind.Undefined ? null : ConfigurationValueMapper.FromValidatedJson(value.Default)),
            ("defaultExpression", OptionalText(value.DefaultExpression))
        };
        switch (value)
        {
            case StringParameterDefinition item:
                fields.Add(("type", Text("string")));
                fields.Add(("minLength", item.MinLength is int min ? Number(min.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null));
                fields.Add(("maxLength", item.MaxLength is int max ? Number(max.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null));
                fields.Add(("allowedValues", item.AllowedValues is null ? null : Set(item.AllowedValues.Select(Text), item.AllowedValues.Select(x => x))));
                break;
            case BooleanParameterDefinition:
                fields.Add(("type", Text("boolean")));
                break;
            case IntegerParameterDefinition item:
                fields.Add(("type", Text("integer")));
                fields.Add(("minimum", item.Minimum is { } integerMinimum ? Number(integerMinimum.RawText) : null));
                fields.Add(("maximum", item.Maximum is { } integerMaximum ? Number(integerMaximum.RawText) : null));
                fields.Add(("allowedValues", item.AllowedValues is null ? null : NumericSet(item.AllowedValues.Select(x => x.RawText))));
                break;
            case NumberParameterDefinition item:
                fields.Add(("type", Text("number")));
                fields.Add(("minimum", item.Minimum is { } numberMinimum ? Number(numberMinimum.RawText) : null));
                fields.Add(("maximum", item.Maximum is { } numberMaximum ? Number(numberMaximum.RawText) : null));
                fields.Add(("allowedValues", item.AllowedValues is null ? null : NumericSet(item.AllowedValues.Select(x => x.RawText))));
                break;
            default:
                throw new ArgumentException("The parameter must be a validated v1alpha1 alternative.", nameof(value));
        }
        return Object(fields);
    }

    private static ConfigurationValue Resource(ResourceDeclaration value) => Object(
        ("type", Text(value.Type)),
        ("providerRef", Text(value.ProviderRef)),
        ("properties", ConfigurationValueMapper.FromValidatedJson(value.Properties)),
        ("lifecycle", Object(("deletionPolicy", Text(value.Lifecycle.DeletionPolicy)))));

    private static ConfigurationValue Export(ResourceExport value) => Object(
        ("resourceRef", Text(value.ResourceRef)),
        ("contract", Text(value.Contract)),
        ("accessProfile", OptionalText(value.AccessProfile)));

    private static ConfigurationValue Component(ComponentDeclaration value) => Object(
        ("displayName", OptionalText(value.DisplayName)),
        ("labels", StringMap(value.Labels)),
        ("bindings", Map(value.Bindings, Binding)),
        ("documents", Map(value.Documents, Document)),
        ("workloads", Map(value.Workloads, Workload)));

    private static ConfigurationValue Binding(BindingDeclaration value) => Object(
        ("target", Target(value.Target)),
        ("options", ConfigurationValueMapper.FromValidatedJson(value.Options)),
        ("documentProjections", new ConfigurationArray(value.DocumentProjections
            .OrderBy(x => x.DocumentRef, StringComparer.Ordinal)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.RendererRef, StringComparer.Ordinal)
            .Select(Projection))));

    private static ConfigurationValue Target(BindingTarget value) => value switch
    {
        LocalResourceTarget item => Object(("kind", Text("local-resource")), ("resourceRef", Text(item.ResourceRef))),
        ApplicationExportTarget item => Object(("kind", Text("application-export")),
            ("applicationId", Text(item.ApplicationId)), ("exportRef", Text(item.ExportRef)),
            ("contract", Text(item.Contract))),
        _ => throw new ArgumentException("The target must be a validated v1alpha1 alternative.", nameof(value))
    };

    private static ConfigurationValue Projection(DocumentProjection value) => Object(
        ("documentRef", Text(value.DocumentRef)), ("path", Text(value.Path)),
        ("rendererRef", Text(value.RendererRef)));

    private static ConfigurationValue Document(ConfigurationDocument value) => value switch
    {
        JsonConfigurationDocument item => Object(("format", Text("json")),
            ("fileName", Text(item.FileName)), ("template", ConfigurationValueMapper.FromValidatedJson(item.Template))),
        YamlConfigurationDocument item => Object(("format", Text("yaml")),
            ("fileName", Text(item.FileName)), ("template", ConfigurationValueMapper.FromValidatedJson(item.Template))),
        TextConfigurationDocument item => Object(("format", Text("text")),
            ("fileName", Text(item.FileName)), ("template", Text(item.Template))),
        _ => throw new ArgumentException("The document must be a validated v1alpha1 alternative.", nameof(value))
    };

    private static ConfigurationValue Workload(WorkloadDeclaration value) => Object(
        ("type", Text(value.Type)), ("providerRef", Text(value.ProviderRef)),
        ("profile", OptionalText(value.Profile)),
        ("properties", ConfigurationValueMapper.FromValidatedJson(value.Properties)));

    private static ConfigurationArray NumericSet(IEnumerable<string> values)
    {
        var numbers = values.Select(Number).ToArray();
        return Set(numbers, numbers.Select(x => x.CanonicalText));
    }

    private static ConfigurationArray Set<T>(IEnumerable<T> values, IEnumerable<string> keys) where T : ConfigurationValue =>
        new(values.Zip(keys).OrderBy(x => x.Second, StringComparer.Ordinal).Select(x => x.First));

    private static ConfigurationNumber Number(string rawText) => ConfigurationNumber.Parse(rawText);
    private static ConfigurationString Text(string value) => new(value);
    private static ConfigurationString? OptionalText(string? value) => value is null ? null : Text(value);

    private static ConfigurationObject StringMap(Dictionary<string, string> values) =>
        new(values.Select(item => new KeyValuePair<string, ConfigurationValue>(item.Key, Text(item.Value))));

    private static ConfigurationObject Map<T>(Dictionary<string, T> values, Func<T, ConfigurationValue> map) =>
        new(values.Select(item => new KeyValuePair<string, ConfigurationValue>(item.Key, map(item.Value))));

    private static ConfigurationObject Object(params (string Key, ConfigurationValue? Value)[] fields) => Object((IEnumerable<(string Key, ConfigurationValue? Value)>)fields);
    private static ConfigurationObject Object(IEnumerable<(string Key, ConfigurationValue? Value)> fields) =>
        new(fields.Where(item => item.Value is not null).Select(item =>
            new KeyValuePair<string, ConfigurationValue>(item.Key, item.Value!)));
}
