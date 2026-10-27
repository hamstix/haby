using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.Persistence.PostgreSql;

/// <summary>
/// The durable journal behind the ADR-025 dispatch barrier. Every transition is a conditional
/// single-row update, so a stale observer can never overwrite a newer durable state. Terminal
/// rows and retained attempts are never rewritten; reads never consult the Provider alias.
/// </summary>
public sealed class PostgreSqlDurableCreateOperationJournal(IDbContextFactory<ManifestCatalogDbContext> contexts)
    : IDurableCreateOperationJournal, IDurableCreateOperationQuery
{
    public async ValueTask<DurableCreateOperation?> FindAsync(DurableCreateOperationKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await ReadAsync(context, key, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<DurableCreateOperationAttempt>> FindAttemptsAsync(
        DurableCreateOperationKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var (deployment, generation, resourceId, action) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await context.DurableCreateOperationAttempts.AsNoTracking()
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.ResourceId == resourceId && row.Action == action)
            .OrderBy(row => row.AttemptNumber)
            .ToListAsync(cancellationToken);
        return rows.Select(ToAttempt).ToArray();
    }

    public async ValueTask<DurableCreateBeginAttemptResult> TryBeginAttemptAsync(DurableCreateOperationKey key,
        DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var (deployment, generation, resourceId, action) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var attemptId = Guid.CreateVersion7();
        var affected = await context.DurableCreateOperations
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.ResourceId == resourceId && row.Action == action &&
                          (row.State == nameof(DurableCreateOperationState.Pending) ||
                           row.State == nameof(DurableCreateOperationState.Preparing)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, nameof(DurableCreateOperationState.Preparing))
                .SetProperty(row => row.CurrentAttemptId, (Guid?)attemptId)
                .SetProperty(row => row.AttemptNumber, row => row.AttemptNumber + 1)
                .SetProperty(row => row.ReasonCode, (string?)null)
                .SetProperty(row => row.UpdatedAt, at), cancellationToken);
        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            var observed = await ReadAsync(context, key, cancellationToken);
            return observed is null
                ? new DurableCreateBeginAttemptResult.NotFound()
                : new DurableCreateBeginAttemptResult.Terminal(observed);
        }

        var operation = await ReadAsync(context, key, cancellationToken)
            ?? throw new InvalidOperationException("Create operation vanished during attempt start.");
        var priorOpen = await context.DurableCreateOperationAttempts
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.ResourceId == resourceId && row.Action == action && row.FinishedAt == null &&
                          row.AttemptId != attemptId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.FinishedAt, at)
                .SetProperty(row => row.Outcome, nameof(DurableCreateAttemptOutcome.AbandonedBeforeDispatch)),
            cancellationToken);
        if (priorOpen > 1) throw new InvalidOperationException("Create operation has more than one open attempt.");
        context.DurableCreateOperationAttempts.Add(new DurableCreateOperationAttemptRow
        {
            AttemptId = attemptId,
            DeploymentId = deployment,
            Generation = generation,
            ResourceId = resourceId,
            Action = action,
            AttemptNumber = operation.AttemptNumber,
            StartedAt = at
        });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return new DurableCreateBeginAttemptResult.Started(operation, attemptId, operation.AttemptNumber);
    }

    public async ValueTask<DurableCreateMarkerResult> TryCommitDispatchMarkerAsync(DurableCreateOperationKey key,
        Guid attemptId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var (deployment, generation, resourceId, action) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var affected = await context.DurableCreateOperations
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.ResourceId == resourceId && row.Action == action &&
                          row.State == nameof(DurableCreateOperationState.Preparing) &&
                          row.CurrentAttemptId == attemptId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, nameof(DurableCreateOperationState.Dispatching))
                .SetProperty(row => row.DispatchedAt, at)
                .SetProperty(row => row.UpdatedAt, at), cancellationToken);
        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            var observed = await ReadAsync(context, key, cancellationToken)
                ?? throw new InvalidOperationException("Create operation vanished during marker rejection.");
            return new DurableCreateMarkerResult.Rejected(observed);
        }

        var attemptUpdated = await context.DurableCreateOperationAttempts
            .Where(row => row.AttemptId == attemptId && row.FinishedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DispatchedAt, at), cancellationToken);
        if (attemptUpdated != 1)
            throw new InvalidOperationException("Create operation attempt is inconsistent.");
        await transaction.CommitAsync(CancellationToken.None);
        return new DurableCreateMarkerResult.Committed();
    }

    public async ValueTask<bool> TryCloseAttemptBeforeDispatchAsync(DurableCreateOperationKey key, Guid attemptId,
        DurableCreateAttemptOutcome outcome, DurableCreateOperationReason? reason, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if ((outcome == DurableCreateAttemptOutcome.CanceledBeforeDispatch && reason is not null) ||
            (outcome == DurableCreateAttemptOutcome.FailedBeforeDispatch &&
             reason is not (DurableCreateOperationReason.ProviderInputUnavailable or
                 DurableCreateOperationReason.SecretUnavailable or DurableCreateOperationReason.Connectivity)) ||
            outcome is not (DurableCreateAttemptOutcome.CanceledBeforeDispatch or
                DurableCreateAttemptOutcome.FailedBeforeDispatch))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        var (deployment, generation, resourceId, action) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var held = await context.DurableCreateOperations
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.ResourceId == resourceId && row.Action == action &&
                          row.State == nameof(DurableCreateOperationState.Preparing) &&
                          row.CurrentAttemptId == attemptId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ReasonCode, reason == null ? null : reason.ToString())
                .SetProperty(row => row.UpdatedAt, at), cancellationToken);
        if (held == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var closed = await context.DurableCreateOperationAttempts
            .Where(row => row.AttemptId == attemptId && row.FinishedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.FinishedAt, at)
                .SetProperty(row => row.Outcome, outcome.ToString()), cancellationToken);
        if (closed != 1) throw new InvalidOperationException("Create operation attempt is inconsistent.");
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    public async ValueTask<bool> TryFinalizeFailureAsync(DurableCreateOperationKey key, Guid attemptId,
        DurableCreateOperationState terminal, DurableCreateOperationReason reason, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (terminal is not (DurableCreateOperationState.Failed or DurableCreateOperationState.NeedsAttention))
            throw new ArgumentOutOfRangeException(nameof(terminal));
        var (deployment, generation, resourceId, action) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var affected = await context.DurableCreateOperations
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.ResourceId == resourceId && row.Action == action &&
                          row.CurrentAttemptId == attemptId &&
                          (row.State == nameof(DurableCreateOperationState.Preparing) ||
                           row.State == nameof(DurableCreateOperationState.Dispatching)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, terminal.ToString())
                .SetProperty(row => row.ReasonCode, reason.ToString())
                .SetProperty(row => row.FinalizedAt, at)
                .SetProperty(row => row.UpdatedAt, at), cancellationToken);
        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var outcome = MapFailureOutcome(terminal, reason);
        var closed = await context.DurableCreateOperationAttempts
            .Where(row => row.AttemptId == attemptId && row.FinishedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.FinishedAt, at)
                .SetProperty(row => row.Outcome, outcome.ToString()), cancellationToken);
        if (closed != 1) throw new InvalidOperationException("Create operation attempt is inconsistent.");
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    public async ValueTask<bool> TryRecoverDispatchingAsync(DurableCreateOperationKey key, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var (deployment, generation, resourceId, action) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var affected = await context.DurableCreateOperations
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.ResourceId == resourceId && row.Action == action &&
                          row.State == nameof(DurableCreateOperationState.Dispatching))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, nameof(DurableCreateOperationState.NeedsAttention))
                .SetProperty(row => row.ReasonCode, nameof(DurableCreateOperationReason.UncertainAfterDispatch))
                .SetProperty(row => row.FinalizedAt, at)
                .SetProperty(row => row.UpdatedAt, at), cancellationToken);
        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await context.DurableCreateOperationAttempts
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.ResourceId == resourceId && row.Action == action && row.FinishedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.FinishedAt, at)
                .SetProperty(row => row.Outcome, nameof(DurableCreateAttemptOutcome.Uncertain)),
            cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    public async ValueTask<bool> TryRecordSuccessAsync(DurableCreateOperationKey key, Guid attemptId,
        DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var (deployment, generation, resourceId, action) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var affected = await context.DurableCreateOperations
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.ResourceId == resourceId && row.Action == action &&
                          row.State == nameof(DurableCreateOperationState.Dispatching) &&
                          row.CurrentAttemptId == attemptId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, nameof(DurableCreateOperationState.Succeeded))
                .SetProperty(row => row.FinalizedAt, at)
                .SetProperty(row => row.UpdatedAt, at), cancellationToken);
        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        // The applied-generation advance is part of the same atomic commit. If this guard fails,
        // the whole transaction rolls back and the operation remains recoverably Dispatching.
        var advanced = await context.ApplicationDeployments
            .Where(row => row.Id == deployment && row.DesiredGeneration == generation &&
                          row.AppliedGeneration == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.AppliedGeneration, generation)
                .SetProperty(row => row.ConcurrencyToken, row => row.ConcurrencyToken + 1), cancellationToken);
        if (advanced == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var closed = await context.DurableCreateOperationAttempts
            .Where(row => row.AttemptId == attemptId && row.FinishedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.FinishedAt, at)
                .SetProperty(row => row.Outcome, nameof(DurableCreateAttemptOutcome.Succeeded)),
            cancellationToken);
        if (closed != 1) throw new InvalidOperationException("Create operation attempt is inconsistent.");
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    private static async ValueTask<DurableCreateOperation?> ReadAsync(ManifestCatalogDbContext context,
        DurableCreateOperationKey key, CancellationToken cancellationToken)
    {
        var (deployment, generation, resourceId, action) = Identity(key);
        var joined = await (from operation in context.DurableCreateOperations.AsNoTracking()
                            join gen in context.DeploymentGenerations.AsNoTracking()
                                on new { operation.DeploymentId, operation.Generation }
                                equals new { gen.DeploymentId, gen.Generation }
                            where operation.DeploymentId == deployment && operation.Generation == generation &&
                                  operation.ResourceId == resourceId && operation.Action == action
                            select new { operation, gen }).SingleOrDefaultAsync(cancellationToken);
        if (joined is null) return null;
        var row = joined.operation;
        if (joined.gen.Presence != nameof(DesiredPresence.Present) ||
            row.Action != nameof(DurableOperationAction.Create))
            throw new InvalidDataException("Stored create operation is inconsistent.");
        return new DurableCreateOperation(key, ParseState(row.State),
            EnvironmentRecordId.Parse(joined.gen.EnvironmentRecordId.ToString()),
            ApplicationRecordId.Parse(joined.gen.ApplicationRecordId.ToString()),
            ManifestRevisionRef.Create(ApplicationRecordId.Parse(joined.gen.ApplicationRecordId.ToString()),
                ManifestRevisionNumber.Create(joined.gen.RevisionNumber)),
            ProviderInstanceId.Parse(joined.gen.ProviderInstanceId.ToString()),
            ProviderConfigurationRevisionNumber.Create(joined.gen.ConfigurationRevisionNumber),
            joined.gen.ImplementationId, row.CreatedAt, row.UpdatedAt, ParseReason(row.ReasonCode),
            row.CurrentAttemptId, row.AttemptNumber, row.DispatchedAt, row.FinalizedAt);
    }

    private static (Guid Deployment, long Generation, string ResourceId, string Action) Identity(
        DurableCreateOperationKey key) =>
        (Guid.Parse(key.DeploymentId.ToString()), key.Generation.Value, key.ResourceId.Value,
            key.Action.ToString());

    private static DurableCreateOperationAttempt ToAttempt(DurableCreateOperationAttemptRow row) => new(
        row.AttemptId, row.AttemptNumber, row.StartedAt, row.DispatchedAt, row.FinishedAt,
        row.Outcome is null ? null : ParseOutcome(row.Outcome));

    private static DurableCreateOperationState ParseState(string value) => value switch
    {
        nameof(DurableCreateOperationState.Pending) => DurableCreateOperationState.Pending,
        nameof(DurableCreateOperationState.Preparing) => DurableCreateOperationState.Preparing,
        nameof(DurableCreateOperationState.Dispatching) => DurableCreateOperationState.Dispatching,
        nameof(DurableCreateOperationState.Succeeded) => DurableCreateOperationState.Succeeded,
        nameof(DurableCreateOperationState.Failed) => DurableCreateOperationState.Failed,
        nameof(DurableCreateOperationState.NeedsAttention) => DurableCreateOperationState.NeedsAttention,
        _ => throw new InvalidDataException("Stored create operation state is unsupported.")
    };

    private static DurableCreateOperationReason? ParseReason(string? value) => value switch
    {
        null => null,
        nameof(DurableCreateOperationReason.ProviderInputUnavailable) =>
            DurableCreateOperationReason.ProviderInputUnavailable,
        nameof(DurableCreateOperationReason.SecretUnavailable) => DurableCreateOperationReason.SecretUnavailable,
        nameof(DurableCreateOperationReason.UnsupportedShape) => DurableCreateOperationReason.UnsupportedShape,
        nameof(DurableCreateOperationReason.DatabaseNameInvalid) => DurableCreateOperationReason.DatabaseNameInvalid,
        nameof(DurableCreateOperationReason.Connectivity) => DurableCreateOperationReason.Connectivity,
        nameof(DurableCreateOperationReason.Conflict) => DurableCreateOperationReason.Conflict,
        nameof(DurableCreateOperationReason.ServerRejected) => DurableCreateOperationReason.ServerRejected,
        nameof(DurableCreateOperationReason.UncertainAfterDispatch) =>
            DurableCreateOperationReason.UncertainAfterDispatch,
        _ => throw new InvalidDataException("Stored create operation reason is unsupported.")
    };

    private static DurableCreateAttemptOutcome ParseOutcome(string value) => value switch
    {
        nameof(DurableCreateAttemptOutcome.Succeeded) => DurableCreateAttemptOutcome.Succeeded,
        nameof(DurableCreateAttemptOutcome.Conflict) => DurableCreateAttemptOutcome.Conflict,
        nameof(DurableCreateAttemptOutcome.ServerRejected) => DurableCreateAttemptOutcome.ServerRejected,
        nameof(DurableCreateAttemptOutcome.Uncertain) => DurableCreateAttemptOutcome.Uncertain,
        nameof(DurableCreateAttemptOutcome.CanceledBeforeDispatch) =>
            DurableCreateAttemptOutcome.CanceledBeforeDispatch,
        nameof(DurableCreateAttemptOutcome.AbandonedBeforeDispatch) =>
            DurableCreateAttemptOutcome.AbandonedBeforeDispatch,
        nameof(DurableCreateAttemptOutcome.FailedBeforeDispatch) =>
            DurableCreateAttemptOutcome.FailedBeforeDispatch,
        _ => throw new InvalidDataException("Stored create attempt outcome is unsupported.")
    };

    private static DurableCreateAttemptOutcome MapFailureOutcome(DurableCreateOperationState terminal,
        DurableCreateOperationReason reason) => (terminal, reason) switch
        {
            (DurableCreateOperationState.NeedsAttention, _) => DurableCreateAttemptOutcome.Uncertain,
            (DurableCreateOperationState.Failed, DurableCreateOperationReason.Conflict) =>
                DurableCreateAttemptOutcome.Conflict,
            (DurableCreateOperationState.Failed, DurableCreateOperationReason.ServerRejected) =>
                DurableCreateAttemptOutcome.ServerRejected,
            (DurableCreateOperationState.Failed, _) => DurableCreateAttemptOutcome.FailedBeforeDispatch,
            _ => throw new InvalidOperationException("Create operation terminal state is unsupported.")
        };
}

public static class DurableCreateOperationJournalServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlDurableCreateOperationJournal(this IServiceCollection services)
    {
        services.TryAddSingleton<PostgreSqlDurableCreateOperationJournal>();
        services.TryAddSingleton<IDurableCreateOperationJournal>(provider =>
            provider.GetRequiredService<PostgreSqlDurableCreateOperationJournal>());
        services.TryAddSingleton<IDurableCreateOperationQuery>(provider =>
            provider.GetRequiredService<PostgreSqlDurableCreateOperationJournal>());
        return services;
    }

    /// <summary>Retained for the desired-state committer path; the journal is the single read implementation.</summary>
    public static IServiceCollection AddPostgreSqlDurableCreateOperationQuery(this IServiceCollection services) =>
        services.AddPostgreSqlDurableCreateOperationJournal();
}
