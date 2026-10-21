using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haby.Domain;
using Haby.Manifest;
using ApplicationId = Haby.Domain.ApplicationId;

namespace Haby.Persistence.PostgreSql;

/// <summary>Version 1 of the internal typed declaration snapshot. It is never a manifest input.</summary>
internal static class NormalizedSnapshotCodec
{
    internal const int FormatVersion = 1;

    internal static string Encode(NormalizedApplicationManifest manifest) => new JsonObject
    {
        ["format"] = FormatVersion,
        ["api"] = manifest.InputApiVersion,
        ["model"] = manifest.NormalizedModelVersion,
        ["metadata"] = new JsonObject
        {
            ["id"] = manifest.Metadata.Id.Value,
            ["display"] = manifest.Metadata.DisplayName,
            ["labels"] = Strings(manifest.Metadata.Labels)
        },
        ["spec"] = new JsonObject
        {
            ["parameters"] = Entries(manifest.Spec.Parameters, EncodeParameter),
            ["resources"] = Entries(manifest.Spec.Resources, resource => new JsonObject
            {
                ["type"] = resource.Type.Value,
                ["provider"] = resource.ProviderRef.Value,
                ["properties"] = EncodeValue(resource.Properties),
                ["deletion"] = resource.DeletionPolicy.ToString()
            }),
            ["exports"] = Entries(manifest.Spec.Exports, export => new JsonObject
            {
                ["resource"] = export.ResourceRef.Value,
                ["contract"] = export.Contract.Value,
                ["profile"] = export.AccessProfile?.Value
            }),
            ["components"] = Entries(manifest.Spec.Components, EncodeComponent)
        }
    }.ToJsonString();

    internal static NormalizedApplicationManifest Decode(string snapshot, string expectedApi, int expectedModel,
        string expectedApplicationId, int formatVersion)
    {
        try
        {
            if (formatVersion != FormatVersion)
                throw new InvalidDataException("Unsupported normalized snapshot format.");
            using (var document = JsonDocument.Parse(snapshot))
                RejectDuplicateProperties(document.RootElement);
            var root = Object(JsonNode.Parse(snapshot));
            if (Int(root, "format") != FormatVersion || Int(root, "model") != expectedModel ||
                expectedModel != NormalizedApplicationManifest.CurrentModelVersion ||
                Text(root, "api") != expectedApi)
                throw new InvalidDataException("Stored normalized snapshot version is inconsistent or unsupported.");
            var metadata = Object(root["metadata"]);
            var id = Text(metadata, "id");
            if (id != expectedApplicationId)
                throw new InvalidDataException("Stored normalized snapshot identity is inconsistent.");
            var spec = Object(root["spec"]);
            return new NormalizedApplicationManifest(expectedApi,
                new NormalizedManifestMetadata(ApplicationId.Create(id), OptionalText(metadata, "display"), ReadStrings(metadata, "labels")),
                new NormalizedManifestSpec(
                    ReadEntries(spec, "parameters", DecodeParameter),
                    ReadEntries(spec, "resources", node =>
                    {
                        var item = Object(node);
                        return new NormalizedResource(ResourceTypeId.Create(Text(item, "type")),
                            ProviderAlias.Create(Text(item, "provider")), ReadObject(item, "properties"),
                            EnumValue<ResourceDeletionPolicy>(item, "deletion"));
                    }),
                    ReadEntries(spec, "exports", node =>
                    {
                        var item = Object(node);
                        return new NormalizedResourceExport(ManifestLocalId.Create(Text(item, "resource")),
                            OutputContractId.Create(Text(item, "contract")), LocalId(OptionalText(item, "profile")));
                    }),
                    ReadEntries(spec, "components", DecodeComponent)));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Stored normalized snapshot is corrupt.", error);
        }
    }

