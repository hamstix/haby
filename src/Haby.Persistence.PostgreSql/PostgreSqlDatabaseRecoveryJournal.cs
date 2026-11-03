using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.Persistence.PostgreSql;

/// <summary>Protected audit query and atomic, compare-and-transition recovery commit.</summary>
public sealed class PostgreSqlDatabaseRecoveryJournal(IDbContextFactory<ManifestCatalogDbContext> contexts)
    : IPostgreSqlDatabaseRecoveryJournal
{
    public async ValueTask<PostgreSqlDatabaseRecoveryAudit?> FindDecisionAsync(DurableOperationKey key,
        Guid attemptId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await DecisionQuery(context, key, attemptId).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToAudit(key, row);
    }

    public async ValueTask<PostgreSqlDatabaseAssignmentResult> TryAssignAsync(
        AssignExistingPostgreSqlDatabase command, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        // Return exactly the timestamp PostgreSQL will retain, including in the first receipt.
        // Its timestamptz precision is microseconds; a lost-response retry reads that stored value.
        at = at.ToUniversalTime();
        at = at.AddTicks(-(at.Ticks % TimeSpan.TicksPerMicrosecond));
        var key = command.Key;
        var deployment = Guid.Parse(key.DeploymentId.ToString());
        var environment = Guid.Parse(command.Environment.ToString());
        var generation = key.Generation.Value;
        var resource = key.OperationId.Value;
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Lock the exact operation before deciding. The second concurrent caller sees the
            // committed decision after the first transaction releases this row lock.
            var operation = await context.DurableOperations.FromSqlInterpolated($"""
                SELECT * FROM durable_create_operations
                WHERE deployment_id = {deployment} AND generation = {generation}
                  AND operation_id = {resource} FOR UPDATE
                """).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            // Revalidate the standalone boundary inside the decision transaction, including
            // for direct trusted callers. Never turn one node of a larger graph into a
            // no-output success. Concrete PostgreSQL eligibility remains adapter-owned.
            var accepted = await context.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(row =>
                row.DeploymentId == deployment && row.Generation == generation, cancellationToken);
            if (accepted is null || DurableOperationGraphCodec.Decode(accepted.GraphJson).Nodes is not [var only] ||
                only.Id != key.OperationId || only.Dependencies.Count != 0 || only.OutputReferences.Count != 0 ||
                await context.DurableOperations.CountAsync(row => row.DeploymentId == deployment &&
                    row.Generation == generation, cancellationToken) != 1)
                return Rejected();
            var existing = await DecisionQuery(context, key, command.ExpectedAttemptId)
                .SingleOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new(PostgreSqlDatabaseAssignmentOutcome.AlreadyAssigned,
                    ToAudit(key, existing).Receipt);
            }
            if (operation is null || operation.State != nameof(DurableOperationState.NeedsAttention) ||
                operation.ReasonCode != nameof(DurableOperationReason.UncertainAfterDispatch) ||
                operation.CurrentAttemptId != command.ExpectedAttemptId || operation.DispatchedAt is null ||
                operation.FinalizedAt is null)
                return Rejected();

            var attempt = await context.DurableOperationAttempts.AsNoTracking()
                .SingleOrDefaultAsync(row => row.AttemptId == command.ExpectedAttemptId &&
                    row.DeploymentId == deployment && row.Generation == generation &&
                    row.OperationId == resource &&
                    row.AttemptNumber == operation.AttemptNumber && row.DispatchedAt != null &&
                    row.FinishedAt != null && row.Outcome == nameof(DurableAttemptOutcome.Uncertain),
                    cancellationToken);
            var stillCurrent = await context.ApplicationDeployments.AsNoTracking().AnyAsync(row =>
                row.Id == deployment && row.EnvironmentRecordId == environment &&
                row.DesiredGeneration == generation && row.AppliedGeneration == null,
                cancellationToken);
            if (attempt is null || !stillCurrent) return Rejected();

            var decision = new PostgreSqlDatabaseRecoveryDecisionRow
            {
                Id = Guid.CreateVersion7(),
                DeploymentId = deployment,
                Generation = generation,
                OperationId = resource,
                AttemptId = command.ExpectedAttemptId,
                AttemptNumber = operation.AttemptNumber,
                AttemptOutcome = nameof(DurableAttemptOutcome.Uncertain),
                Reason = command.Reason.ToString(),
                Note = command.Note,
                TechnicalActor = command.TechnicalActor,
                Acknowledged = command.Acknowledged,
                DecidedAt = at,
                OperationState = nameof(DurableOperationState.Succeeded),
                AppliedGeneration = null
            };

            var updated = await context.DurableOperations
                .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                    row.OperationId == resource &&
                    row.State == nameof(DurableOperationState.NeedsAttention) &&
                    row.ReasonCode == nameof(DurableOperationReason.UncertainAfterDispatch) &&
                    row.CurrentAttemptId == command.ExpectedAttemptId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.State, nameof(DurableOperationState.Succeeded))
                    .SetProperty(row => row.ReasonCode, (string?)null)
                    .SetProperty(row => row.UpdatedAt, at), cancellationToken);
            if (updated != 1) return Rejected();

            if (!await DurableAggregateCompletion.TryAdvanceAsync(context, deployment, generation, cancellationToken)) return Rejected();
            decision.AppliedGeneration = await context.ApplicationDeployments.AsNoTracking().Where(r => r.Id == deployment)
                .Select(r => r.AppliedGeneration).SingleAsync(cancellationToken);
            context.PostgreSqlDatabaseRecoveryDecisions.Add(decision);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            return new(PostgreSqlDatabaseAssignmentOutcome.Assigned, ToAudit(key, decision).Receipt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Never propagate database parameter values (including the protected operator note).
            throw new InvalidOperationException("Recovery decision could not be persisted.");
        }
    }

    private static IQueryable<PostgreSqlDatabaseRecoveryDecisionRow> DecisionQuery(
        ManifestCatalogDbContext context, DurableOperationKey key, Guid attemptId)
    {
        var deployment = Guid.Parse(key.DeploymentId.ToString());
        return context.PostgreSqlDatabaseRecoveryDecisions.AsNoTracking().Where(row =>
            row.DeploymentId == deployment && row.Generation == key.Generation.Value &&
            row.OperationId == key.OperationId.Value &&
            row.AttemptId == attemptId);
    }

    private static PostgreSqlDatabaseRecoveryAudit ToAudit(DurableOperationKey key,
        PostgreSqlDatabaseRecoveryDecisionRow row) =>
        new(new PostgreSqlDatabaseRecoveryReceipt(row.Id, key, row.AttemptId,
                PostgreSqlRecoveryReason.OperatorAcceptedExistingDatabase, row.TechnicalActor, row.DecidedAt, row.AppliedGeneration),
            row.Acknowledged, row.Note);

    private static PostgreSqlDatabaseAssignmentResult Rejected() =>
        new(PostgreSqlDatabaseAssignmentOutcome.Rejected, null);
}

public static class PostgreSqlDatabaseRecoveryJournalServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlDatabaseRecoveryJournal(this IServiceCollection services)
    {
        services.TryAddSingleton<IPostgreSqlDatabaseRecoveryJournal, PostgreSqlDatabaseRecoveryJournal>();
        return services;
    }
}
