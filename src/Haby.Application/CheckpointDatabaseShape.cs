using System.Text.RegularExpressions;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Application;

/// <summary>The accepted durable checkpoint shape and name rules, shared with planning and commit.</summary>
public static class CheckpointDatabaseShape
{
    private static readonly Regex SafeDatabase = new(@"\A[a-z][a-z0-9_]{0,62}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool IsSupported(NormalizedManifestSpec spec) =>
        spec.Resources.Count == 1 && spec.Components.Count == 0 && spec.Exports.Count == 0 &&
        spec.Parameters.Count == 0 && IsSupportedResource(spec.Resources.Single().Value);

    public static bool IsSupportedResource(NormalizedResource resource) =>
        resource.Type == PostgreSqlDatabaseContract.Id && resource.Properties.Members.Count == 1 &&
        resource.Properties.Members.TryGetValue("name", out var value) &&
        value is ConfigurationString name && SafeDatabase.IsMatch(name.Value);
}
