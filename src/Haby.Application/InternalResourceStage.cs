using System.Collections.Immutable;
using Haby.Domain;

namespace Haby.Application;

/// <summary>A pure Resource-stage view for the internal mixed-generation capability.
/// The accepted snapshot and fingerprint always retain the complete declaration.</summary>
internal static class InternalResourceStage
{
    // Always inspect the original complete inputs, never the Resource projection.
    internal static bool IsEmpty(DurablePlanningInputs inputs, DurableOperationGraph graph) =>
        inputs.Configuration is { } configuration && configuration.Manifest.Equals(inputs.Providers.Declaration) &&
        configuration.Manifest.Spec.Resources.Count == 0 &&
        configuration.Manifest.Spec.Components.Values.Any(c => c.Workloads.Count > 0) &&
        configuration.Manifest.Spec.Components.Values.All(c => c.Bindings.Count == 0 && c.Documents.Count == 0) &&
        inputs.Providers.Entries.Keys.All(x => x is ProviderInput.Workload) &&
        graph.Baseline is null && graph.Nodes.Count == 0 && graph.ReusedResources.Count == 0 && graph.RetainedResources.Count == 0;

    internal static DurablePlanningInputs Project(DurablePlanningInputs inputs)
    {
        inputs.Validate();
        var original = inputs.Providers.Declaration;
        if (original.Spec.Components.Values.All(c => c.Workloads.Count == 0)) return inputs;
        var spec = original.Spec;
        var manifest = new NormalizedApplicationManifest(original.InputApiVersion, original.Metadata,
            new(spec.Parameters.ToImmutableDictionary(), spec.Resources.ToImmutableDictionary(),
                spec.Exports.ToImmutableDictionary(), spec.Components.ToImmutableDictionary(x => x.Key,
                    x => new NormalizedComponent(x.Value.DisplayName, x.Value.Labels.ToImmutableDictionary(),
                        x.Value.Bindings.ToImmutableDictionary(), x.Value.Documents.ToImmutableDictionary(), []))));
        var frozen = inputs.Configuration ?? throw new InvalidDataException("Configuration snapshot unavailable.");
        var configuration = ConfigurationPlanningSnapshot.PrepareCore(inputs.Revision, manifest,
            frozen.Scopes, frozen.Profiles, frozenPins: frozen.RenderPins).Snapshot
            ?? throw new InvalidDataException("Resource-stage configuration unavailable.");
        return inputs with
        {
            Configuration = configuration,
            Providers = new(manifest, inputs.Environment,
            inputs.Providers.Entries.Values.Where(x => x.Input is ProviderInput.Resource))
        };
    }
}
