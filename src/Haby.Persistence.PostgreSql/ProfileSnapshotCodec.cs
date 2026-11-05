using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Persistence.PostgreSql;

/// <summary>Internal Profile model/format v1, independent of Application snapshots and wire adapters.</summary>
internal static partial class ProfileSnapshotCodec
{
    internal const int FormatVersion = 1;
    internal const int ModelVersion = 1;
    private static readonly JsonSerializerOptions TreeOptions = new() { MaxDepth = 128 };

    internal static string Encode(ConfigurationProfileRevision profile)
    {
        var snapshot = new JsonObject
        {
            ["format"] = FormatVersion,
            ["model"] = ModelVersion,
            ["id"] = profile.Reference.ProfileId.Value,
            ["revision"] = profile.Reference.Revision,
            ["documents"] = Map(profile.Documents, x => x.Id.Value, x => new JsonObject
            {
                ["file"] = x.FileName,
                ["format"] = x.Format == StructuredDocumentFormat.Json ? "json" : "unsupported",
                ["template"] = Value(x.Template)
            }),
            ["contributions"] = Map(profile.Contributions, x => x.Id.Value, x => new JsonObject
            {
                ["document"] = x.Document.Value,
                ["path"] = x.Path,
                ["value"] = Value(x.Value),
                ["secrets"] = new JsonArray(x.SecretProjections.Select(p => (JsonNode)new JsonObject
                {
                    ["path"] = p.Path,
                    ["format"] = p.Format switch
                    {
                        ProfileSecretFormat.String => "string",
                        ProfileSecretFormat.Json => "json",
                        _ => throw Corrupt()
                    },
                    ["name"] = p.SecretRef.Name,
                    ["version"] = p.SecretRef.Version
                }).ToArray())
            }),
            ["rules"] = Map(profile.Rules, x => x.Id.Value, x => new JsonObject
            {
                ["slot"] = x.Slot.Value,
                ["renderer"] = x.Renderer.Value,
                ["options"] = Value(x.Defaults),
                ["projections"] = new JsonArray(x.Projections.Select(p => (JsonNode)new JsonObject
                { ["document"] = p.Document.Value, ["path"] = p.Path }).ToArray())
            })
        }.ToJsonString(TreeOptions);
        // Ports accept semantic objects too. Enforce the current stored contract before any write.
        _ = Decode(snapshot, profile.Reference.ProfileId.Value, profile.Reference.Revision, FormatVersion, ModelVersion);
        return snapshot;
    }

