using System.Collections.Immutable;
using Haby.Application;
using Haby.Domain;
using ApplicationId = Haby.Domain.ApplicationId;
using static Haby.Kubernetes.Contracts.KubernetesContract;

namespace Haby.Kubernetes.Contracts;

/// <summary>Exact Placement binding; namespace authority is the immutable declaration.</summary>
public sealed record PlacementRevisionInput(EnvironmentRecordId Environment, ProviderAlias Alias,
    ProviderInstanceId Provider, ProviderConfigurationRevisionNumber Configuration,
    ProviderImplementationId Implementation, KubernetesPlacement Declaration);
public sealed record PlacementScope(EnvironmentRecordId Environment, ProviderAlias Alias,
    ApplicationId? Application = null, ManifestLocalId? Component = null, ManifestLocalId? Selector = null);
public sealed record PlacementAssignment(PlacementScope Scope, PlacementReference Revision);

/// <summary>Detaches mutable caller collections at construction; selection has no IO or current-alias lookup.</summary>
public sealed class PlacementInputs
{
    public PlacementInputs(IEnumerable<PlacementRevisionInput> revisions, IEnumerable<PlacementAssignment> assignments)
    {
        Revisions = revisions.ToImmutableArray(); Assignments = assignments.ToImmutableArray();
        Require(Revisions.Select(x => (x.Environment, x.Alias, x.Declaration.Reference)).Distinct().Count() == Revisions.Length, "placement.duplicate-revision");
        Require(Assignments.Select(x => x.Scope).Distinct().Count() == Assignments.Length, "placement.duplicate-assignment");
        Require(Assignments.All(x => (x.Scope.Component is null || x.Scope.Application is not null) &&
            (x.Scope.Selector is null || x.Scope.Application is null && x.Scope.Component is null)), "placement.scope");
    }
    public ImmutableArray<PlacementRevisionInput> Revisions { get; }
    public ImmutableArray<PlacementAssignment> Assignments { get; }
}
public sealed record ObjectName(ProviderInstanceId Provider, string Namespace, string Group, string Kind, string Name);
public sealed record EffectiveWorkload(ProviderInput.Workload Input, ExactProviderResolution Provider,
    PlacementRevisionInput Placement, KubernetesWorkload Declaration, string ImageReference,
    OperationalSettings Settings, ImmutableArray<ObjectName> Objects);

/// <summary>Pure immutable effective resolution; this result cannot be passed to durable Commit/Execute.</summary>
public static class KubernetesWorkloadPlanner
{
    public static ImmutableArray<EffectiveWorkload> Resolve(NormalizedApplicationManifest manifest, ApplicationVersion version,
        ExactProviderResolutionSet providers, PlacementInputs placements, string? expectedNamespace = null)
    {
        Require(providers.Declaration.Equals(manifest), "provider.declaration-mismatch");
        var results = ImmutableArray.CreateBuilder<EffectiveWorkload>();
        var names = new HashSet<ObjectName>();
        foreach (var (componentId, component) in manifest.Spec.Components.OrderBy(x => x.Key.Value, StringComparer.Ordinal))
            foreach (var (workloadId, workload) in component.Workloads.OrderBy(x => x.Key.Value, StringComparer.Ordinal))
            {
                var input = new ProviderInput.Workload(componentId, workloadId);
                Require(providers.Entries.TryGetValue(input, out var provider) && provider.Alias == workload.ProviderRef, "provider.missing");
                var exact = provider!;
                var scope = new PlacementScope(providers.Environment, workload.ProviderRef);
                var candidates = workload.PlacementRef is { } selector ? new[] { scope with { Selector = selector } } :
                    new[] { scope with { Application = manifest.ApplicationId, Component = componentId }, scope with { Application = manifest.ApplicationId }, scope };
                var assignment = candidates.Select(s => placements.Assignments.SingleOrDefault(x => x.Scope == s)).FirstOrDefault(x => x is not null);
                Require(assignment is not null, "placement.missing-selection");
                var placement = placements.Revisions.SingleOrDefault(x => x.Environment == providers.Environment && x.Alias == exact.Alias && x.Declaration.Reference == assignment!.Revision);
                Require(placement is not null, "placement.missing-revision");
                Require(placement!.Provider == exact.Instance && placement.Configuration == exact.Configuration && placement.Implementation == exact.Implementation, "placement.provider-mismatch");
                var selected = placement.Declaration;
                Require(expectedNamespace is null || expectedNamespace == selected.Namespace, "placement.namespace-mismatch");
                var declaration = Decode(workload);
                foreach (var document in declaration.Documents) Require(component.Documents.ContainsKey(document), "configuration.document-reference");
                var settings = Effective(selected.Settings, declaration.Overrides);
                Require(settings.Ingress!.Enabled == false || settings.Service == true && declaration.Routes.Length > 0, "ingress.routes-service");
                Require(settings.Service != true && settings.HeadlessService != true || declaration.Ports.Count > 0, "service.ports");
                foreach (var route in declaration.Routes)
                    if (settings.Ingress.Enabled) Dns(route.HostLabel + "." + settings.Ingress.Domain);
                var image = declaration.Image switch
                {
                    WorkloadImage.Complete c => c.Reference,
                    WorkloadImage.Composed c => Compose(c, selected, version),
                    _ => throw new WorkloadContractException("image.kind")
                };
                ImageReference(image);
                var baseName = declaration.NameOverride ?? manifest.ApplicationId.Value + "-" + componentId.Value + "-" + workloadId.Value;
                var objects = ImmutableArray.CreateBuilder<ObjectName>();
                void Add(string group, string kind, string name)
                {
                    Dns(name);
                    if (kind == "Service") Require(name.Length <= 63 && Match(name, "[a-z](?:[a-z0-9-]*[a-z0-9])?"), "name.service");
                    var key = new ObjectName(exact.Instance, selected.Namespace, group, kind, name);
                    Require(names.Add(key), "name.collision"); objects.Add(key);
                }
                Add("apps", "Deployment", baseName);
                if (settings.Service == true) Add("", "Service", baseName);
                if (settings.HeadlessService == true) Add("", "Service", baseName + "-headless");
                if (settings.Ingress.Enabled) Add("networking.k8s.io", "Ingress", baseName);
                if (!declaration.Documents.IsEmpty) Add("", "ServiceAccount", baseName + "-runtime");
                results.Add(new(input, exact, placement, declaration, image, settings, objects.ToImmutable()));
            }
        return results.ToImmutable();
    }
    private static string Compose(WorkloadImage.Composed image, KubernetesPlacement placement, ApplicationVersion version)
    {
        Require(placement.Registry is not null, "image.registry-required");
        var tag = image.FromApplicationVersion ? version.Value : image.Tag!; Tag(tag);
        return placement.Registry + "/" + (placement.RepositoryPrefix is null ? "" : placement.RepositoryPrefix + "/") + image.Name + ":" + tag;
    }
    private static OperationalSettings Effective(OperationalSettings defaults, OperationalSettings overrides) => new(
        overrides.Replicas ?? defaults.Replicas ?? 1,
        overrides.Strategy ?? defaults.Strategy ?? new("RollingUpdate", new(25, true), new(25, true)),
        overrides.MinReadySeconds ?? defaults.MinReadySeconds ?? 0,
        overrides.RolloutSeconds ?? defaults.RolloutSeconds ?? 600,
        overrides.Resources ?? defaults.Resources,
        overrides.Service ?? defaults.Service ?? false,
        overrides.HeadlessService ?? defaults.HeadlessService ?? false,
        overrides.Ingress ?? defaults.Ingress ?? new(false, null, null, null));
}
