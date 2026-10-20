using System.Text;
using System.Text.Json;
using Corvus.Text.Json.Validator;
using Haby.Domain;

namespace Haby.Manifest;

/// <summary>Checks exact registered metadata after core semantic validation.</summary>
internal static class ManifestExtensionValidator
{
    public static IEnumerable<ManifestDiagnostic> Validate(ApplicationManifest manifest, ManifestContractCatalog catalog)
    {
        foreach (var (name, resource) in manifest.Spec.Resources)
        {
            var path = "/spec/resources/" + Escape(name);
            var id = ResourceTypeId.Create(resource.Type);
            if (!catalog.TryGet(id, out var definition))
                yield return Unknown(path + "/type", "resource", id.Value);
            else
                foreach (var diagnostic in Check(resource.Properties, definition.PropertiesSchema, id.Value, path + "/properties", catalog))
                    yield return diagnostic;
        }

        foreach (var (name, export) in manifest.Spec.Exports)
        {
            var id = OutputContractId.Create(export.Contract);
            if (!catalog.TryGet(id, out _))
                yield return Unknown("/spec/exports/" + Escape(name) + "/contract", "output", id.Value);
        }

        foreach (var (componentName, component) in manifest.Spec.Components)
        {
            var componentPath = "/spec/components/" + Escape(componentName);
            foreach (var (name, workload) in component.Workloads)
            {
                var path = componentPath + "/workloads/" + Escape(name);
                var id = WorkloadTypeId.Create(workload.Type);
                if (!catalog.TryGet(id, out var definition))
                    yield return Unknown(path + "/type", "workload", id.Value);
                else
                    foreach (var diagnostic in Check(workload.Properties, definition.PropertiesSchema, id.Value, path + "/properties", catalog))
                        yield return diagnostic;
            }

            foreach (var (name, binding) in component.Bindings)
            {
                var path = componentPath + "/bindings/" + Escape(name);
                string? optionsSchema = null;
                string? ownerId = null;
                var ownerExists = false;
                if (binding.Target is LocalResourceTarget local && manifest.Spec.Resources.TryGetValue(local.ResourceRef, out var resource))
                {
                    var id = ResourceTypeId.Create(resource.Type);
                    ownerId = id.Value;
                    ownerExists = catalog.TryGet(id, out var definition);
                    if (ownerExists) optionsSchema = definition.BindingOptionsSchema;
                }
                else if (binding.Target is ApplicationExportTarget external)
                {
                    var id = OutputContractId.Create(external.Contract);
                    ownerId = id.Value;
                    ownerExists = catalog.TryGet(id, out var definition);
                    if (ownerExists) optionsSchema = definition.BindingOptionsSchema;
                    if (!ownerExists) yield return Unknown(path + "/target/contract", "output", id.Value);
                }

                if (ownerExists && optionsSchema is not null)
                    foreach (var diagnostic in Check(binding.Options, optionsSchema, ownerId!, path + "/options", catalog))
                        yield return diagnostic;
                else if (ownerExists && binding.Options.EnumerateObject().Any())
                    yield return Unknown(path + "/options", "binding-options", ownerId!);

                for (var index = 0; index < binding.DocumentProjections.Count; index++)
                {
                    var projection = binding.DocumentProjections[index];
                    var id = ConfigurationRendererId.Create(projection.RendererRef);
                    if (!catalog.Contains(id))
                        yield return Unknown(path + "/documentProjections/" + index + "/rendererRef", "renderer", id.Value);
                }
            }
        }
    }

    private static IEnumerable<ManifestDiagnostic> Check(JsonElement value, string schemaText, string contractId,
        string path, ManifestContractCatalog catalog)
    {
        using var collector = Corvus.Text.Json.JsonSchemaResultsCollector.Create(Corvus.Text.Json.JsonSchemaResultsLevel.Detailed, 20000);
        if (catalog.GetSchema(schemaText).Validate(Encoding.UTF8.GetBytes(value.GetRawText()), collector))
            yield break;

        var emitted = false;
        using var schema = JsonDocument.Parse(schemaText);
        foreach (var result in collector.EnumerateResults())
        {
            if (result.IsMatch) continue;
            var location = result.GetSchemaEvaluationLocationText();
            var keyword = location[(location.LastIndexOf('/') + 1)..];
            if (keyword is not ("required" or "additionalProperties" or "type" or "enum" or "const" or "pattern" or
                "minLength" or "maxLength" or "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" or
                "minItems" or "maxItems" or "uniqueItems" or "minProperties" or "maxProperties" or "multipleOf" or "format"))
                continue;
            var relative = result.GetDocumentEvaluationLocationText();
            if (keyword == "required" && relative.Length != 0)
            {
                emitted = true;
                yield return Violation(path + relative, contractId, keyword);
            }
            else if (keyword == "required" && TryRequired(schema.RootElement, location, value, relative, out var missing))
            {
                foreach (var name in missing)
                {
                    emitted = true;
                    yield return Violation(path + relative + "/" + Escape(name), contractId, keyword);
                }
            }
            else if (keyword != "required")
            {
                emitted = true;
                yield return Violation(path + relative, contractId, keyword);
            }
        }
        if (!emitted) yield return Violation(path, contractId, "schema");
    }

    private static bool TryRequired(JsonElement schema, string location, JsonElement value, string relative,
        out IReadOnlyList<string> missing)
    {
        missing = [];
        var fragment = location.Contains('#') ? location[(location.IndexOf('#') + 1)..] : location;
        if (!fragment.StartsWith('/')) return false;
        var node = schema;
        foreach (var encoded in fragment[1..].Split('/'))
        {
            var segment = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(segment, out var child)) node = child;
            else if (node.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index >= 0 && index < node.GetArrayLength()) node = node[index];
            else return false;
        }
        if (node.ValueKind != JsonValueKind.Array) return false;
        var instance = value;
        foreach (var encoded in relative.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (instance.ValueKind == JsonValueKind.Object && instance.TryGetProperty(segment, out var child)) instance = child;
            else if (instance.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index >= 0 && index < instance.GetArrayLength()) instance = instance[index];
            else return false;
        }
        if (instance.ValueKind != JsonValueKind.Object) return false;
        missing = node.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!).Where(name => !instance.TryGetProperty(name, out _)).ToArray();
        return missing.Count != 0;
    }

    private static ManifestDiagnostic Unknown(string path, string kind, string id) =>
        new("manifest.type.unknown", ManifestDiagnosticSeverity.Error, path, "An exact contract definition is not registered.",
            new Dictionary<string, string> { ["contractKind"] = kind, ["contractId"] = SafeId(id) }, []);

    private static ManifestDiagnostic Violation(string path, string id, string constraint) =>
        new("manifest.properties.schema-violation", ManifestDiagnosticSeverity.Error, path,
            "The extension object violates its registered schema.",
            new Dictionary<string, string> { ["contractId"] = SafeId(id), ["constraint"] = constraint }, []);

    private static string SafeId(string id) => id.Length <= 256 ? id : "<redacted>";

    private static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
