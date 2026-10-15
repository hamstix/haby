using System.Text.Json;

namespace Haby.Manifest;

/// <summary>
/// Completes the v1alpha1 schema's numeric uniqueItems assertion. Corvus 5.7.7
/// treats negative and positive zero as distinct array items, while the
/// manifest contract compares numbers by exact mathematical value.
/// Call only after the checked-in schema has accepted the manifest shape.
/// </summary>
public static class V1Alpha1NumericSetGuard
{
    public const string DiagnosticCode = "manifest.parameter.allowed-value-duplicate";
    public const string Constraint = "uniqueItems";

    public static IReadOnlyList<string> FindDuplicatePaths(JsonElement manifest, int maxDiagnostics = 100)
    {
        if (maxDiagnostics <= 0 ||
            !manifest.TryGetProperty("spec"u8, out var spec) ||
            spec.ValueKind != JsonValueKind.Object ||
            !spec.TryGetProperty("parameters"u8, out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object)
            return [];

        var paths = new List<string>();
        foreach (var parameter in parameters.EnumerateObject())
        {
            var definition = parameter.Value;
            if (definition.ValueKind != JsonValueKind.Object ||
                !definition.TryGetProperty("type"u8, out var type) ||
                type.ValueKind != JsonValueKind.String ||
                type.GetString() is not ("integer" or "number") ||
                !definition.TryGetProperty("allowedValues"u8, out var values) ||
                values.ValueKind != JsonValueKind.Array)
                continue;

            var seen = new HashSet<ExactJsonNumber>();
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Number)
                    break;
                if (seen.Add(ExactJsonNumber.Parse(value.GetRawText())))
                    continue;

                var escapedId = parameter.Name.Replace("~", "~0", StringComparison.Ordinal)
                    .Replace("/", "~1", StringComparison.Ordinal);
                paths.Add($"/spec/parameters/{escapedId}/allowedValues");
                if (paths.Count == maxDiagnostics)
                    return paths;
                break;
            }
        }
        return paths;
    }
}
