using Haby.Application;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

/// <summary>Shared guarded state primitives. Caller owns transaction, validation and commit.
/// No Resource rehydration, aggregate completion, adapter dispatch or material resolution.</summary>
internal static class DurableOperationTransitions
{
    internal static IQueryable<DurableOperationRow> Operation(ManifestCatalogDbContext db, DurableOperationKey key)
    {
        var deployment = Guid.Parse(key.DeploymentId.ToString());
        return db.DurableOperations.Where(x => x.DeploymentId == deployment && x.Generation == key.Generation.Value && x.OperationId == key.OperationId.Value);
    }
    internal static async Task<DurableOperationRow?> BeginAsync(ManifestCatalogDbContext db, DurableOperationKey key, DateTimeOffset at, CancellationToken token)
    {
        var attempt = Guid.CreateVersion7();
        var changed = await Operation(db, key).Where(x => x.State == nameof(DurableOperationState.Pending) || x.State == nameof(DurableOperationState.Preparing))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, nameof(DurableOperationState.Preparing))
                .SetProperty(x => x.CurrentAttemptId, (Guid?)attempt).SetProperty(x => x.AttemptNumber, x => x.AttemptNumber + 1)
                .SetProperty(x => x.ReasonCode, (string?)null).SetProperty(x => x.UpdatedAt, at), token);
        if (changed != 1) return null;
        var row = await Operation(db, key).AsNoTracking().SingleAsync(token);
        var abandoned = await db.DurableOperationAttempts.Where(x => x.DeploymentId == row.DeploymentId && x.Generation == row.Generation &&
            x.OperationId == row.OperationId && x.FinishedAt == null && x.AttemptId != attempt)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.FinishedAt, at).SetProperty(x => x.Outcome, nameof(DurableAttemptOutcome.AbandonedBeforeDispatch)), token);
        if (abandoned > 1) throw new InvalidDataException("Multiple open dispatch attempts.");
        db.DurableOperationAttempts.Add(new()
        {
            DeploymentId = row.DeploymentId,
            Generation = row.Generation,
            OperationId = row.OperationId,
            AttemptId = attempt,
            AttemptNumber = row.AttemptNumber,
            StartedAt = at
        });
        await db.SaveChangesAsync(token);
        return row;
    }
    internal static async Task<bool> MarkerAsync(ManifestCatalogDbContext db, DurableOperationKey key, Guid attempt, DateTimeOffset at, CancellationToken token)
    {
        var changed = await Operation(db, key).Where(x => x.State == nameof(DurableOperationState.Preparing) && x.CurrentAttemptId == attempt)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, nameof(DurableOperationState.Dispatching))
                .SetProperty(x => x.DispatchedAt, at).SetProperty(x => x.UpdatedAt, at), token);
        if (changed != 1) return false;
        if (await db.DurableOperationAttempts.Where(x => x.AttemptId == attempt && x.FinishedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DispatchedAt, at), token) != 1) throw new InvalidDataException("Dispatch attempt mismatch.");
        return true;
    }
    internal static async Task<bool> RecoverAsync(ManifestCatalogDbContext db, DurableOperationKey key, DateTimeOffset at, CancellationToken token)
    {
        var row = await Operation(db, key).AsNoTracking().SingleOrDefaultAsync(token);
        if (row?.State != nameof(DurableOperationState.Dispatching)) return false;
        if (await Operation(db, key).Where(x => x.State == nameof(DurableOperationState.Dispatching) && x.CurrentAttemptId == row.CurrentAttemptId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, nameof(DurableOperationState.NeedsAttention))
                .SetProperty(x => x.ReasonCode, nameof(DurableOperationReason.UncertainAfterDispatch))
                .SetProperty(x => x.FinalizedAt, at).SetProperty(x => x.UpdatedAt, at), token) != 1) return false;
        if (await db.DurableOperationAttempts.Where(x => x.AttemptId == row.CurrentAttemptId && x.FinishedAt == null && x.DispatchedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Outcome, nameof(DurableAttemptOutcome.Uncertain)).SetProperty(x => x.FinishedAt, at), token) != 1)
            throw new InvalidDataException("Uncertain attempt mismatch.");
        return true;
    }
    internal static async Task<bool> ConfirmAsync(ManifestCatalogDbContext db, DurableOperationKey key, Guid attempt,
        DateTimeOffset at, bool observation, CancellationToken token)
    {
        var expected = observation ? nameof(DurableOperationState.NeedsAttention) : nameof(DurableOperationState.Dispatching);
        var outcome = observation ? nameof(DurableAttemptOutcome.Uncertain) : null;
        var row = await Operation(db, key).AsNoTracking().SingleOrDefaultAsync(token);
        if (row is null || row.CurrentAttemptId != attempt || row.DispatchedAt is null ||
            !await db.DurableOperationAttempts.AsNoTracking().AnyAsync(x => x.AttemptId == attempt && x.DeploymentId == row.DeploymentId &&
                x.Generation == row.Generation && x.OperationId == row.OperationId && x.AttemptNumber == row.AttemptNumber &&
                x.DispatchedAt == row.DispatchedAt && x.Outcome == outcome && (observation ? x.FinishedAt != null : x.FinishedAt == null), token)) return false;
        if (await Operation(db, key).Where(x => x.State == expected && x.CurrentAttemptId == attempt && (!observation || x.ReasonCode == nameof(DurableOperationReason.UncertainAfterDispatch)))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, nameof(DurableOperationState.Succeeded)).SetProperty(x => x.ReasonCode, (string?)null)
                .SetProperty(x => x.FinalizedAt, at).SetProperty(x => x.UpdatedAt, at), token) != 1) return false;
        if (!observation && await db.DurableOperationAttempts.Where(x => x.AttemptId == attempt && x.FinishedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.FinishedAt, at).SetProperty(x => x.Outcome, nameof(DurableAttemptOutcome.Succeeded)), token) != 1)
            throw new InvalidDataException("Response attempt mismatch.");
        return true;
    }
}
