using Haby.Domain;
using Haby.Manifest;

namespace Haby.Application;

/// <summary>Pure capability identity for the single checkpoint implementation.</summary>
public sealed class PostgreSqlDatabaseCheckpointCapability : ICheckpointProviderCapability
{
    public const string ImplementationId = "haby.postgresql.database/v1alpha1";
    public bool Supports(ProviderResolution selection, ResourceTypeId contract) =>
        contract == PostgreSqlDatabaseContract.Id && selection.ImplementationId == ImplementationId;
}

/// <summary>Shared checkpoint alias resolution and exact implementation capability rules.</summary>
internal static class CheckpointPlanningRules
{
    internal static object Resolve(NormalizedApplicationManifest declaration,
        Func<ProviderAlias, ProviderResolution?> select, ICheckpointProviderCapability? capability)
    {
        if (declaration.Spec.Parameters.Count != 0 || declaration.Spec.Components.Values.Any(component =>
                component.Bindings.Values.Any(binding => binding.Target is NormalizedApplicationExportTarget)))
            return new PlanChangeSetResult.UnsupportedDependency();
        var aliases = declaration.Spec.Resources.Values.Select(item => item.ProviderRef)
            .Concat(declaration.Spec.Components.Values.SelectMany(component => component.Workloads.Values.Select(item => item.ProviderRef)))
            .Distinct().OrderBy(item => item.Value, StringComparer.Ordinal);
        var resolved = new List<ResolvedProviderAlias>();
        foreach (var alias in aliases)
        {
            var provider = select(alias);
            if (provider is null) return new PlanChangeSetResult.ProviderAliasNotResolved(alias);
            resolved.Add(new ResolvedProviderAlias(alias, provider));
        }
        foreach (var resource in declaration.Spec.Resources.Values)
        {
            if (resource.Type != PostgreSqlDatabaseContract.Id) continue;
            var selected = resolved.Single(item => item.Alias == resource.ProviderRef);
            if (capability?.Supports(selected.Resolution, resource.Type) != true)
                return new PlanChangeSetResult.ProviderCapabilityMismatch(resource.ProviderRef);
        }
        return resolved;
    }
}
