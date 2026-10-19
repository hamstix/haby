using System.Text.Json;

namespace Haby.Manifest;

/// <summary>Checks local v1alpha1 relationships after schema validation and DTO deserialization.</summary>
internal static class ManifestSemanticValidator
{
    public static IEnumerable<ManifestDiagnostic> Validate(ApplicationManifest manifest)
    {
        var resources = manifest.Spec.Resources;
        var usedResources = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<ManifestDiagnostic>();

        foreach (var (exportId, export) in manifest.Spec.Exports)
        {
            var path = "/spec/exports/" + Escape(exportId) + "/resourceRef";
            if (resources.ContainsKey(export.ResourceRef))
                usedResources.Add(export.ResourceRef);
            else
                diagnostics.Add(Missing(path, "resource", export.ResourceRef));
        }

        foreach (var (componentId, component) in manifest.Spec.Components)
        {
            var componentPath = "/spec/components/" + Escape(componentId);
            var validProjections = new List<Projection>();
            foreach (var (bindingId, binding) in component.Bindings)
            {
                var bindingPath = componentPath + "/bindings/" + Escape(bindingId);
                if (binding.Target is LocalResourceTarget local)
                {
                    var path = bindingPath + "/target/resourceRef";
                    if (resources.ContainsKey(local.ResourceRef))
                        usedResources.Add(local.ResourceRef);
                    else
                        diagnostics.Add(Missing(path, "resource", local.ResourceRef));
                }

                for (var index = 0; index < binding.DocumentProjections.Count; index++)
                {
                    var projection = binding.DocumentProjections[index];
                    var path = bindingPath + "/documentProjections/" + index + "/documentRef";
                    if (!component.Documents.TryGetValue(projection.DocumentRef, out var document))
                        diagnostics.Add(Missing(path, "document", projection.DocumentRef));
                    else if (document is TextConfigurationDocument)
                        diagnostics.Add(Error("manifest.document.projection-target-invalid", path,
                            "A text document cannot receive a projection.", ("reason", "text-document")));
                    else
                    {
                        var template = document switch
                        {
                            JsonConfigurationDocument json => json.Template,
                            YamlConfigurationDocument yaml => yaml.Template,
                            _ => default
                        };
                        if (template.ValueKind != JsonValueKind.Object)
                        {
                            diagnostics.Add(Error("manifest.document.projection-target-invalid", path,
                                "A structured document needs an object root for projections.", ("reason", "non-object-root")));
                            continue;
                        }
                        var projectionPath = bindingPath + "/documentProjections/" + index + "/path";
                        var segments = Decode(projection.Path);
                        if (segments is null)
                        {
                            diagnostics.Add(Error("manifest.document.projection-path-invalid", projectionPath,
                                "The projection path is invalid.", ("reason", projection.Path.Length == 0 ? "root" : "invalid-pointer")));
                            continue;
                        }
                        var candidate = new Projection(projection.DocumentRef, projection.Path, projection.RendererRef, projectionPath, segments);
                        var collision = TemplateCollision(template, segments);
                        if (collision is not null)
                            diagnostics.Add(Error(collision == "array-traversal" ? "manifest.document.projection-path-invalid" : "manifest.document.projection-collision",
                                projectionPath, "A projection path conflicts with the document template.", ("reason", collision)));
                        if (collision != "array-traversal")
                            validProjections.Add(candidate);
                    }
                }
            }

            foreach (var group in validProjections.GroupBy(item => item.DocumentRef, StringComparer.Ordinal))
            {
                var projections = group.OrderBy(item => item.ManifestPath, StringComparer.Ordinal).ToArray();
                for (var i = 0; i < projections.Length; i++)
                    for (var j = i + 1; j < projections.Length; j++)
                    {
                        var first = projections[i];
                        var second = projections[j];
                        string? reason = null;
                        string code = "manifest.document.projection-collision";
                        if (first.CanonicalPath == second.CanonicalPath)
                        {
                            if (first.RendererRef == second.RendererRef)
                            {
                                code = "manifest.document.projection-duplicate";
                            }
                            else reason = "same-path";
                        }
                        else if (IsAncestor(first.Segments, second.Segments) || IsAncestor(second.Segments, first.Segments))
                            reason = "ancestor-descendant";
                        else continue;
                        diagnostics.Add(new(code, ManifestDiagnosticSeverity.Error, first.ManifestPath,
                            "Projection declarations conflict.", reason is null ? Parameters() : Parameters(("reason", reason)),
                            [second.ManifestPath]));
                    }
            }

            foreach (var group in component.Documents.GroupBy(item => item.Value.FileName, StringComparer.Ordinal))
            {
                var paths = group.Select(item => componentPath + "/documents/" + Escape(item.Key) + "/fileName")
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray();
                if (paths.Length < 2) continue;
                diagnostics.Add(new("manifest.document.file-name-conflict", ManifestDiagnosticSeverity.Error,
                    paths[0], "Documents in one component use the same file name.",
                    Parameters(("fileName", group.Key)), paths[1..]));
            }
        }

        foreach (var resourceId in resources.Keys)
        {
            if (!usedResources.Contains(resourceId))
                diagnostics.Add(new("manifest.resource.unused", ManifestDiagnosticSeverity.Warning,
                    "/spec/resources/" + Escape(resourceId), "The resource has no local binding or export.",
                    Parameters(), []));
        }

        return diagnostics;
    }

    private sealed record Projection(string DocumentRef, string CanonicalPath, string RendererRef,
        string ManifestPath, string[] Segments);

    private static string[]? Decode(string pointer)
    {
        if (pointer.Length == 0 || pointer[0] != '/') return null;
        var encoded = pointer[1..].Split('/');
        var decoded = new string[encoded.Length];
        for (var i = 0; i < encoded.Length; i++)
        {
            var segment = encoded[i];
            for (var j = 0; j < segment.Length; j++)
                if (segment[j] == '~' && (++j == segment.Length || segment[j] is not ('0' or '1')))
                    return null;
            decoded[i] = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (Escape(decoded[i]) != segment) return null;
        }
        return decoded;
    }

    private static string? TemplateCollision(JsonElement template, string[] segments)
    {
        var current = template;
        for (var i = 0; i < segments.Length; i++)
        {
            if (current.ValueKind == JsonValueKind.Array) return "array-traversal";
            if (current.ValueKind != JsonValueKind.Object) return "non-object-parent";
            if (!current.TryGetProperty(segments[i], out var next)) return null; // Missing parents become Objects.
            if (i == segments.Length - 1) return "existing-leaf";
            current = next;
        }
        return null;
    }

    private static bool IsAncestor(string[] ancestor, string[] descendant) =>
        ancestor.Length < descendant.Length && ancestor.SequenceEqual(descendant.Take(ancestor.Length), StringComparer.Ordinal);

    private static ManifestDiagnostic Missing(string path, string kind, string reference) =>
        Error("manifest.reference.not-found", path, "A local reference does not resolve.",
            ("targetKind", kind), ("reference", reference));

    private static ManifestDiagnostic Error(string code, string path, string message,
        params (string Key, string Value)[] parameters) =>
        new(code, ManifestDiagnosticSeverity.Error, path, message, Parameters(parameters), []);

    private static IReadOnlyDictionary<string, string> Parameters(params (string Key, string Value)[] values) =>
        values.OrderBy(value => value.Key, StringComparer.Ordinal)
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);

    private static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);
}
