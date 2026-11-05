using System.Collections.Immutable;
using Haby.Domain;

namespace Haby.Application;

/// <summary>A ready projection with a safe local origin identity. Invalid pointers are reported by composition.</summary>
public sealed class ConfigurationProjection(string path, ConfigurationFragment fragment, ManifestLocalId source)
{
    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));
    public ConfigurationFragment Fragment { get; } = fragment ?? throw new ArgumentNullException(nameof(fragment));
    public ManifestLocalId Source { get; } = source ?? throw new ArgumentNullException(nameof(source));
}

public sealed record ConfigurationRenderDiagnostic(string Code, string Path, string Source);

/// <summary>All errors are counted before truncation; success never conceals an omitted error.</summary>
public sealed class ConfigurationCompositionResult
{
    internal ConfigurationCompositionResult(ConfigurationValue? document, IEnumerable<ConfigurationRenderDiagnostic> errors)
    {
        var ordered = errors.Distinct().OrderBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Source, StringComparer.Ordinal).ToArray();
        ErrorCount = ordered.Length;
        Diagnostics = ordered.Take(100).ToImmutableArray();
        Document = ErrorCount == 0 ? document : null;
    }
    public ConfigurationValue? Document { get; }
    public bool IsSuccess => Document is not null;
    public int ErrorCount { get; }
    public bool IsTruncated => ErrorCount > Diagnostics.Count;
    public IReadOnlyList<ConfigurationRenderDiagnostic> Diagnostics { get; }
}

/// <summary>Pure all-or-nothing static tree composition; no expression evaluation or formatter execution.</summary>
public sealed class ConfigurationComposer(ConfigurationFormatterCatalog formatters)
{
    private readonly ConfigurationFormatterCatalog _formatters = formatters ?? throw new ArgumentNullException(nameof(formatters));
    public ConfigurationCompositionResult Compose(ConfigurationValue template, IEnumerable<ConfigurationProjection> projections)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(projections);
        var inputs = projections.ToArray();
        foreach (var input in inputs) ArgumentNullException.ThrowIfNull(input);
        var errors = new List<ConfigurationRenderDiagnostic>();
        ValidateTree(template, "", "", errors);
        errors.AddRange(ValidateStructure(template, inputs.Select(x => (x.Path, x.Source.Value))));
        foreach (var projection in inputs)
            if (Decode(projection.Path) is not null)
                ValidateTree(projection.Fragment.Value, projection.Path, projection.Source.Value, errors);
        if (errors.Count != 0) return new(null, errors);
        var result = template;
        foreach (var projection in inputs.OrderBy(x => x.Path, StringComparer.Ordinal))
            result = Insert((ConfigurationObject)result, Decode(projection.Path)!, 0, projection.Fragment.Value);
        return new(result, []);
    }
    /// <summary>Validate reserved paths without producing placeholder values or a document.</summary>
    internal static IReadOnlyList<ConfigurationRenderDiagnostic> ValidateStructure(ConfigurationValue template,
        IEnumerable<(string Path, string Source)> targets)
    {
        var errors = new List<ConfigurationRenderDiagnostic>();
        var valid = new List<(string Path, string Source, string[] Segments)>();
        foreach (var (path, source) in targets)
        {
            var segments = Decode(path);
            if (segments is null) errors.Add(new("render.projection.invalid-path", "", source));
            else
            {
                valid.Add((path, source, segments));
                if (Collides(template, segments)) errors.Add(new("render.projection.template-collision", path, source));
            }
        }
        for (var i = 0; i < valid.Count; i++)
            for (var j = i + 1; j < valid.Count; j++)
                if (Prefix(valid[i].Segments, valid[j].Segments) || Prefix(valid[j].Segments, valid[i].Segments))
                    foreach (var candidate in new[] { valid[i], valid[j] })
                        errors.Add(new("render.projection.path-conflict", candidate.Path, candidate.Source));
        return errors;
    }
    private void ValidateTree(ConfigurationValue value, string path, string source, List<ConfigurationRenderDiagnostic> errors)
    {
        switch (value)
        {
            case ConfigurationDeferredValue deferred:
                if (_formatters.Validate(deferred) is { } code) errors.Add(new(code, path, source));
                break;
            case ConfigurationObject obj:
                foreach (var (key, child) in obj.Members) ValidateTree(child, path + "/" + Escape(key), source, errors);
                break;
            case ConfigurationArray array:
                for (var i = 0; i < array.Items.Count; i++) ValidateTree(array.Items[i], path + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), source, errors);
                break;
        }
    }
    internal static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static string[]? Decode(string path)
    {
        if (path.Length == 0 || path[0] != '/') return null;
        var segments = path[1..].Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var encoded = segments[i];
            segments[i] = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (Escape(segments[i]) != encoded) return null;
        }
        return segments;
    }
    private static bool Prefix(string[] a, string[] b) => a.Length <= b.Length && a.SequenceEqual(b.Take(a.Length), StringComparer.Ordinal);
    private static bool Collides(ConfigurationValue root, string[] segments)
    {
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            if (current is not ConfigurationObject obj) return true;
            if (!obj.Members.TryGetValue(segments[i], out var next)) return false;
            if (i == segments.Length - 1) return true;
            current = next;
        }
        return false;
    }
    private static ConfigurationObject Insert(ConfigurationObject root, string[] segments, int index, ConfigurationValue value)
    {
        var members = root.Members.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var key = segments[index];
        members[key] = index == segments.Length - 1 ? value : Insert(
            members.TryGetValue(key, out var child) ? (ConfigurationObject)child : new ConfigurationObject([]), segments, index + 1, value);
        return new(members);
    }
}
