using Haby.Application;
using Haby.Domain;
using ApplicationId = Haby.Domain.ApplicationId;

namespace Haby.Kubernetes.Contracts;

/// <summary>Adapter-owned pure bridge for explicitly supplied persisted snapshots and synthetic/exact Provider inputs.
/// This creates no durable admission, freshness capture or execution authority.</summary>
public static class PersistedPlacementInputs
{
    public static PlacementInputs Create(IEnumerable<AcceptedPlacementRevision> revisions,
        IEnumerable<PersistedPlacementSelection> selections, ExactProviderResolutionSet providers)
    {
        var accepted = revisions.ToDictionary(x => x.Key);
        var chosen = selections.ToArray();
        var bindings = new List<PlacementRevisionInput>();
        var assignments = new List<PlacementAssignment>();
        foreach (var selection in chosen)
        {
            var key = selection.Reference;
            if (key.Environment != providers.Environment) continue;
            var exact = providers.Entries.Values.FirstOrDefault(x => x.Alias == key.Alias && x.Input is ProviderInput.Workload);
            if (exact is null) continue;
            if (!accepted.TryGetValue(key, out var revision)) throw new InvalidDataException("Selected Placement revision is unavailable.");
            var typed = KubernetesPlacementCodec.ToTyped(revision.Declaration);
            if (typed.Reference.Name != key.Name || typed.Reference.Revision != key.Revision) throw new InvalidDataException("Selected Placement revision is unavailable.");
            if (!bindings.Any(x => x.Environment == key.Environment && x.Alias == key.Alias && x.Declaration.Reference == typed.Reference))
                bindings.Add(new(key.Environment, key.Alias, exact.Instance, exact.Configuration, exact.Implementation, typed));
            var target = selection.Target;
            assignments.Add(new(new(key.Environment, key.Alias, target.Application == "" ? null : ApplicationId.Create(target.Application),
                target.Component == "" ? null : ManifestLocalId.Create(target.Component), target.Selector == "" ? null : ManifestLocalId.Create(target.Selector)), typed.Reference));
        }
        return new(bindings, assignments);
    }
}
