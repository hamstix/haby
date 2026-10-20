using Haby.Domain;

namespace Haby.Application;

/// <summary>Value-free, bounded paths through the authoritative normalized declaration.</summary>
internal static class NormalizedManifestDiff
{
    internal sealed record Result(IReadOnlyList<string> Paths, bool Truncated);

    internal static Result Compare(NormalizedApplicationManifest left, NormalizedApplicationManifest right)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var truncated = false;
        void Add(string path)
        {
            paths.Add(path);
            if (paths.Count <= 100) return;
            truncated = true;
            paths.Remove(paths.Max!);
        }
        Check(left.InputApiVersion, right.InputApiVersion, "/apiVersion", Add);
        Check(left.Kind, right.Kind, "/kind", Add);
        Check(left.Metadata.Id, right.Metadata.Id, "/metadata/id", Add);
        Check(left.Metadata.DisplayName, right.Metadata.DisplayName, "/metadata/displayName", Add);
        Map(left.Metadata.Labels, right.Metadata.Labels, "/metadata/labels", (a, b, p) => Check(a, b, p, Add), Add);
        Map(left.Spec.Parameters, right.Spec.Parameters, "/spec/parameters", (a, b, p) => Parameter(a, b, p, Add), Add);
        Map(left.Spec.Resources, right.Spec.Resources, "/spec/resources", (a, b, p) => Resource(a, b, p, Add), Add);
        Map(left.Spec.Exports, right.Spec.Exports, "/spec/exports", (a, b, p) => Export(a, b, p, Add), Add);
        Map(left.Spec.Components, right.Spec.Components, "/spec/components", (a, b, p) => Component(a, b, p, Add), Add);
        return new Result(Array.AsReadOnly(paths.ToArray()), truncated);
    }

    private static string Child(string path, string key) => path + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static void Check<T>(T left, T right, string path, Action<string> add)
    {
        if (!EqualityComparer<T>.Default.Equals(left, right)) add(path);
    }
    private static void Map<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> left, IReadOnlyDictionary<TKey, TValue> right,
        string path, Action<TValue, TValue, string> compare, Action<string> add) where TKey : notnull
    {
        foreach (var key in left.Keys.Union(right.Keys).OrderBy(key => key.ToString(), StringComparer.Ordinal))
        {
            var itemPath = Child(path, key.ToString()!);
            if (!left.TryGetValue(key, out var a) || !right.TryGetValue(key, out var b)) add(itemPath);
            else compare(a, b, itemPath);
        }
    }
    private static void Configuration(ConfigurationValue left, ConfigurationValue right, string path, Action<string> add)
    {
        if (left.Equals(right)) return;
        if (left is ConfigurationObject a && right is ConfigurationObject b)
            Map(a.Members, b.Members, path, (x, y, p) => Configuration(x, y, p, add), add);
        else add(path); // Arrays are one ordered change; scalars and kind changes are one node.
    }
    private static void Resource(NormalizedResource a, NormalizedResource b, string p, Action<string> add)
    {
        Check(a.Type, b.Type, Child(p, "type"), add);
        Check(a.ProviderRef, b.ProviderRef, Child(p, "providerRef"), add);
        Check(a.DeletionPolicy, b.DeletionPolicy, Child(p, "deletionPolicy"), add);
        Configuration(a.Properties, b.Properties, Child(p, "properties"), add);
    }
    private static void Export(NormalizedResourceExport a, NormalizedResourceExport b, string p, Action<string> add)
    {
        Check(a.ResourceRef, b.ResourceRef, Child(p, "resourceRef"), add);
        Check(a.Contract, b.Contract, Child(p, "contract"), add);
        Check(a.AccessProfile, b.AccessProfile, Child(p, "accessProfile"), add);
    }
    private static void Component(NormalizedComponent a, NormalizedComponent b, string p, Action<string> add)
    {
        Check(a.DisplayName, b.DisplayName, Child(p, "displayName"), add);
        Map(a.Labels, b.Labels, Child(p, "labels"), (x, y, path) => Check(x, y, path, add), add);
        Map(a.Bindings, b.Bindings, Child(p, "bindings"), (x, y, path) => Binding(x, y, path, add), add);
        Map(a.Documents, b.Documents, Child(p, "documents"), (x, y, path) => Document(x, y, path, add), add);
        Map(a.Workloads, b.Workloads, Child(p, "workloads"), (x, y, path) => Workload(x, y, path, add), add);
    }
    private static void Binding(NormalizedBinding a, NormalizedBinding b, string p, Action<string> add)
    {
        if (a.Target.GetType() != b.Target.GetType()) add(Child(p, "target"));
        else if (a.Target is NormalizedLocalResourceTarget localA && b.Target is NormalizedLocalResourceTarget localB)
            Check(localA.ResourceRef, localB.ResourceRef, Child(Child(p, "target"), "resourceRef"), add);
        else if (a.Target is NormalizedApplicationExportTarget exportA && b.Target is NormalizedApplicationExportTarget exportB)
        {
            var target = Child(p, "target");
            Check(exportA.ApplicationId, exportB.ApplicationId, Child(target, "applicationId"), add);
            Check(exportA.ExportRef, exportB.ExportRef, Child(target, "exportRef"), add);
            Check(exportA.Contract, exportB.Contract, Child(target, "contract"), add);
        }
        Configuration(a.Options, b.Options, Child(p, "options"), add);
        if (a.DocumentProjections.Count != b.DocumentProjections.Count ||
            a.DocumentProjections.Where((item, i) => i < b.DocumentProjections.Count &&
                (item.DocumentRef != b.DocumentProjections[i].DocumentRef || item.Path != b.DocumentProjections[i].Path ||
                 item.RendererRef != b.DocumentProjections[i].RendererRef)).Any())
            add(Child(p, "documentProjections"));
    }
    private static void Document(NormalizedConfigurationDocument a, NormalizedConfigurationDocument b, string p, Action<string> add)
    {
        if (a.GetType() != b.GetType()) { add(p); return; }
        Check(a.FileName, b.FileName, Child(p, "fileName"), add);
        if (a is NormalizedStructuredDocument structuredA && b is NormalizedStructuredDocument structuredB)
        {
            Check(structuredA.Format, structuredB.Format, Child(p, "format"), add);
            Configuration(structuredA.Template, structuredB.Template, Child(p, "template"), add);
        }
        else if (a is NormalizedTextDocument textA && b is NormalizedTextDocument textB)
            Check(textA.Template, textB.Template, Child(p, "template"), add);
    }
    private static void Workload(NormalizedWorkload a, NormalizedWorkload b, string p, Action<string> add)
    {
        Check(a.Type, b.Type, Child(p, "type"), add);
        Check(a.ProviderRef, b.ProviderRef, Child(p, "providerRef"), add);
        Check(a.Profile, b.Profile, Child(p, "profile"), add);
        Configuration(a.Properties, b.Properties, Child(p, "properties"), add);
    }
    private static void Parameter(NormalizedParameterDefinition a, NormalizedParameterDefinition b, string p, Action<string> add)
    {
        if (a.GetType() != b.GetType()) { add(Child(p, "type")); return; }
        Check(a.Description, b.Description, Child(p, "description"), add);
        if (a.Default?.GetType() != b.Default?.GetType()) add(Child(p, "default"));
        else if (a.Default is NormalizedExpressionDefault expressionA && b.Default is NormalizedExpressionDefault expressionB)
            Check(expressionA.Source, expressionB.Source, Child(p, "defaultExpression"), add);
        else if (a.Default is NormalizedLiteralDefault literalA && b.Default is NormalizedLiteralDefault literalB &&
                 !ScalarEqual(literalA.Value, literalB.Value)) add(Child(p, "default"));
        if (a is NormalizedStringParameter stringA && b is NormalizedStringParameter stringB)
        {
            Check(stringA.MinLength, stringB.MinLength, Child(p, "minLength"), add);
            Check(stringA.MaxLength, stringB.MaxLength, Child(p, "maxLength"), add);
            Sequence(stringA.AllowedValues, stringB.AllowedValues, Child(p, "allowedValues"), add);
        }
        else if (a is NormalizedIntegerParameter integerA && b is NormalizedIntegerParameter integerB)
        {
            Check(integerA.Minimum, integerB.Minimum, Child(p, "minimum"), add);
            Check(integerA.Maximum, integerB.Maximum, Child(p, "maximum"), add);
            Sequence(integerA.AllowedValues, integerB.AllowedValues, Child(p, "allowedValues"), add);
        }
        else if (a is NormalizedNumberParameter numberA && b is NormalizedNumberParameter numberB)
        {
            Check(numberA.Minimum, numberB.Minimum, Child(p, "minimum"), add);
            Check(numberA.Maximum, numberB.Maximum, Child(p, "maximum"), add);
            Sequence(numberA.AllowedValues, numberB.AllowedValues, Child(p, "allowedValues"), add);
        }
    }
    private static bool ScalarEqual(NormalizedParameterScalar a, NormalizedParameterScalar b) => (a, b) switch
    {
        (NormalizedStringScalar x, NormalizedStringScalar y) => x.Value == y.Value,
        (NormalizedBooleanScalar x, NormalizedBooleanScalar y) => x.Value == y.Value,
        (NormalizedIntegerScalar x, NormalizedIntegerScalar y) => x.Value.Equals(y.Value),
        (NormalizedNumberScalar x, NormalizedNumberScalar y) => x.Value.Equals(y.Value),
        _ => false
    };
    private static void Sequence<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b, string path, Action<string> add)
    {
        if (a is null && b is null) return;
        if (a is null || b is null || !a.SequenceEqual(b)) add(path);
    }
}
