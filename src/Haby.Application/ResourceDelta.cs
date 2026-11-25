using System.Collections.Immutable;
using Haby.Domain;

namespace Haby.Application;

public enum ResourceDeltaKind { Unchanged, Added, Changed, Removed }
public sealed record ResourceDeltaEntry(ManifestLocalId Resource, ResourceDeltaKind Kind);

/// <summary>Stable declaration identity classification; renaming is removal plus addition.</summary>
public sealed class ResourceDelta
{
    public ResourceDelta(NormalizedManifestSpec accepted, NormalizedManifestSpec target)
    {
        Entries = accepted.Resources.Keys.Union(target.Resources.Keys).OrderBy(x => x.Value, StringComparer.Ordinal)
            .Select(id => new ResourceDeltaEntry(id, !accepted.Resources.TryGetValue(id, out var before) ? ResourceDeltaKind.Added
                : !target.Resources.TryGetValue(id, out var after) ? ResourceDeltaKind.Removed
                : before.Type == after.Type && before.ProviderRef == after.ProviderRef && before.DeletionPolicy == after.DeletionPolicy && before.Properties.Equals(after.Properties)
                    ? ResourceDeltaKind.Unchanged : ResourceDeltaKind.Changed)).ToImmutableArray();
    }
    public IReadOnlyList<ResourceDeltaEntry> Entries { get; }
}
