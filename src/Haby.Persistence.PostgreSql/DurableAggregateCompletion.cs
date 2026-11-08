using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal static class DurableAggregateCompletion
{
    // Caller owns the transaction. This PostgreSQL row lock serializes aggregate decisions
    // with recovery and finalization. No network work occurs while it is held.
    internal static async Task<bool> TryAdvanceAsync(ManifestCatalogDbContext context, Guid deployment,
        long generation, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM application_deployments WHERE id = {deployment} FOR UPDATE", cancellationToken);
        var owner = await context.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(r => r.Id == deployment, cancellationToken);
        if (owner is null || owner.DesiredGeneration != generation) return false;
        if (await context.DurableOperations.AnyAsync(r => r.DeploymentId == deployment && r.Generation == generation &&
            r.State != nameof(DurableOperationState.Succeeded), cancellationToken)) return true;
        var source = await context.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment && x.Generation == generation, cancellationToken);
        // Mixed completion belongs to the runner's locked reuse + operation decision.
        // Preserve confirmed new effects even if a reused peer subsequently becomes unavailable.
        if (DurableOperationGraphCodec.Decode(source.GraphJson).ReusedResources.Count > 0) return true;
        if (owner.AppliedGeneration == generation) return true;
        return await context.ApplicationDeployments.Where(r => r.Id == deployment && r.DesiredGeneration == generation && r.AppliedGeneration == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.AppliedGeneration, generation)
                .SetProperty(r => r.ConcurrencyToken, r => r.ConcurrencyToken + 1), cancellationToken) == 1;
    }
}