    private static JsonObject EncodeComponent(NormalizedComponent component) => new()
    {
        ["display"] = component.DisplayName,
        ["labels"] = Strings(component.Labels),
        ["bindings"] = Entries(component.Bindings, binding => new JsonObject
        {
            ["target"] = binding.Target switch
            {
                NormalizedLocalResourceTarget local => new JsonObject { ["kind"] = "local", ["resource"] = local.ResourceRef.Value },
                NormalizedApplicationExportTarget external => new JsonObject
                {
                    ["kind"] = "export",
                    ["application"] = external.ApplicationId.Value,
                    ["export"] = external.ExportRef.Value,
                    ["contract"] = external.Contract.Value
                },
                _ => throw new InvalidOperationException("Unknown binding target.")
            },
            ["options"] = EncodeValue(binding.Options),
            ["projections"] = new JsonArray(binding.DocumentProjections.Select(projection => (JsonNode?)new JsonObject
            {
                ["document"] = projection.DocumentRef.Value,
                ["path"] = projection.Path.Value,
                ["renderer"] = projection.RendererRef.Value
            }).ToArray())
        }),
        ["documents"] = Entries(component.Documents, document => document switch
        {
            NormalizedStructuredDocument structured => new JsonObject
            {
                ["kind"] = "structured",
                ["file"] = structured.FileName,
                ["format"] = structured.Format.ToString(),
                ["template"] = EncodeValue(structured.Template)
            },
            NormalizedTextDocument text => new JsonObject
            {
                ["kind"] = "text",
                ["file"] = text.FileName,
                ["template"] = text.Template
            },
            _ => throw new InvalidOperationException("Unknown document kind.")
        }),
        ["workloads"] = Entries(component.Workloads, workload => new JsonObject
        {
            ["type"] = workload.Type.Value,
            ["provider"] = workload.ProviderRef.Value,
            ["profile"] = workload.Profile?.Value,
            ["properties"] = EncodeValue(workload.Properties)
        })
    };

    private static NormalizedComponent DecodeComponent(JsonNode? node)
    {
        var item = Object(node);
        return new NormalizedComponent(OptionalText(item, "display"), ReadStrings(item, "labels"),
            ReadEntries(item, "bindings", value =>
            {
                var binding = Object(value);
                var target = Object(binding["target"]);
                NormalizedBindingTarget parsedTarget = Text(target, "kind") switch
                {
                    "local" => new NormalizedLocalResourceTarget(ManifestLocalId.Create(Text(target, "resource"))),
                    "export" => new NormalizedApplicationExportTarget(ApplicationId.Create(Text(target, "application")),
                        ManifestLocalId.Create(Text(target, "export")), OutputContractId.Create(Text(target, "contract"))),
                    _ => throw new InvalidDataException("Unknown stored binding target.")
                };
                var projections = Array(binding, "projections").Select(value =>
                {
                    var projection = Object(value);
                    return new NormalizedDocumentProjection(ManifestLocalId.Create(Text(projection, "document")),
                        ProjectionPath.FromValidated(Text(projection, "path")), ConfigurationRendererId.Create(Text(projection, "renderer")));
                }).ToImmutableArray();
                return new NormalizedBinding(parsedTarget, ReadObject(binding, "options"), projections);
            }),
            ReadEntries(item, "documents", value =>
            {
                var document = Object(value);
                return Text(document, "kind") switch
                {
                    "structured" => (NormalizedConfigurationDocument)new NormalizedStructuredDocument(Text(document, "file"),
                        EnumValue<StructuredDocumentFormat>(document, "format"), DecodeValue(document["template"])),
                    "text" => new NormalizedTextDocument(Text(document, "file"), Text(document, "template")),
                    _ => throw new InvalidDataException("Unknown stored document kind.")
                };
            }),
            ReadEntries(item, "workloads", value =>
            {
                var workload = Object(value);
                return new NormalizedWorkload(WorkloadTypeId.Create(Text(workload, "type")),
                    ProviderAlias.Create(Text(workload, "provider")), LocalId(OptionalText(workload, "profile")),
                    ReadObject(workload, "properties"));
            }));
    }

    private static JsonObject EncodeParameter(NormalizedParameterDefinition parameter)
    {
        var result = new JsonObject { ["description"] = parameter.Description, ["default"] = EncodeDefault(parameter.Default) };
        switch (parameter)
        {
            case NormalizedStringParameter value:
                result["kind"] = "string"; result["minLength"] = value.MinLength; result["maxLength"] = value.MaxLength;
                result["allowed"] = value.AllowedValues is null ? null : new JsonArray(value.AllowedValues.Select(item => JsonValue.Create(item)).ToArray());
                break;
            case NormalizedBooleanParameter:
                result["kind"] = "boolean"; break;
            case NormalizedIntegerParameter value:
                result["kind"] = "integer"; AddNumericConstraints(result, value.Minimum, value.Maximum, value.AllowedValues); break;
            case NormalizedNumberParameter value:
                result["kind"] = "number"; AddNumericConstraints(result, value.Minimum, value.Maximum, value.AllowedValues); break;
            default: throw new InvalidOperationException("Unknown parameter kind.");
        }
        return result;
    }

