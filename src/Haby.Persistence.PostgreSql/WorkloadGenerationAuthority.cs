using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

// Catalog-only inspection shared by submission and public observation. No effect preparation.
internal sealed record WorkloadGenerationInspection(DurableChangeSetCommit? Accepted, ProviderInput.Workload[] Subjects, string? Reason = null);
internal static class WorkloadGenerationAuthority
{
    internal static async ValueTask<WorkloadGenerationInspection> InspectAsync(
        IDbContextFactory<ManifestCatalogDbContext> contexts, InternalWorkloadConfiguration configuration,
        InternalDurableActions actions, string deploymentAction, string identityAction,
        ApplicationDeploymentId deployment, DeploymentGeneration generation, CancellationToken token, bool completedRead = false)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var id = Guid.Parse(deployment.ToString());
        var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == id && x.Generation == generation.Value, token);
        if (source is null) return new(null, [], "generation-not-found");
        var commit = await db.ChangeSetCommits.AsNoTracking().SingleOrDefaultAsync(x => x.ChangeSetId == source.ChangeSetId && x.DeploymentId == id && x.Generation == generation.Value, token);
        if (commit is null || !source.InternalEffects) return new(null, [], "unsupported-shape");
        var accepted = new DurableChangeSetCommit(DeploymentChangeSetId.Parse(commit.ChangeSetId.ToString()), DeploymentPlanId.Parse(commit.PlanId.ToString()), deployment, generation, commit.CommittedAt, commit.ActorId);
        if (source.SnapshotFormatVersion != DurableInputStorage.Version || source.GraphJson.Length > 2097152) return new(null, [], "unsupported-inventory");
        var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        var transition = graph.WorkloadUpdate is not null || graph.Correction is not null;
        var retirement = graph.Retirement?.Document;
        if (!transition && (retirement is null || retirement.ObsoleteObjects.Length != 0 || retirement.ObsoletePublications.Length != 0)) return new(null, [], "unsupported-inventory");
        source = completedRead ? await InternalGenerationAuthority.RequireStageReadAsync(db, accepted, token) : await InternalGenerationAuthority.RequireStageAsync(db, accepted, token);
        var selection = await configuration.SelectAsync(db, source, token);
        await actions.RequireSubmissionSupportAsync(db, source, selection, deploymentAction, identityAction, token);
        var subjects = selection.Keys.OrderBy(x => x.Component.Value, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal).ToArray();
        // All distinct item cardinalities are known before the first NEW write.
        if (subjects.Length is 0 or > 512 || selection.Values.Sum(x => (long)x.Count) > 512 || selection.Values.Any(x => x.Count > 64)) return new(null, [], "target-bound");
        if (!transition && graph.Baseline is { } baseline)
        {
            var previous = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == id && x.Generation == baseline.Generation, token);
            if (previous.InternalEffects || previous.Inputs().Configuration!.Manifest.Spec.Components.Values.Any(x => x.Workloads.Count > 0) || graph.ReusedResources.Any(x => x.Reintroduced))
                return new(null, [], "unsupported-shape");
        }
        return new(accepted, subjects);
    }
}
