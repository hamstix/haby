using System.Collections.Immutable;
using Haby.Domain;

namespace Haby.Application;

/// <summary>A declared input, never a first-match Provider alias.</summary>
public abstract record ProviderInput
{
    private ProviderInput() { }
    public sealed record Resource(ManifestLocalId Id) : ProviderInput;
    public sealed record Workload(ManifestLocalId Component, ManifestLocalId Id) : ProviderInput;
}

/// <summary>Adapter-owned non-secret exact data; common planning does not interpret its contents.</summary>
public sealed record ExactAdapterInput(AdapterInputContractId Contract, ConfigurationObject Value);

/// <summary>Explicit immutable resolution supplied by a caller; no live lookup or executable authority.</summary>
public sealed record ExactProviderResolution(ProviderInput Input, EnvironmentRecordId Environment,
    ProviderAlias Alias, ProviderInstanceId Instance, ProviderConfigurationRevisionNumber Configuration,
    ProviderImplementationId Implementation, ExactAdapterInput? Naming = null, ExactAdapterInput? Placement = null);

/// <summary>An explicitly uniform Resource capability context, never a substitute declaration key.</summary>
public sealed record UniformResourceProviderContext(EnvironmentRecordId Environment, ProviderAlias Alias,
    ProviderInstanceId Instance, ProviderConfigurationRevisionNumber Configuration, ProviderImplementationId Implementation,
    ExactAdapterInput? Naming, ExactAdapterInput? Placement);

/// <summary>Declaration-keyed immutable resolution shared by pure and durable planning; admission is separate.</summary>
public sealed class ExactProviderResolutionSet : IEquatable<ExactProviderResolutionSet>
{
    public ExactProviderResolutionSet(NormalizedApplicationManifest manifest, EnvironmentRecordId environment,
        IEnumerable<ExactProviderResolution> resolutions)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(resolutions);
        var entries = resolutions.ToImmutableArray();
        var expected = manifest.Spec.Resources.Select(x => (Input: (ProviderInput)new ProviderInput.Resource(x.Key), Alias: x.Value.ProviderRef))
            .Concat(manifest.Spec.Components.SelectMany(c => c.Value.Workloads.Select(w =>
                (Input: (ProviderInput)new ProviderInput.Workload(c.Key, w.Key), Alias: w.Value.ProviderRef)))).ToArray();
        if (entries.Any(x => x is null || x.Input is null || x.Environment is null || x.Alias is null || x.Instance is null || x.Configuration is null || x.Implementation is null) ||
            entries.Length != expected.Length || entries.Select(x => x.Input).Distinct().Count() != entries.Length ||
            entries.Any(x => x.Environment != environment || !expected.Contains((x.Input, x.Alias))))
            throw new ArgumentException("Exact Provider inputs do not match the declaration and Environment.", nameof(resolutions));
        if (entries.GroupBy(x => x.Alias).Any(group => group.Select(x =>
                (x.Instance, x.Configuration, x.Implementation)).Distinct().Count() != 1))
            throw new ArgumentException("One alias cannot select conflicting exact Provider inputs.", nameof(resolutions));
        foreach (var payload in entries.SelectMany(x => new[] { x.Naming, x.Placement }).OfType<ExactAdapterInput>())
        {
            if (payload.Contract is null || payload.Value is null) throw new ArgumentException("Invalid exact adapter input.", nameof(resolutions));
            ConfigurationDocumentCodec.ValidateTree(payload.Value);
            if (!ConfigurationJson.Serialize(payload.Value).IsSuccess) throw new ArgumentException("Exact adapter inputs must be non-secret.", nameof(resolutions));
        }
        Declaration = manifest;
        Environment = environment;
        Entries = entries.ToImmutableDictionary(x => x.Input);
    }
    public NormalizedApplicationManifest Declaration { get; }
    public EnvironmentRecordId Environment { get; }
    public ImmutableDictionary<ProviderInput, ExactProviderResolution> Entries { get; }

    public ExactProviderResolution Resource(ManifestLocalId id) => Entries.TryGetValue(new ProviderInput.Resource(id), out var entry)
        ? entry : throw new InvalidDataException("Exact Resource Provider input is unavailable.");

    /// <summary>Explicit current Resource-pilot capability check, never a first-entry projection.</summary>
    public UniformResourceProviderContext RequireUniformResourceProvider()
    {
        if (Entries.Count == 0 || Entries.Keys.Any(x => x is not ProviderInput.Resource))
            throw new InvalidDataException("The current Resource pilot requires Resource inputs.");
        var contexts = Entries.Values.Select(x => new UniformResourceProviderContext(x.Environment, x.Alias, x.Instance,
            x.Configuration, x.Implementation, x.Naming, x.Placement)).Distinct().Take(2).ToArray();
        return contexts is [var context] ? context : throw new InvalidDataException("The current Resource pilot requires one exact Provider context.");
    }

    public bool Equals(ExactProviderResolutionSet? other) => other is not null && Environment == other.Environment &&
        Declaration.Equals(other.Declaration) && Entries.Count == other.Entries.Count &&
        Entries.All(x => other.Entries.TryGetValue(x.Key, out var value) && x.Value == value);
    public override bool Equals(object? obj) => obj is ExactProviderResolutionSet other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ExactProviderResolutionSetCodec.Encode(this));
}
