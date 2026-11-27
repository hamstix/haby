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
        inputs.Providers.Entries.Keys.All(x => x is ProviderInput.Workload) &&
        (graph.Baseline is null || graph.WorkloadUpdate is not null) && graph.Nodes.Count == 0 && graph.ReusedResources.Count == 0 && graph.RetainedResources.Count == 0;

    // Only the existing initial Provision graph may precede a first Workload rollout.
    internal static bool IsInitialCreate(DurablePlanningInputs inputs, DurableOperationGraph graph) =>
        inputs.Configuration is { } configuration && configuration.Manifest.Equals(inputs.Providers.Declaration) &&
        configuration.Manifest.Spec.Resources.Count > 0 && configuration.Manifest.Spec.Components.Values.Any(c => c.Workloads.Count > 0) &&
        graph.Baseline is null && graph.ReusedResources.Count == 0 && graph.RetainedResources.Count == 0 && graph.Import is null &&
        graph.Nodes.Count > 0 && graph.Nodes.All(x => x.Action.Admission.Mode == ResourceAcquisitionMode.Provision) &&
        inputs.Acquisitions.Resources.All(x => x.Mode == ResourceAcquisitionMode.Provision) &&
        ResourceReadinessCoverage.IsComplete(configuration.Manifest.Spec.Resources.Keys.Select(x => x.Value),
            graph.Nodes.Select(x => x.ResourceId.Value), []);

    internal static DurableOperationGraph Project(DurableOperationGraph graph) =>
        new(graph.Nodes, graph.ReusedResources, graph.Baseline, graph.RetainedResources)
        { Import = graph.Import, Acquisitions = graph.Acquisitions, ObservationGroups = graph.ObservationGroups };

    internal static DurablePlanningInputs Structure(DurablePlanningInputs inputs)
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
        return inputs with
        {
            Configuration = null,
            Providers = new(manifest, inputs.Environment,
            inputs.Providers.Entries.Values.Where(x => x.Input is ProviderInput.Resource))
        };
    }
    internal static DurablePlanningInputs Project(DurablePlanningInputs inputs)
    {
        var structural = Structure(inputs);
        var frozen = inputs.Configuration ?? throw new InvalidDataException("Configuration snapshot unavailable.");
        var configuration = ConfigurationPlanningSnapshot.PrepareCore(inputs.Revision, structural.Providers.Declaration,
            frozen.Scopes, frozen.Profiles, frozenPins: frozen.RenderPins, frozenSecrets: frozen.FrozenSecrets).Snapshot
            ?? throw new InvalidDataException("Resource-stage configuration unavailable.");
        return structural with { Configuration = configuration };
    }

}
