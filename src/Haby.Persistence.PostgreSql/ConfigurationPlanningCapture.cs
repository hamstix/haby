using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Transaction-owned scope synchronization, including keys with no assignment row.</summary>
internal static class ConfigurationPlanningCapture
{
    internal static async Task LockAsync(ManifestCatalogDbContext context, Guid environment,
        IEnumerable<ProfileAssignmentTarget> targets, bool shared, CancellationToken token)
    {
        // Every multi-scope reader acquires the same ordinal key order. Writers acquire
        // one exclusive key before reading/CAS, including Unchanged and first insertion.
        // Locks are transaction-scoped; caller cancellation reaches PostgreSQL lock waits.
        await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '15s'", token);
        foreach (var key in targets.Select(t => LockKey(environment, t)).Order(StringComparer.Ordinal))
            if (shared) await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock_shared(hashtextextended({key}, 0))", token);
            else await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", token);
    }

    internal static string LockKey(Guid environment, ProfileAssignmentTarget target)
    {
        var (kind, app, component) = Key(target);
        return $"haby-configuration:{environment:N}:{kind}:{app}:{component}";
    }
    private static (string Kind, string App, string Component) Key(ProfileAssignmentTarget target) => target switch
    {
        ProfileAssignmentTarget.Environment => ("environment", "", ""),
        ProfileAssignmentTarget.Application a => ("application", a.ApplicationId.Value, ""),
        ProfileAssignmentTarget.Component c => ("component", c.ApplicationId.Value, c.ComponentRef.Value),
        _ => throw new InvalidDataException("Unsupported configuration scope.")
    };

    internal static async Task<IReadOnlyList<ConfigurationPlanningScope>> ReadScopesAsync(ManifestCatalogDbContext context,
        EnvironmentMetadata environment, NormalizedApplicationManifest manifest, CancellationToken token)
    {
        var targets = ConfigurationPlanningSnapshot.Targets(manifest);
        var id = Guid.Parse(environment.RecordId.ToString());
        await LockAsync(context, id, targets, true, token);
        var result = new List<ConfigurationPlanningScope>();
        foreach (var target in targets)
        {
            var (kind, app, component) = Key(target);
            var row = await context.ProfileAssignments.AsNoTracking().SingleOrDefaultAsync(x =>
                x.EnvironmentRecordId == id && x.TargetKind == kind && x.ApplicationId == app && x.ComponentRef == component, token);
            var stored = row is null ? null : PostgreSqlProfileAssignmentStore.Rehydrate(row, environment, target);
            result.Add(new(target, stored?.Token, stored?.Declaration ?? new(target, [])));
        }
        return result;
    }

    internal static async Task<(ConfigurationPlanningSnapshot? Snapshot, ProfileExpansionResult Expansion)> CaptureAsync(
        ManifestCatalogDbContext context, EnvironmentMetadata environment, ManifestRevisionRef revision,
        NormalizedApplicationManifest manifest, CancellationToken token, ConfigurationRenderingCatalog rendering, IReadOnlyList<ConfigurationRenderPin>? frozenPins = null, IReadOnlyList<DurableOperationNode>? producers = null)
    {
        var scopes = await ReadScopesAsync(context, environment, manifest, token);
        var profiles = new List<ConfigurationProfileRevision>();
        foreach (var reference in scopes.SelectMany(x => x.Declaration.Decisions).OfType<ProfileDecision.Use>()
            .Select(x => x.Reference).Distinct())
        {
            var row = await context.ProfileRevisions.AsNoTracking().SingleOrDefaultAsync(x =>
                x.ProfileId == reference.ProfileId.Value && x.Revision == reference.Revision, token);
            if (row is null) throw new InvalidDataException("Selected Profile revision is unavailable.");
            profiles.Add(ProfileSnapshotCodec.Decode(row.Snapshot, row.ProfileId, row.Revision, row.SnapshotFormatVersion, row.ModelVersion));
        }
        return ConfigurationPlanningSnapshot.PrepareCore(revision, manifest, scopes, profiles, rendering, frozenPins, producers);
    }

    internal static bool SameScopes(IReadOnlyList<ConfigurationPlanningScope> left, IReadOnlyList<ConfigurationPlanningScope> right) =>
        left.Count == right.Count && left.Zip(right).All(x => x.First.Target == x.Second.Target &&
            x.First.Token == x.Second.Token && x.First.Declaration.Equals(x.Second.Declaration));
}