    internal static ConfigurationProfileRevision Decode(string snapshot, string expectedId, int expectedRevision,
        int formatVersion, int modelVersion)
    {
        try
        {
            if (formatVersion != FormatVersion || modelVersion != ModelVersion) throw Corrupt();
            using var document = JsonDocument.Parse(snapshot, new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            ValidateTree(root);
            Fields(root, "format", "model", "id", "revision", "documents", "contributions", "rules");
            if (Integer(root, "format") != FormatVersion || Integer(root, "model") != ModelVersion ||
                Text(root, "id") != expectedId || Integer(root, "revision") != expectedRevision) throw Corrupt();
            var documents = Members(root, "documents", 32).Select(x =>
            {
                Fields(x.Value, "file", "format", "template");
                var file = Text(x.Value, "file");
                if (file.Length is 0 or > 255 || !FileNamePattern().IsMatch(file) || Text(x.Value, "format") != "json") throw Corrupt();
                return new ProfileDocument(ManifestLocalId.Create(x.Name), file, StructuredDocumentFormat.Json,
                    Ordinary(x.Value.GetProperty("template"u8)));
            }).ToArray();
            var contributions = Members(root, "contributions", 256).Select(x =>
            {
                Fields(x.Value, "document", "path", "value", "secrets");
                var secrets = Items(x.Value, "secrets", 0, 255).Select(p =>
                {
                    Fields(p, "path", "format", "name", "version");
                    return new ProfileSecretProjection(Path(p), Text(p, "format") switch
                    {
                        "string" => ProfileSecretFormat.String,
                        "json" => ProfileSecretFormat.Json,
                        _ => throw Corrupt()
                    }, new(Text(p, "name"), Integer(p, "version")));
                });
                return new ProfileStaticContribution(ManifestLocalId.Create(x.Name),
                    ManifestLocalId.Create(Text(x.Value, "document")), Path(x.Value), Ordinary(x.Value.GetProperty("value"u8)), secrets);
            }).ToArray();
            var rules = Members(root, "rules", 32).Select(x =>
            {
                Fields(x.Value, "slot", "renderer", "options", "projections");
                var projections = Items(x.Value, "projections", 1, 256).Select(p =>
                {
                    Fields(p, "document", "path");
                    return new ProfileProjection(ManifestLocalId.Create(Text(p, "document")), Path(p));
                }).ToArray();
                if (projections.Distinct().Count() != projections.Length) throw Corrupt();
                return new ProfileBindingRule(ManifestLocalId.Create(x.Name), ManifestLocalId.Create(Text(x.Value, "slot")),
                    ConfigurationRendererId.Create(Text(x.Value, "renderer")),
                    Ordinary(x.Value.GetProperty("options"u8)) as ConfigurationObject ?? throw Corrupt(), projections);
            }).ToArray();
            if (contributions.Length + contributions.Sum(x => x.SecretProjections.Count) + rules.Sum(x => x.Projections.Count) > 256)
                throw Corrupt();
            return new(new(ProfileId.Create(expectedId), expectedRevision), documents, contributions, rules);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or
            FormatException or OverflowException or KeyNotFoundException)
        {
            // No exception chain: JSON/library exceptions can contain arbitrary stored values.
            throw Corrupt();
        }
    }

    private static InvalidDataException Corrupt() => new("Stored Profile snapshot is invalid or unsupported.");
    private static JsonNode? Value(ConfigurationValue value) =>
        JsonNode.Parse(ConfigurationJson.Serialize(value).Json ?? throw Corrupt(), documentOptions: new() { MaxDepth = 128 });

    private static ConfigurationValue Ordinary(JsonElement value)
    {
        // Match the wire model's container-depth bound independently of wrapper depth.
        CheckDepth(value, 0);
        return ConfigurationValueMapper.FromValidatedJson(value);
    }
    private static void CheckDepth(JsonElement value, int depth)
    {
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return;
        if (depth >= 64) throw Corrupt();
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var member in value.EnumerateObject()) CheckDepth(member.Value, depth + 1);
        else foreach (var item in value.EnumerateArray()) CheckDepth(item, depth + 1);
    }
    private static JsonObject Map<T>(IEnumerable<T> values, Func<T, string> key, Func<T, JsonObject> encode)
    {
        var result = new JsonObject();
        foreach (var value in values) result.Add(key(value), encode(value));
        return result;
    }
    private static void ValidateTree(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw Corrupt();
                ValidateTree(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateTree(item);
        else if (element.ValueKind == JsonValueKind.String) _ = element.GetString();
    }
    private static void Fields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != fields.Length ||
            element.EnumerateObject().Any(p => !fields.Contains(p.Name, StringComparer.Ordinal))) throw Corrupt();
    }
    private static JsonProperty[] Members(JsonElement parent, string name, int max)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Object) throw Corrupt();
        var members = value.EnumerateObject().ToArray();
        if (members.Length > max) throw Corrupt();
        return members;
    }
    private static JsonElement[] Items(JsonElement parent, string name, int min, int max)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < min || value.GetArrayLength() > max) throw Corrupt();
        return value.EnumerateArray().ToArray();
    }
    private static string Text(JsonElement parent, string name) => parent.GetProperty(name).GetString() ?? throw Corrupt();
    private static int Integer(JsonElement parent, string name) => parent.GetProperty(name).GetInt32();
    private static string Path(JsonElement parent)
    {
        var path = Text(parent, "path");
        if (!PointerPattern().IsMatch(path)) throw Corrupt();
        return path;
    }
    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9._-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
    [GeneratedRegex("\\A/(?:[^~]|~[01])*\\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex PointerPattern();
}
