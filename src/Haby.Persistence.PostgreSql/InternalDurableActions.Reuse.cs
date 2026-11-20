using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed partial class InternalDurableActions
{
    private async ValueTask<ResourceReuseValidationResult> VerifyReuseAsync(DurableOperationKey key, CancellationToken token,
        bool operation = false, bool historical = false)
    {
        if (historical)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var deployment = Guid.Parse(key.DeploymentId.ToString());
            if (await db.ApplicationDeployments.AsNoTracking().AnyAsync(x => x.Id == deployment && x.AppliedGeneration >= key.Generation.Value, token))
            {
                var source = await db.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.DeploymentId == deployment && x.Generation == key.Generation.Value, token);
                _ = await InternalGenerationAuthority.RequireStageReadAsync(db, source, token);
                // Existing complete proof permits historical observation only. No active scope is minted,
                // so this result cannot authorize any new imported-Resource transition.
                return new(ResourceReuseValidationStatus.Confirmed);
            }
        }
        return await new PostgreSqlDurableOperationJournal(contexts, resourceOutputs, typedCodecs, reuseVerifiers)
            .ValidateInternalReuseAsync(key.DeploymentId, key.Generation, resourceAdmission, token, operation ? key.OperationId : null);
    }

    private static void RequireReuse(ResourceReuseValidationResult result)
    {
        if (result.Status != ResourceReuseValidationStatus.Confirmed)
            throw new InvalidDataException($"Imported Resource reuse refused: {result.Status}.");
        result.ActivateScope();
    }

    private static async Task RequireReuseTransitionAsync(ManifestCatalogDbContext db, Guid deployment, long generation, CancellationToken token)
    {
        if (!await PostgreSqlReuseVerificationScopes.BeforeAsync(db, deployment, generation, null,
            PostgreSqlReuseVerificationScopes.Transition.Aggregate, token))
            throw new InvalidDataException("Imported Resource reuse verification changed.");
    }
}
