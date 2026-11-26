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
    internal static async Task<DurableOperationRow?> BeginAsync(ManifestCatalogDbContext db, DurableOperationKey key, DateTimeOffset at, CancellationToken token, string? actor = null, ResourceReuseValidationResult? verification = null)
    {
        if (!await ImportAdmissionAuthority.BeforeExecutionAsync(db, Guid.Parse(key.DeploymentId.ToString()), key.Generation.Value, token)) return null;
        if (!await PostgreSqlReuseVerificationScopes.BeforeAsync(db, Guid.Parse(key.DeploymentId.ToString()), key.Generation.Value,
            key.OperationId, PostgreSqlReuseVerificationScopes.Transition.Begin, token, verification: verification)) return null;
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
            StartedAt = at,
            Actor = actor
        });
        await db.SaveChangesAsync(token);
        await PostgreSqlReuseVerificationScopes.UpdatedAsync(db, key, token, begun: true, verification: verification);
        return row;
    }
    internal static async Task<bool> MarkerAsync(ManifestCatalogDbContext db, DurableOperationKey key, Guid attempt, DateTimeOffset at, CancellationToken token, ResourceReuseValidationResult? verification = null)
    {
        if (!await ImportAdmissionAuthority.BeforeExecutionAsync(db, Guid.Parse(key.DeploymentId.ToString()), key.Generation.Value, token)) return false;
        if (!await PostgreSqlReuseVerificationScopes.BeforeAsync(db, Guid.Parse(key.DeploymentId.ToString()), key.Generation.Value,
            key.OperationId, PostgreSqlReuseVerificationScopes.Transition.Marker, token, attempt, verification)) return false;
        var changed = await Operation(db, key).Where(x => x.State == nameof(DurableOperationState.Preparing) && x.CurrentAttemptId == attempt)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, nameof(DurableOperationState.Dispatching))
                .SetProperty(x => x.DispatchedAt, at).SetProperty(x => x.UpdatedAt, at), token);
        if (changed != 1) return false;
        if (await db.DurableOperationAttempts.Where(x => x.AttemptId == attempt && x.FinishedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DispatchedAt, at), token) != 1) throw new InvalidDataException("Dispatch attempt mismatch.");
        await PostgreSqlReuseVerificationScopes.UpdatedAsync(db, key, token, verification: verification);
        return true;
    }
    internal static async Task<bool> RecoverAsync(ManifestCatalogDbContext db, DurableOperationKey key, DateTimeOffset at, CancellationToken token, ResourceReuseValidationResult? verification = null)
    {
        if (!await ImportAdmissionAuthority.BeforeExecutionAsync(db, Guid.Parse(key.DeploymentId.ToString()), key.Generation.Value, token)) return false;
        var row = await Operation(db, key).AsNoTracking().SingleOrDefaultAsync(token);
        if (row?.State != nameof(DurableOperationState.Dispatching)) return false;
        if (await Operation(db, key).Where(x => x.State == nameof(DurableOperationState.Dispatching) && x.CurrentAttemptId == row.CurrentAttemptId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, nameof(DurableOperationState.NeedsAttention))
                .SetProperty(x => x.ReasonCode, nameof(DurableOperationReason.UncertainAfterDispatch))
                .SetProperty(x => x.FinalizedAt, at).SetProperty(x => x.UpdatedAt, at), token) != 1) return false;
        if (await db.DurableOperationAttempts.Where(x => x.AttemptId == row.CurrentAttemptId && x.FinishedAt == null && x.DispatchedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Outcome, nameof(DurableAttemptOutcome.Uncertain)).SetProperty(x => x.FinishedAt, at), token) != 1)
            throw new InvalidDataException("Uncertain attempt mismatch.");
        await PostgreSqlReuseVerificationScopes.UpdatedAsync(db, key, token, verification: verification);
        return true;
    }
    internal static async Task<bool> ConfirmAsync(ManifestCatalogDbContext db, DurableOperationKey key, Guid attempt,
        DateTimeOffset at, bool observation, CancellationToken token, ResourceReuseValidationResult? verification = null)
    {
        if (!await PostgreSqlReuseVerificationScopes.BeforeAsync(db, Guid.Parse(key.DeploymentId.ToString()), key.Generation.Value,
            key.OperationId, PostgreSqlReuseVerificationScopes.Transition.Completion, token, attempt, verification)) return false;
        return await ConfirmCoreAsync(db, key, attempt, at, observation, token, verification);
    }

    // The complete immutable group is verified before any member changes. The aggregate
    // lock and owning transaction retain that proof through every atomic member transition.
    internal static async Task<bool> ConfirmObservationGroupAsync(ManifestCatalogDbContext db,
        IReadOnlyList<ObservationOperationConfirmation> members, DateTimeOffset at, CancellationToken token,
        ResourceReuseValidationResult verification)
    {
        if (members.Count == 0 || db.Database.CurrentTransaction is null) return false;
        var first = members[0].Key;
        if (members.Any(x => x.Key.DeploymentId != first.DeploymentId || x.Key.Generation != first.Generation)) return false;
        var deployment = Guid.Parse(first.DeploymentId.ToString());
        var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == first.Generation.Value, token);
        if (source is null) return false;
        var group = DurableOperationGraphCodec.Decode(source.GraphJson).ObservationGroups.SingleOrDefault(x => x.Operations.Contains(first.OperationId));
        if (group is null || group.Operations.Count != members.Count || !group.Operations.ToHashSet().SetEquals(members.Select(x => x.Key.OperationId)) ||
            !await PostgreSqlReuseVerificationScopes.BeforeAsync(db, deployment, first.Generation.Value, null,
                PostgreSqlReuseVerificationScopes.Transition.Aggregate, token, verification: verification)) return false;
        foreach (var member in members)
            if (!await ConfirmCoreAsync(db, member.Key, member.AttemptId, at, false, token)) return false;
        return true;
    }

    private static async Task<bool> ConfirmCoreAsync(ManifestCatalogDbContext db, DurableOperationKey key, Guid attempt,
        DateTimeOffset at, bool observation, CancellationToken token, ResourceReuseValidationResult? verification = null)
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
        await PostgreSqlReuseVerificationScopes.UpdatedAsync(db, key, token, verification: verification);
        return true;
    }
}
