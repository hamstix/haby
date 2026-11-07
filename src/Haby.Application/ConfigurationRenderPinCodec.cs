using System.Collections.Immutable;
using System.Text.Json;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Application;

internal static class ConfigurationRenderPinCodec
{
    internal static void Write(Utf8JsonWriter w, IReadOnlyList<ConfigurationRenderPin> pins)
    {
        w.WriteStartArray();
        foreach (var p in pins)
        {
            w.WriteStartObject(); w.WriteString("component"u8, p.Component.Value); w.WriteNumber("request"u8, p.Request);
            w.WriteString("renderer"u8, p.Renderer.Value); w.WriteString("rendererImplementation"u8, p.RendererImplementation.Value);
            w.WriteString("formatter"u8, p.Formatter.Value); w.WriteString("formatterImplementation"u8, p.FormatterImplementation.Value);
            w.WriteString("resource"u8, p.Resource.Value); w.WriteString("resourceLocalId"u8, p.ResourceLocalId.Value); w.WriteString("outputContract"u8, p.OutputContract); w.WriteString("outputKey"u8, p.OutputKey);
            w.WriteString("producer"u8, p.Producer.Value);
            w.WriteStartObject("producerAction"u8);
            w.WriteString("resource"u8, p.ProducerAction.Admission.ResourceContract.Value);
            w.WriteString("provider"u8, p.ProducerAction.Admission.ProviderKind.Value);
            w.WriteString("configuration"u8, p.ProducerAction.Admission.ConfigurationContract.Value);
            w.WriteString("implementation"u8, p.ProducerAction.Admission.ImplementationId.Value);
            w.WriteString("action"u8, p.ProducerAction.ActionContract.Value); w.WriteEndObject();
            Value("defaults", p.Defaults); Value("options", p.Options);
            w.WriteStartObject("origins"u8);
            foreach (var x in p.OptionOrigins.OrderBy(x => x.Key, StringComparer.Ordinal)) w.WriteString(x.Key, x.Value.ToString());
            w.WriteEndObject(); w.WriteEndObject();
        }
        w.WriteEndArray();
        void Value(string name, ConfigurationObject value)
        { w.WritePropertyName(name); w.WriteRawValue(ConfigurationJson.Serialize(value).Json ?? throw new InvalidDataException()); }
    }
    internal static IReadOnlyList<ConfigurationRenderPin> Read(JsonElement root) => root.EnumerateArray().Select(p =>
        new ConfigurationRenderPin(ManifestLocalId.Create(p.GetProperty("component"u8).GetString()!), p.GetProperty("request"u8).GetInt32(),
            ConfigurationRendererId.Create(p.GetProperty("renderer"u8).GetString()!), ManifestLocalId.Create(p.GetProperty("rendererImplementation"u8).GetString()!),
            ConfigurationFormatterId.Create(p.GetProperty("formatter"u8).GetString()!), ManifestLocalId.Create(p.GetProperty("formatterImplementation"u8).GetString()!),
            ResourceTypeId.Create(p.GetProperty("resource"u8).GetString()!), ManifestLocalId.Create(p.GetProperty("resourceLocalId"u8).GetString()!), p.GetProperty("outputContract"u8).GetString()!, p.GetProperty("outputKey"u8).GetString()!,
            OperationId.Create(p.GetProperty("producer"u8).GetString()!), Action(p.GetProperty("producerAction"u8)), Object(p, "defaults"), Object(p, "options"),
            p.GetProperty("origins"u8).EnumerateObject().ToImmutableDictionary(x => x.Name,
                x => x.Value.GetString() switch
                {
                    "RendererDefault" => ProfileOptionScope.RendererDefault,
                    "Profile" => ProfileOptionScope.Profile,
                    "Binding" => ProfileOptionScope.Binding,
                    _ => throw new InvalidDataException()
                }, StringComparer.Ordinal))).ToImmutableArray();
    private static DurableActionExecutionKey Action(JsonElement a) => new(new(
        ResourceTypeId.Create(a.GetProperty("resource"u8).GetString()!), ProviderKindId.Create(a.GetProperty("provider"u8).GetString()!),
        ProviderConfigurationContractId.Create(a.GetProperty("configuration"u8).GetString()!), ProviderImplementationId.Create(a.GetProperty("implementation"u8).GetString()!)),
        OperationContractId.Create(a.GetProperty("action"u8).GetString()!));
    private static ConfigurationObject Object(JsonElement p, string key) =>
        ConfigurationValueMapper.FromValidatedJson(p.GetProperty(key)) as ConfigurationObject ?? throw new InvalidDataException();

    internal static void ValidateFrozen(ConfigurationRenderPin p, ProfileBindingRenderRequest r, NormalizedManifestSpec spec)
    {
        if (p.Renderer != r.Renderer || r.Binding.Target is not NormalizedLocalResourceTarget local ||
            !spec.Resources.TryGetValue(local.ResourceRef, out var resource) || resource.Type != p.Resource || p.ProducerAction.Admission.ResourceContract != p.Resource || local.ResourceRef != p.ResourceLocalId ||
            string.IsNullOrWhiteSpace(p.OutputContract) || string.IsNullOrWhiteSpace(p.OutputKey)) throw new InvalidDataException();
        var options = p.Defaults.Members.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var origins = options.Keys.ToDictionary(x => x, _ => ProfileOptionScope.RendererDefault, StringComparer.Ordinal);
        foreach (var x in r.Options.Members) { options[x.Key] = x.Value; origins[x.Key] = r.OptionOrigins[x.Key]; }
        if (!new ConfigurationObject(options).Equals(p.Options) || origins.Count != p.OptionOrigins.Count ||
            origins.Any(x => !p.OptionOrigins.TryGetValue(x.Key, out var o) || o != x.Value)) throw new InvalidDataException();
    }
}
