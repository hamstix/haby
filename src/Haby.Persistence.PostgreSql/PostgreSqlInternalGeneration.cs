using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

public sealed partial class PostgreSqlDurableDeploymentPlanner
{
    internal async Task<ExactProviderResolutionSet> CaptureInternalProvidersAsync(ManifestCatalogDbContext db,
        NormalizedApplicationManifest manifest, EnvironmentRecordId environment, IEnumerable<ExactProviderResolution> resources, CancellationToken token)
    {
        var entries = resources.ToList();
        var env = Guid.Parse(environment.ToString());
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Exact action capture requires a transaction.");
        var aliases = manifest.Spec.Components.Values.SelectMany(x => x.Workloads.Values)
            .Select(x => x.ProviderRef.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // Match Commit's combined order: Provider alias rows first, then Placement scopes.
        // All scopes must be held before subject reads; component order can reverse aliases.
        foreach (var alias in aliases)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT environment_record_id FROM provider_aliases WHERE environment_record_id = {env} AND alias = {alias} FOR SHARE", token);
        foreach (var alias in aliases)
        {
            var lockKey = $"haby-placement:{env:N}:{alias}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", token);
        }
        foreach (var (component, declaration) in manifest.Spec.Components.OrderBy(x => x.Key.Value, StringComparer.Ordinal))
            foreach (var (id, workload) in declaration.Workloads.OrderBy(x => x.Key.Value, StringComparer.Ordinal))
            {
                var alias = workload.ProviderRef.Value;
                var binding = await db.ProviderAliases.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentRecordId == env && x.Alias == alias, token)
                    ?? throw new InvalidDataException("Exact action Provider unavailable.");
                var revision = await db.ProviderConfigurationRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentRecordId == env &&
                    x.ProviderInstanceId == binding.ProviderInstanceId && x.RevisionNumber == binding.RevisionNumber && x.IsSealed, token)
                    ?? throw new InvalidDataException("Exact action configuration unavailable.");
                if (!await CodecAvailableAsync(db, revision, token)) throw new InvalidDataException("Exact action configuration codec unavailable.");
                var selection = await SelectPlacementAsync(db, env, alias, manifest.ApplicationId.Value, component.Value, workload.PlacementRef?.Value, token);
                var placement = await db.PlacementRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == selection && x.EnvironmentRecordId == env && x.ProviderAlias == alias, token)
                    ?? throw new InvalidDataException("Exact Placement unavailable.");
                entries.Add(new(new ProviderInput.Workload(component, id), environment, workload.ProviderRef,
                    ProviderInstanceId.Parse(binding.ProviderInstanceId.ToString()), ProviderConfigurationRevisionNumber.Create(binding.RevisionNumber),
                    ProviderImplementationId.Create(revision.ImplementationId), Placement: InternalPlacementInput.Encode(placement.Id,
                        new(ManifestLocalId.Create(placement.Name), placement.Revision, placement.Contract, placement.FormatVersion, placement.Payload))));
            }
        return new(manifest, environment, entries);
    }

    internal static async Task<Guid> SelectPlacementAsync(ManifestCatalogDbContext db, Guid environment, string alias,
        string application, string component, string? selector, CancellationToken token)
    {
        // Capture and commit validation exclude every writer in this alias scope, including
        // insertion of an absent higher-precedence row, until the outer transaction ends.
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Placement capture requires a transaction.");
        var lockKey = $"haby-placement:{environment:N}:{alias}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT environment_record_id FROM placement_selections WHERE environment_record_id = {environment} AND provider_alias = {alias} FOR SHARE", token);
        var rows = await db.PlacementSelections.AsNoTracking().Where(x => x.EnvironmentRecordId == environment && x.ProviderAlias == alias).ToArrayAsync(token);
        var selected = selector is not null ? rows.SingleOrDefault(x => x.TargetKind == "named" && x.Selector == selector) :
            rows.SingleOrDefault(x => x.TargetKind == "component" && x.ApplicationId == application && x.ComponentRef == component) ??
            rows.SingleOrDefault(x => x.TargetKind == "application" && x.ApplicationId == application) ?? rows.SingleOrDefault(x => x.TargetKind == "environment");
        return selected?.RevisionId ?? throw new InvalidDataException("Placement selection unavailable.");
    }

    internal async Task<bool> ValidateInternalProvidersAsync(ManifestCatalogDbContext db, DurablePlanningInputs inputs, CancellationToken token)
    {
        try
        {
            var fresh = await CaptureInternalProvidersAsync(db, inputs.Providers.Declaration, inputs.Environment,
                inputs.Providers.Entries.Values.Where(x => x.Input is ProviderInput.Resource), token);
            return fresh.Equals(inputs.Providers);
        }
        catch (InvalidDataException) { return false; }
    }
}

/// <summary>Friend composition only. Uses the same Plan/Commit pipeline, never caller snapshots.</summary>
internal sealed class InternalAcceptedGenerationBuilder(PostgreSqlDurableDeploymentPlanner planner,
    PostgreSqlDurableDesiredStateCommitter committer, IReadOnlyList<IInternalActionHandler>? handlers = null)
{
    internal async ValueTask<DurableChangeSetCommit> AcceptForEffectAsync(EnvironmentRecordId environment,
        ApplicationRecordId application, ManifestRevisionRef revision, DeploymentGeneration expected, string actor, CancellationToken token)
        => await AcceptForEffectAsync(environment, application, revision, new DeploymentPrecondition.ExpectedGeneration(expected), actor, token);

    internal async ValueTask<DurableChangeSetCommit> AcceptForEffectAsync(EnvironmentRecordId environment,
        ApplicationRecordId application, ManifestRevisionRef revision, DeploymentPrecondition precondition, string actor, CancellationToken token)
    {
        var change = await planner.CreateChangeSetAsync(environment, application, revision, precondition, actor, token);
        if (change.ChangeSet is null) throw new InvalidDataException("Internal ChangeSet refused.");
        var plan = await planner.CreateInternalPlanAsync(change.ChangeSet.Id, actor, token, handlers);
        if (plan.Plan is null) throw new InvalidDataException("Internal Plan refused.");
        var commit = await committer.CommitInternalAsync(change.ChangeSet.Id, plan.Plan.Id, actor, token, handlers);
        return commit is DurableCommitResult.Committed accepted ? accepted.Commit : throw new InvalidDataException("Internal Commit refused.");
    }
}