    private static void AddNumericConstraints(JsonObject result, ExactManifestNumber? minimum, ExactManifestNumber? maximum,
        IReadOnlyList<ExactManifestNumber>? allowed)
    {
        result["minimum"] = minimum?.CanonicalText;
        result["maximum"] = maximum?.CanonicalText;
        result["allowed"] = allowed is null ? null : new JsonArray(allowed.Select(value => JsonValue.Create(value.CanonicalText)).ToArray());
    }

    private static NormalizedParameterDefinition DecodeParameter(JsonNode? node)
    {
        var item = Object(node);
        RequireField(item, "description");
        RequireField(item, "default");
        if (Text(item, "kind") is "string" or "integer" or "number") RequireField(item, "allowed");
        var description = OptionalText(item, "description");
        var defaultValue = DecodeDefault(item["default"]);
        return Text(item, "kind") switch
        {
            "string" => new NormalizedStringParameter(description, defaultValue, OptionalInt(item, "minLength"),
                OptionalInt(item, "maxLength"), item["allowed"] is null ? null :
                    Array(item, "allowed").Select(value => ValueText(value)).ToImmutableArray()),
            "boolean" => new NormalizedBooleanParameter(description, defaultValue),
            "integer" => new NormalizedIntegerParameter(description, defaultValue, OptionalNumber(item, "minimum"),
                OptionalNumber(item, "maximum"), ReadAllowedNumbers(item)),
            "number" => new NormalizedNumberParameter(description, defaultValue, OptionalNumber(item, "minimum"),
                OptionalNumber(item, "maximum"), ReadAllowedNumbers(item)),
            _ => throw new InvalidDataException("Unknown stored parameter kind.")
        };
    }

    private static JsonNode? EncodeDefault(NormalizedParameterDefault? value) => value switch
    {
        null => null,
        NormalizedExpressionDefault expression => new JsonObject { ["kind"] = "expression", ["source"] = expression.Source },
        NormalizedLiteralDefault literal => literal.Value switch
        {
            NormalizedStringScalar scalar => new JsonObject { ["kind"] = "string", ["value"] = scalar.Value },
            NormalizedBooleanScalar scalar => new JsonObject { ["kind"] = "boolean", ["value"] = scalar.Value },
            NormalizedIntegerScalar scalar => new JsonObject { ["kind"] = "integer", ["value"] = scalar.Value.CanonicalText },
            NormalizedNumberScalar scalar => new JsonObject { ["kind"] = "number", ["value"] = scalar.Value.CanonicalText },
            _ => throw new InvalidOperationException("Unknown scalar kind.")
        },
        _ => throw new InvalidOperationException("Unknown parameter default.")
    };

    private static NormalizedParameterDefault? DecodeDefault(JsonNode? node)
    {
        if (node is null) return null;
        var item = Object(node);
        return Text(item, "kind") switch
        {
            "expression" => new NormalizedExpressionDefault(Text(item, "source")),
            "string" => new NormalizedLiteralDefault(new NormalizedStringScalar(Text(item, "value"))),
            "boolean" => new NormalizedLiteralDefault(new NormalizedBooleanScalar(Bool(item, "value"))),
            "integer" => new NormalizedLiteralDefault(new NormalizedIntegerScalar(ExactManifestNumber.Parse(Text(item, "value")))),
            "number" => new NormalizedLiteralDefault(new NormalizedNumberScalar(ExactManifestNumber.Parse(Text(item, "value")))),
            _ => throw new InvalidDataException("Unknown stored default kind.")
        };
    }

    private static JsonObject EncodeValue(ConfigurationValue value) => value switch
    {
        ConfigurationNull => new JsonObject { ["kind"] = "null" },
        ConfigurationBoolean item => new JsonObject { ["kind"] = "boolean", ["value"] = item.Value },
        ConfigurationString item => new JsonObject { ["kind"] = "string", ["value"] = item.Value },
        ConfigurationNumber item => new JsonObject { ["kind"] = "number", ["value"] = item.CanonicalText },
        ConfigurationArray item => new JsonObject { ["kind"] = "array", ["items"] = new JsonArray(item.Items.Select(element => (JsonNode?)EncodeValue(element)).ToArray()) },
        ConfigurationObject item => new JsonObject { ["kind"] = "object", ["members"] = Values(item.Members) },
        ConfigurationSecretReference item => new JsonObject
        {
            ["kind"] = "secret",
            ["id"] = item.Reference.SecretId.ToString(),
            ["version"] = item.Reference.SecretVersionId.ToString()
        },
        _ => throw new InvalidOperationException("Unknown configuration value kind.")
    };

