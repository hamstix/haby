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
    public async ValueTask<PostgreSqlDatabaseRecoveryAudit?> FindDecisionAsync(DurableCreateOperationKey key,
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
        var key = command.Key;
        var deployment = Guid.Parse(key.DeploymentId.ToString());
        var environment = Guid.Parse(command.Environment.ToString());
        var generation = key.Generation.Value;
        var resource = key.ResourceId.Value;
        var action = key.Action.ToString();
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Lock the exact operation before deciding. The second concurrent caller sees the
            // committed decision after the first transaction releases this row lock.
            var operation = await context.DurableCreateOperations.FromSqlInterpolated($"""
                SELECT * FROM durable_create_operations
                WHERE deployment_id = {deployment} AND generation = {generation}
                  AND resource_id = {resource} AND action = {action} FOR UPDATE
                """).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            var existing = await DecisionQuery(context, key, command.ExpectedAttemptId)
                .SingleOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new(PostgreSqlDatabaseAssignmentOutcome.AlreadyAssigned,
                    ToAudit(key, existing).Receipt);
            }
            if (operation is null || operation.State != nameof(DurableCreateOperationState.NeedsAttention) ||
                operation.ReasonCode != nameof(DurableCreateOperationReason.UncertainAfterDispatch) ||
                operation.CurrentAttemptId != command.ExpectedAttemptId || operation.DispatchedAt is null ||
                operation.FinalizedAt is null)
                return Rejected();

            var attempt = await context.DurableCreateOperationAttempts.AsNoTracking()
                .SingleOrDefaultAsync(row => row.AttemptId == command.ExpectedAttemptId &&
                    row.DeploymentId == deployment && row.Generation == generation &&
                    row.ResourceId == resource && row.Action == action &&
                    row.AttemptNumber == operation.AttemptNumber && row.DispatchedAt != null &&
                    row.FinishedAt != null && row.Outcome == nameof(DurableCreateAttemptOutcome.Uncertain),
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
                ResourceId = resource,
                Action = action,
                AttemptId = command.ExpectedAttemptId,
                AttemptNumber = operation.AttemptNumber,
                AttemptOutcome = nameof(DurableCreateAttemptOutcome.Uncertain),
                Reason = command.Reason.ToString(),
                Note = command.Note,
                TechnicalActor = command.TechnicalActor,
                Acknowledged = command.Acknowledged,
                DecidedAt = at,
                OperationState = nameof(DurableCreateOperationState.Succeeded),
                AppliedGeneration = generation
            };
            context.PostgreSqlDatabaseRecoveryDecisions.Add(decision);
            await context.SaveChangesAsync(cancellationToken);

            var advanced = await context.ApplicationDeployments
                .Where(row => row.Id == deployment && row.EnvironmentRecordId == environment &&
                    row.DesiredGeneration == generation && row.AppliedGeneration == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.AppliedGeneration, generation)
                    .SetProperty(row => row.ConcurrencyToken, row => row.ConcurrencyToken + 1),
                    cancellationToken);
            if (advanced != 1) return Rejected();

            var updated = await context.DurableCreateOperations
                .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                    row.ResourceId == resource && row.Action == action &&
                    row.State == nameof(DurableCreateOperationState.NeedsAttention) &&
                    row.ReasonCode == nameof(DurableCreateOperationReason.UncertainAfterDispatch) &&
                    row.CurrentAttemptId == command.ExpectedAttemptId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.State, nameof(DurableCreateOperationState.Succeeded))
                    .SetProperty(row => row.ReasonCode, (string?)null)
                    .SetProperty(row => row.UpdatedAt, at), cancellationToken);
            if (updated != 1) return Rejected();

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
        ManifestCatalogDbContext context, DurableCreateOperationKey key, Guid attemptId)
    {
        var deployment = Guid.Parse(key.DeploymentId.ToString());
        return context.PostgreSqlDatabaseRecoveryDecisions.AsNoTracking().Where(row =>
            row.DeploymentId == deployment && row.Generation == key.Generation.Value &&
            row.ResourceId == key.ResourceId.Value && row.Action == key.Action.ToString() &&
            row.AttemptId == attemptId);
    }

    private static PostgreSqlDatabaseRecoveryAudit ToAudit(DurableCreateOperationKey key,
        PostgreSqlDatabaseRecoveryDecisionRow row) =>
        new(new PostgreSqlDatabaseRecoveryReceipt(row.Id, key, row.AttemptId,
                PostgreSqlRecoveryReason.OperatorAcceptedExistingDatabase, row.TechnicalActor, row.DecidedAt),
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
