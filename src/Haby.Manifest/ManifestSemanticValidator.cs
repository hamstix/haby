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