    private static ConfigurationValue DecodeValue(JsonNode? node)
    {
        var item = Object(node);
        return Text(item, "kind") switch
        {
            "null" => ConfigurationNull.Instance,
            "boolean" => new ConfigurationBoolean(Bool(item, "value")),
            "string" => new ConfigurationString(Text(item, "value")),
            "number" => ConfigurationNumber.Parse(Text(item, "value")),
            "array" => new ConfigurationArray(Array(item, "items").Select(DecodeValue)),
            "object" => new ConfigurationObject(Object(item["members"]).Select(member =>
                new KeyValuePair<string, ConfigurationValue>(member.Key, DecodeValue(member.Value)))),
            "secret" => new ConfigurationSecretReference(new SecretRef(SecretId.Parse(Text(item, "id")),
                SecretVersionId.Parse(Text(item, "version")))),
            _ => throw new InvalidDataException("Unknown stored configuration value kind.")
        };
    }

    private static ConfigurationObject ReadObject(JsonObject item, string name) => DecodeValue(item[name]) is ConfigurationObject value
        ? value : throw new InvalidDataException("Stored configuration object has the wrong kind.");
    private static JsonObject Strings(IReadOnlyDictionary<string, string> values) =>
        new(values.Select(item => new KeyValuePair<string, JsonNode?>(item.Key, JsonValue.Create(item.Value))));
    private static JsonObject Values(IReadOnlyDictionary<string, ConfigurationValue> values) =>
        new(values.Select(item => new KeyValuePair<string, JsonNode?>(item.Key, EncodeValue(item.Value))));
    private static JsonObject Entries<T>(IReadOnlyDictionary<ManifestLocalId, T> entries, Func<T, JsonObject> encode) =>
        new(entries.Select(item => new KeyValuePair<string, JsonNode?>(item.Key.Value, encode(item.Value))));
    private static ImmutableDictionary<string, string> ReadStrings(JsonObject item, string name) =>
        Object(item[name]).ToImmutableDictionary(entry => entry.Key, entry => ValueText(entry.Value), StringComparer.Ordinal);
    private static ImmutableDictionary<ManifestLocalId, T> ReadEntries<T>(JsonObject item, string name, Func<JsonNode?, T> decode) =>
        Object(item[name]).ToImmutableDictionary(entry => ManifestLocalId.Create(entry.Key), entry => decode(entry.Value));
    private static ImmutableArray<ExactManifestNumber>? ReadAllowedNumbers(JsonObject item)
    {
        RequireField(item, "allowed");
        return item["allowed"] is null ? null : Array(item, "allowed")
            .Select(value => ExactManifestNumber.Parse(ValueText(value))).ToImmutableArray();
    }
    private static ExactManifestNumber? OptionalNumber(JsonObject item, string name) => OptionalText(item, name) is { } text
        ? ExactManifestNumber.Parse(text) : null;
    private static ManifestLocalId? LocalId(string? text) => text is null ? null : ManifestLocalId.Create(text);
    private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw new InvalidDataException("Stored snapshot object is missing.");
    private static JsonArray Array(JsonObject item, string name) => item[name] as JsonArray ?? throw new InvalidDataException("Stored snapshot array is missing.");
    private static string ValueText(JsonNode? node) => node?.GetValue<string>() ?? throw new InvalidDataException("Stored snapshot text is missing.");
    private static string Text(JsonObject item, string name) => ValueText(item[name]);
    private static string? OptionalText(JsonObject item, string name)
    {
        RequireField(item, name);
        return item[name] is null ? null : ValueText(item[name]);
    }
    private static int Int(JsonObject item, string name) => item[name]?.GetValue<int>() ?? throw new InvalidDataException("Stored snapshot integer is missing.");
    private static int? OptionalInt(JsonObject item, string name)
    {
        RequireField(item, name);
        return item[name] is null ? null : Int(item, name);
    }
    private static bool Bool(JsonObject item, string name) => item[name]?.GetValue<bool>() ?? throw new InvalidDataException("Stored snapshot Boolean is missing.");
    private static T EnumValue<T>(JsonObject item, string name) where T : struct, Enum
    {
        var text = Text(item, name);
        if (!Enum.TryParse<T>(text, false, out var value) || !Enum.IsDefined(value) || value.ToString() != text)
            throw new InvalidDataException("Stored snapshot enum is unsupported.");
        return value;
    }
    private static void RequireField(JsonObject item, string name)
    {
        if (!item.ContainsKey(name)) throw new InvalidDataException("Stored snapshot field is missing.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException("Stored snapshot contains duplicate fields.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
