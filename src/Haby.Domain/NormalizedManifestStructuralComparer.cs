namespace Haby.Domain;

/// <summary>Explicit structural comparison of the closed typed declaration graph.</summary>
internal static class NormalizedManifestStructuralComparer
{
    internal static bool Equals(NormalizedApplicationManifest? left, NormalizedApplicationManifest? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null &&
        left.NormalizedModelVersion == right.NormalizedModelVersion &&
        left.Kind == right.Kind && Same(left.InputApiVersion, right.InputApiVersion) &&
        Metadata(left.Metadata, right.Metadata) && Spec(left.Spec, right.Spec);

    internal static int GetHashCode(NormalizedApplicationManifest value) =>
        HashCode.Combine(value.NormalizedModelVersion, value.Kind, Hash(value.InputApiVersion),
            MetadataHash(value.Metadata), SpecHash(value.Spec));

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
    private static int Hash(string? value) => value is null ? 0 : StringComparer.Ordinal.GetHashCode(value);

    private static bool Metadata(NormalizedManifestMetadata left, NormalizedManifestMetadata right) =>
        left.Id == right.Id && Same(left.DisplayName, right.DisplayName) &&
        Map(left.Labels, right.Labels, Same);
    private static int MetadataHash(NormalizedManifestMetadata value) =>
        HashCode.Combine(value.Id, Hash(value.DisplayName), MapHash(value.Labels, key => key, Hash));

    private static bool Spec(NormalizedManifestSpec left, NormalizedManifestSpec right) =>
        Map(left.Parameters, right.Parameters, Parameter) &&
        Map(left.Resources, right.Resources, Resource) &&
        Map(left.Exports, right.Exports, Export) &&
        Map(left.Components, right.Components, Component);
    private static int SpecHash(NormalizedManifestSpec value) =>
        HashCode.Combine(MapHash(value.Parameters, key => key.Value, ParameterHash),
            MapHash(value.Resources, key => key.Value, ResourceHash),
            MapHash(value.Exports, key => key.Value, ExportHash),
            MapHash(value.Components, key => key.Value, ComponentHash));

    private static bool Resource(NormalizedResource left, NormalizedResource right) =>
        left.Type == right.Type && left.ProviderRef == right.ProviderRef &&
        left.DeletionPolicy == right.DeletionPolicy && left.Properties.Equals(right.Properties);
    private static int ResourceHash(NormalizedResource value) =>
        HashCode.Combine(value.Type, value.ProviderRef, value.DeletionPolicy, value.Properties);

    private static bool Export(NormalizedResourceExport left, NormalizedResourceExport right) =>
        left.ResourceRef == right.ResourceRef && left.Contract == right.Contract &&
        left.AccessProfile == right.AccessProfile;
    private static int ExportHash(NormalizedResourceExport value) =>
        HashCode.Combine(value.ResourceRef, value.Contract, value.AccessProfile);

    private static bool Component(NormalizedComponent left, NormalizedComponent right) =>
        Same(left.DisplayName, right.DisplayName) && Map(left.Labels, right.Labels, Same) &&
        Map(left.Bindings, right.Bindings, Binding) &&
        Map(left.Documents, right.Documents, Document) &&
        Map(left.Workloads, right.Workloads, Workload);
    private static int ComponentHash(NormalizedComponent value) =>
        HashCode.Combine(Hash(value.DisplayName), MapHash(value.Labels, key => key, Hash),
            MapHash(value.Bindings, key => key.Value, BindingHash),
            MapHash(value.Documents, key => key.Value, DocumentHash),
            MapHash(value.Workloads, key => key.Value, WorkloadHash));

    private static bool Binding(NormalizedBinding left, NormalizedBinding right) =>
        Target(left.Target, right.Target) && left.Options.Equals(right.Options) &&
        Sequence(left.DocumentProjections, right.DocumentProjections, Projection);
    private static int BindingHash(NormalizedBinding value) =>
        HashCode.Combine(TargetHash(value.Target), value.Options, SequenceHash(value.DocumentProjections, ProjectionHash));

    private static bool Target(NormalizedBindingTarget left, NormalizedBindingTarget right) => (left, right) switch
    {
        (NormalizedLocalResourceTarget a, NormalizedLocalResourceTarget b) => a.ResourceRef == b.ResourceRef,
        (NormalizedApplicationExportTarget a, NormalizedApplicationExportTarget b) =>
            a.ApplicationId == b.ApplicationId && a.ExportRef == b.ExportRef && a.Contract == b.Contract,
        _ => false
    };
    private static int TargetHash(NormalizedBindingTarget value) => value switch
    {
        NormalizedLocalResourceTarget item => HashCode.Combine(0, item.ResourceRef),
        NormalizedApplicationExportTarget item => HashCode.Combine(1, item.ApplicationId, item.ExportRef, item.Contract),
        _ => throw new ArgumentException("Unknown normalized target.", nameof(value))
    };

    private static bool Projection(NormalizedDocumentProjection left, NormalizedDocumentProjection right) =>
        left.DocumentRef == right.DocumentRef && left.Path == right.Path && left.RendererRef == right.RendererRef;
    private static int ProjectionHash(NormalizedDocumentProjection value) =>
        HashCode.Combine(value.DocumentRef, value.Path, value.RendererRef);

    private static bool Document(NormalizedConfigurationDocument left, NormalizedConfigurationDocument right) => (left, right) switch
    {
        (NormalizedStructuredDocument a, NormalizedStructuredDocument b) =>
            Same(a.FileName, b.FileName) && a.Format == b.Format && a.Template.Equals(b.Template),
        (NormalizedTextDocument a, NormalizedTextDocument b) =>
            Same(a.FileName, b.FileName) && Same(a.Template, b.Template),
        _ => false
    };
    private static int DocumentHash(NormalizedConfigurationDocument value) => value switch
    {
        NormalizedStructuredDocument item => HashCode.Combine(0, Hash(item.FileName), item.Format, item.Template),
        NormalizedTextDocument item => HashCode.Combine(1, Hash(item.FileName), Hash(item.Template)),
        _ => throw new ArgumentException("Unknown normalized document.", nameof(value))
    };

    private static bool Workload(NormalizedWorkload left, NormalizedWorkload right) =>
        left.Type == right.Type && left.ProviderRef == right.ProviderRef &&
        left.Profile == right.Profile && left.Properties.Equals(right.Properties);
    private static int WorkloadHash(NormalizedWorkload value) =>
        HashCode.Combine(value.Type, value.ProviderRef, value.Profile, value.Properties);

    private static bool Parameter(NormalizedParameterDefinition left, NormalizedParameterDefinition right)
    {
        if (!Same(left.Description, right.Description) || !Default(left.Default, right.Default)) return false;
        return (left, right) switch
        {
            (NormalizedStringParameter a, NormalizedStringParameter b) =>
                a.MinLength == b.MinLength && a.MaxLength == b.MaxLength &&
                OptionalSequence(a.AllowedValues, b.AllowedValues, Same),
            (NormalizedBooleanParameter, NormalizedBooleanParameter) => true,
            (NormalizedIntegerParameter a, NormalizedIntegerParameter b) =>
                object.Equals(a.Minimum, b.Minimum) && object.Equals(a.Maximum, b.Maximum) &&
                OptionalSequence(a.AllowedValues, b.AllowedValues, object.Equals),
            (NormalizedNumberParameter a, NormalizedNumberParameter b) =>
                object.Equals(a.Minimum, b.Minimum) && object.Equals(a.Maximum, b.Maximum) &&
                OptionalSequence(a.AllowedValues, b.AllowedValues, object.Equals),
            _ => false
        };
    }
    private static int ParameterHash(NormalizedParameterDefinition value)
    {
        var common = HashCode.Combine(Hash(value.Description), DefaultHash(value.Default));
        return value switch
        {
            NormalizedStringParameter item => HashCode.Combine(0, common, item.MinLength, item.MaxLength,
                OptionalSequenceHash(item.AllowedValues, Hash)),
            NormalizedBooleanParameter => HashCode.Combine(1, common),
            NormalizedIntegerParameter item => HashCode.Combine(2, common, item.Minimum, item.Maximum,
                OptionalSequenceHash(item.AllowedValues, x => x.GetHashCode())),
            NormalizedNumberParameter item => HashCode.Combine(3, common, item.Minimum, item.Maximum,
                OptionalSequenceHash(item.AllowedValues, x => x.GetHashCode())),
            _ => throw new ArgumentException("Unknown normalized parameter.", nameof(value))
        };
    }
    private static bool Default(NormalizedParameterDefault? left, NormalizedParameterDefault? right) => (left, right) switch
    {
        (null, null) => true,
        (NormalizedExpressionDefault a, NormalizedExpressionDefault b) => Same(a.Source, b.Source),
        (NormalizedLiteralDefault a, NormalizedLiteralDefault b) => Scalar(a.Value, b.Value),
        _ => false
    };
    private static int DefaultHash(NormalizedParameterDefault? value) => value switch
    {
        null => 0,
        NormalizedExpressionDefault item => HashCode.Combine(1, Hash(item.Source)),
        NormalizedLiteralDefault item => HashCode.Combine(2, ScalarHash(item.Value)),
        _ => throw new ArgumentException("Unknown normalized default.", nameof(value))
    };
    private static bool Scalar(NormalizedParameterScalar left, NormalizedParameterScalar right) => (left, right) switch
    {
        (NormalizedStringScalar a, NormalizedStringScalar b) => Same(a.Value, b.Value),
        (NormalizedBooleanScalar a, NormalizedBooleanScalar b) => a.Value == b.Value,
        (NormalizedIntegerScalar a, NormalizedIntegerScalar b) => a.Value.Equals(b.Value),
        (NormalizedNumberScalar a, NormalizedNumberScalar b) => a.Value.Equals(b.Value),
        _ => false
    };
    private static int ScalarHash(NormalizedParameterScalar value) => value switch
    {
        NormalizedStringScalar item => HashCode.Combine(0, Hash(item.Value)),
        NormalizedBooleanScalar item => HashCode.Combine(1, item.Value),
        NormalizedIntegerScalar item => HashCode.Combine(2, item.Value),
        NormalizedNumberScalar item => HashCode.Combine(3, item.Value),
        _ => throw new ArgumentException("Unknown normalized scalar.", nameof(value))
    };

    private static bool Map<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> left, IReadOnlyDictionary<TKey, TValue> right,
        Func<TValue, TValue, bool> same) where TKey : notnull =>
        left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var candidate) && same(item.Value, candidate));
    private static int MapHash<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> values,
        Func<TKey, string> keyText, Func<TValue, int> hash) where TKey : notnull
    {
        var result = new HashCode();
        foreach (var item in values.OrderBy(item => keyText(item.Key), StringComparer.Ordinal))
        {
            result.Add(keyText(item.Key), StringComparer.Ordinal);
            result.Add(hash(item.Value));
        }
        return result.ToHashCode();
    }
    private static bool Sequence<T>(IReadOnlyList<T> left, IReadOnlyList<T> right, Func<T, T, bool> same) =>
        left.Count == right.Count && Enumerable.Range(0, left.Count).All(index => same(left[index], right[index]));
    private static bool OptionalSequence<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right, Func<T, T, bool> same) =>
        left is null ? right is null : right is not null && Sequence(left, right, same);
    private static int SequenceHash<T>(IReadOnlyList<T> values, Func<T, int> hash)
    {
        var result = new HashCode();
        foreach (var item in values) result.Add(hash(item));
        return result.ToHashCode();
    }
    private static int OptionalSequenceHash<T>(IReadOnlyList<T>? values, Func<T, int> hash) =>
        values is null ? 0 : HashCode.Combine(1, SequenceHash(values, hash));
}
