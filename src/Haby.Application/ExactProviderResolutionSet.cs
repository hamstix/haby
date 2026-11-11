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
public sealed record ExactAdapterInput(VersionedContractId Contract, ConfigurationObject Value);

/// <summary>Explicit immutable resolution supplied by a caller; no live lookup or executable authority.</summary>
public sealed record ExactProviderResolution(ProviderInput Input, EnvironmentRecordId Environment,
    ProviderAlias Alias, ProviderInstanceId Instance, ProviderConfigurationRevisionNumber Configuration,
    ProviderImplementationId Implementation, ExactAdapterInput? Naming = null, ExactAdapterInput? Placement = null);

/// <summary>Pure resolution boundary. Durable Plan/Commit/runner still use their existing guarded pilot contract.</summary>
public sealed class ExactProviderResolutionSet
{
    public ExactProviderResolutionSet(NormalizedApplicationManifest manifest, EnvironmentRecordId environment,
        IEnumerable<ExactProviderResolution> resolutions)
    {
        var entries = resolutions.ToImmutableArray();
        var expected = manifest.Spec.Resources.Select(x => (Input: (ProviderInput)new ProviderInput.Resource(x.Key), Alias: x.Value.ProviderRef))
            .Concat(manifest.Spec.Components.SelectMany(c => c.Value.Workloads.Select(w =>
                (Input: (ProviderInput)new ProviderInput.Workload(c.Key, w.Key), Alias: w.Value.ProviderRef)))).ToArray();
        if (entries.Length != expected.Length || entries.Select(x => x.Input).Distinct().Count() != entries.Length ||
            entries.Any(x => x.Environment != environment || !expected.Contains((x.Input, x.Alias))))
            throw new ArgumentException("Exact Provider inputs do not match the declaration and Environment.", nameof(resolutions));
        if (entries.GroupBy(x => x.Alias).Any(group => group.Select(x =>
                (x.Instance, x.Configuration, x.Implementation)).Distinct().Count() != 1))
            throw new ArgumentException("One alias cannot select conflicting exact Provider inputs.", nameof(resolutions));
        Declaration = manifest;
        Environment = environment;
        Entries = entries.ToImmutableDictionary(x => x.Input);
    }
    public NormalizedApplicationManifest Declaration { get; }
    public EnvironmentRecordId Environment { get; }
    public ImmutableDictionary<ProviderInput, ExactProviderResolution> Entries { get; }
}
