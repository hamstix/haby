using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Haby.Persistence.PostgreSql;

/// <summary>
/// The durable journal behind the ADR-025 dispatch barrier. Every transition is a conditional
/// single-row update, so a stale observer can never overwrite a newer durable state. Terminal
/// rows are changed only by the separate audited recovery path; retained attempts are never
/// rewritten and reads never consult the Provider alias.
/// </summary>
public sealed class PostgreSqlDurableOperationJournal(IDbContextFactory<ManifestCatalogDbContext> contexts,
    IEnumerable<IExternalOperationOutputContract> outputContracts, IEnumerable<IProviderConfigurationCodec>? codecs = null)
    : IDurableOperationJournal, IDurableOperationQuery
{
    public async ValueTask<bool> ValidateReuseAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation, DurableActionAdmission admission, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        try
        {
            var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == Guid.Parse(deployment.ToString()) && x.Generation == generation.Value, cancellationToken);
            if (source is null) return false;
            _ = ResourceContinuity.Inputs(source);
            var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
            return await ResourceContinuity.ValidateAsync(db, source.DeploymentId, graph, cancellationToken) &&
                await ResourceContinuity.ValidateOutputsAsync(db, source.DeploymentId, graph, outputContracts, codecs ?? [], admission, ResourceContinuity.Inputs(source), cancellationToken, validateMaterial: true);
        }
        catch (InvalidDataException) { return false; }
    }

    public async ValueTask<bool> TryCompleteReuseAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation, DurableActionAdmission admission, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var id = Guid.Parse(deployment.ToString());
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM application_deployments WHERE id = {id} FOR UPDATE", cancellationToken);
        await ResourceContinuity.LockAsync(db, id, cancellationToken);
        var owner = await db.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == id && x.Generation == generation.Value, cancellationToken);
        if (owner is null || source is null || owner.DesiredGeneration < generation.Value) return false;
        try
        {
            _ = ResourceContinuity.Inputs(source);
            var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
            var operations = await db.DurableOperations.AsNoTracking().Where(x => x.DeploymentId == id && x.Generation == generation.Value).ToArrayAsync(cancellationToken);
            if (operations.Length != graph.Nodes.Count || graph.Nodes.Any(n => !operations.Any(o => o.OperationId == n.Id.Value && o.State == nameof(DurableOperationState.Succeeded)))) return false;
            if (!await ResourceContinuity.ValidateAsync(db, id, graph, cancellationToken) ||
                !await ResourceContinuity.ValidateOutputsAsync(db, id, graph, outputContracts, codecs ?? [], admission, ResourceContinuity.Inputs(source), cancellationToken, validateMaterial: true,
                    creationGeneration: graph.Nodes.Count > 0 ? generation.Value : null)) return false;
            foreach (var resource in graph.Nodes.Select(n => n.ResourceId.Value).Distinct())
            {
                var managed = await db.ResourceManagement.AsNoTracking().SingleOrDefaultAsync(x => x.Deployment == id && x.Resource == resource, cancellationToken);
                if (managed is null || managed.Environment != source.EnvironmentRecordId || managed.Application != source.ApplicationRecordId ||
                    managed.SourceGeneration != generation.Value || await ResourceContinuity.EvidenceAsync(db, managed, cancellationToken, requireApplied: false) is null) return false;
            }
            if (owner.AppliedGeneration >= generation.Value) return true;
            if (owner.DesiredGeneration != generation.Value || graph.Baseline?.Generation != owner.AppliedGeneration) return false;
            var advanced = await db.ApplicationDeployments.Where(x => x.Id == id && x.DesiredGeneration == generation.Value && x.AppliedGeneration == owner.AppliedGeneration)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AppliedGeneration, generation.Value).SetProperty(x => x.ConcurrencyToken, x => x.ConcurrencyToken + 1), cancellationToken);
            if (advanced != 1) return false;
            cancellationToken.ThrowIfCancellationRequested();
            await tx.CommitAsync(CancellationToken.None);
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    public async ValueTask<DurableGenerationProgress?> ProgressAsync(ApplicationDeploymentId deployment,
        DeploymentGeneration generation, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var id = Guid.Parse(deployment.ToString());
        var gen = await context.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(r => r.DeploymentId == id && r.Generation == generation.Value, cancellationToken);
        if (gen is null) return null;
        var graph = DurableOperationGraphCodec.Decode(gen.GraphJson);
        var owner = await context.ApplicationDeployments.AsNoTracking().SingleAsync(r => r.Id == id, cancellationToken);
        var operations = new List<DurableOperation>();
        foreach (var node in graph.Nodes)
            operations.Add(await ReadAsync(context, new(deployment, generation, node.Id), cancellationToken)
                ?? throw new InvalidDataException("Incomplete operation journal."));
        var succeeded = operations.Where(o => o.State == DurableOperationState.Succeeded).Select(o => o.Key.OperationId).ToHashSet();
        var attempts = await context.DurableOperationAttempts.AsNoTracking().Where(r => r.DeploymentId == id && r.Generation == generation.Value)
            .ToListAsync(cancellationToken);
        return new(deployment, generation, owner.DesiredGeneration, owner.AppliedGeneration,
            operations.Select(o => new DurableNodeProgress(o, Array.AsReadOnly(o.Node.Dependencies.Where(d => !succeeded.Contains(d)).ToArray()),
                attempts.SingleOrDefault(a => a.AttemptId == o.AttemptId)?.Outcome is { } outcome ? ParseOutcome(outcome) : null)).ToArray())
        { Reused = graph.ReusedResources.Count, Retained = graph.RetainedResources.Count, Reintroduced = graph.ReusedResources.Count(x => x.Reintroduced) };
    }

    public async ValueTask<DurableOperation?> FindAsync(DurableOperationKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await ReadAsync(context, key, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<DurableOperationAttempt>> FindAttemptsAsync(
        DurableOperationKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var (deployment, generation, resourceId) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await context.DurableOperationAttempts.AsNoTracking()
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.OperationId == resourceId)
            .OrderBy(row => row.AttemptNumber)
            .ToListAsync(cancellationToken);
        return rows.Select(ToAttempt).ToArray();
    }

    public async ValueTask<DurableBeginAttemptResult> TryBeginAttemptAsync(DurableOperationKey key,
        DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var (deployment, generation, resourceId) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var attemptId = Guid.CreateVersion7();
        var affected = await context.DurableOperations
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.OperationId == resourceId &&
                          (row.State == nameof(DurableOperationState.Pending) ||
                           row.State == nameof(DurableOperationState.Preparing)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, nameof(DurableOperationState.Preparing))
                .SetProperty(row => row.CurrentAttemptId, (Guid?)attemptId)
                .SetProperty(row => row.AttemptNumber, row => row.AttemptNumber + 1)
                .SetProperty(row => row.ReasonCode, (string?)null)
                .SetProperty(row => row.UpdatedAt, at), cancellationToken);
        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            var observed = await ReadAsync(context, key, cancellationToken);
            return observed is null
                ? new DurableBeginAttemptResult.NotFound()
                : new DurableBeginAttemptResult.Terminal(observed);
        }

        var operation = await ReadAsync(context, key, cancellationToken)
            ?? throw new InvalidOperationException("Create operation vanished during attempt start.");
        var priorOpen = await context.DurableOperationAttempts
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.OperationId == resourceId && row.FinishedAt == null &&
                          row.AttemptId != attemptId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.FinishedAt, at)
                .SetProperty(row => row.Outcome, nameof(DurableAttemptOutcome.AbandonedBeforeDispatch)),
            cancellationToken);
        if (priorOpen > 1) throw new InvalidOperationException("Create operation has more than one open attempt.");
        context.DurableOperationAttempts.Add(new DurableOperationAttemptRow
        {
            AttemptId = attemptId,
            DeploymentId = deployment,
            Generation = generation,
            OperationId = resourceId,
            AttemptNumber = operation.AttemptNumber,
            StartedAt = at
        });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return new DurableBeginAttemptResult.Started(operation, attemptId, operation.AttemptNumber);
    }

    public async ValueTask<DurableMarkerResult> TryCommitDispatchMarkerAsync(DurableOperationKey key,
        Guid attemptId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var local = await FindAsync(key, cancellationToken);
        if (local is not null && GeneratedCredentialContracts.IsProducer(local.Node))
            return new DurableMarkerResult.Rejected(local);
        var (deployment, generation, resourceId) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var affected = await context.DurableOperations
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.OperationId == resourceId &&
                          row.State == nameof(DurableOperationState.Preparing) &&
                          row.CurrentAttemptId == attemptId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, nameof(DurableOperationState.Dispatching))
                .SetProperty(row => row.DispatchedAt, at)
                .SetProperty(row => row.UpdatedAt, at), cancellationToken);
        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            var observed = await ReadAsync(context, key, cancellationToken)
                ?? throw new InvalidOperationException("Create operation vanished during marker rejection.");
            return new DurableMarkerResult.Rejected(observed);
        }

        var attemptUpdated = await context.DurableOperationAttempts
            .Where(row => row.AttemptId == attemptId && row.FinishedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DispatchedAt, at), cancellationToken);
        if (attemptUpdated != 1)
            throw new InvalidOperationException("Create operation attempt is inconsistent.");
        await transaction.CommitAsync(CancellationToken.None);
        return new DurableMarkerResult.Committed();
    }

    public async ValueTask<bool> TryCloseAttemptBeforeDispatchAsync(DurableOperationKey key, Guid attemptId,
        DurableAttemptOutcome outcome, DurableOperationReason? reason, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if ((outcome == DurableAttemptOutcome.CanceledBeforeDispatch && reason is not null) ||
            (outcome == DurableAttemptOutcome.FailedBeforeDispatch &&
             reason is not (DurableOperationReason.InputUnavailable or
                 DurableOperationReason.SecretUnavailable or DurableOperationReason.Connectivity)) ||
            outcome is not (DurableAttemptOutcome.CanceledBeforeDispatch or
                DurableAttemptOutcome.FailedBeforeDispatch))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        var (deployment, generation, resourceId) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var held = await context.DurableOperations
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.OperationId == resourceId &&
                          row.State == nameof(DurableOperationState.Preparing) &&
                          row.CurrentAttemptId == attemptId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ReasonCode, FormatReason(reason))
                .SetProperty(row => row.UpdatedAt, at), cancellationToken);
        if (held == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var closed = await context.DurableOperationAttempts
            .Where(row => row.AttemptId == attemptId && row.FinishedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.FinishedAt, at)
                .SetProperty(row => row.Outcome, outcome.ToString()), cancellationToken);
        if (closed != 1) throw new InvalidOperationException("Create operation attempt is inconsistent.");
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    public async ValueTask<bool> TryFinalizeFailureAsync(DurableOperationKey key, Guid attemptId,
        DurableOperationState terminal, DurableOperationReason reason, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (terminal is not (DurableOperationState.Failed or DurableOperationState.NeedsAttention))
            throw new ArgumentOutOfRangeException(nameof(terminal));
        var (deployment, generation, resourceId) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var affected = await context.DurableOperations
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.OperationId == resourceId &&
                          row.CurrentAttemptId == attemptId &&
                          (row.State == nameof(DurableOperationState.Preparing) ||
                           row.State == nameof(DurableOperationState.Dispatching)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, terminal.ToString())
                .SetProperty(row => row.ReasonCode, FormatReason(reason))
                .SetProperty(row => row.FinalizedAt, at)
                .SetProperty(row => row.UpdatedAt, at), cancellationToken);
        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var outcome = MapFailureOutcome(terminal, reason);
        var closed = await context.DurableOperationAttempts
            .Where(row => row.AttemptId == attemptId && row.FinishedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.FinishedAt, at)
                .SetProperty(row => row.Outcome, outcome.ToString()), cancellationToken);
        if (closed != 1) throw new InvalidOperationException("Create operation attempt is inconsistent.");
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    public async ValueTask<bool> TryRecoverDispatchingAsync(DurableOperationKey key, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var (deployment, generation, resourceId) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var affected = await context.DurableOperations
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.OperationId == resourceId &&
                          row.State == nameof(DurableOperationState.Dispatching))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, nameof(DurableOperationState.NeedsAttention))
                .SetProperty(row => row.ReasonCode, nameof(DurableOperationReason.UncertainAfterDispatch))
                .SetProperty(row => row.FinalizedAt, at)
                .SetProperty(row => row.UpdatedAt, at), cancellationToken);
        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await context.DurableOperationAttempts
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.OperationId == resourceId && row.FinishedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.FinishedAt, at)
                .SetProperty(row => row.Outcome, nameof(DurableAttemptOutcome.Uncertain)),
            cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    public async ValueTask<bool> TryRecordSuccessAsync(DurableOperationKey key, Guid attemptId,
        DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var local = await FindAsync(key, cancellationToken);
        if (local is not null && (GeneratedCredentialContracts.IsProducer(local.Node) ||
            outputContracts.Any(c => c.Producer == local.Node.Action)))
            return false;
        var (deployment, generation, resourceId) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var affected = await context.DurableOperations
            .Where(row => row.DeploymentId == deployment && row.Generation == generation &&
                          row.OperationId == resourceId &&
                          row.State == nameof(DurableOperationState.Dispatching) &&
                          row.CurrentAttemptId == attemptId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, nameof(DurableOperationState.Succeeded))
                .SetProperty(row => row.FinalizedAt, at)
                .SetProperty(row => row.UpdatedAt, at), cancellationToken);
        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (!await DurableAggregateCompletion.TryAdvanceAsync(context, deployment, generation, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var closed = await context.DurableOperationAttempts
            .Where(row => row.AttemptId == attemptId && row.FinishedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.FinishedAt, at)
                .SetProperty(row => row.Outcome, nameof(DurableAttemptOutcome.Succeeded)),
            cancellationToken);
        if (closed != 1) throw new InvalidOperationException("Create operation attempt is inconsistent.");
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    private static async ValueTask<DurableOperation?> ReadAsync(ManifestCatalogDbContext context,
        DurableOperationKey key, CancellationToken cancellationToken)
    {
        var (deployment, generation, resourceId) = Identity(key);
        var joined = await (from operation in context.DurableOperations.AsNoTracking()
                            join gen in context.DeploymentGenerations.AsNoTracking()
                                on new { operation.DeploymentId, operation.Generation }
                                equals new { gen.DeploymentId, gen.Generation }
                            where operation.DeploymentId == deployment && operation.Generation == generation &&
                                  operation.OperationId == resourceId
                            select new { operation, gen }).SingleOrDefaultAsync(cancellationToken);
        if (joined is null) return null;
        var row = joined.operation;
        if (joined.gen.Presence != nameof(DesiredPresence.Present))
            throw new InvalidDataException("Stored create operation is inconsistent.");
        return new DurableOperation(key, ParseState(row.State),
            EnvironmentRecordId.Parse(joined.gen.EnvironmentRecordId.ToString()),
            ApplicationRecordId.Parse(joined.gen.ApplicationRecordId.ToString()),
            ManifestRevisionRef.Create(ApplicationRecordId.Parse(joined.gen.ApplicationRecordId.ToString()),
                ManifestRevisionNumber.Create(joined.gen.RevisionNumber)),
            ProviderInstanceId.Parse(joined.gen.ProviderInstanceId.ToString()),
            ProviderConfigurationRevisionNumber.Create(joined.gen.ConfigurationRevisionNumber),
            joined.gen.ImplementationId, row.CreatedAt, row.UpdatedAt, ParseReason(row.ReasonCode),
            row.CurrentAttemptId, row.AttemptNumber, row.DispatchedAt, row.FinalizedAt)
        { Node = DurableOperationGraphCodec.Decode(joined.gen.GraphJson).Nodes.Single(n => n.Id == key.OperationId) };
    }

    private static (Guid Deployment, long Generation, string OperationId) Identity(
        DurableOperationKey key) =>
        (Guid.Parse(key.DeploymentId.ToString()), key.Generation.Value, key.OperationId.Value);

    private static DurableOperationAttempt ToAttempt(DurableOperationAttemptRow row) => new(
        row.AttemptId, row.AttemptNumber, row.StartedAt, row.DispatchedAt, row.FinishedAt,
        row.Outcome is null ? null : ParseOutcome(row.Outcome));

    private static DurableOperationState ParseState(string value) => value switch
    {
        nameof(DurableOperationState.Pending) => DurableOperationState.Pending,
        nameof(DurableOperationState.Preparing) => DurableOperationState.Preparing,
        nameof(DurableOperationState.Dispatching) => DurableOperationState.Dispatching,
        nameof(DurableOperationState.Succeeded) => DurableOperationState.Succeeded,
        nameof(DurableOperationState.Failed) => DurableOperationState.Failed,
        nameof(DurableOperationState.NeedsAttention) => DurableOperationState.NeedsAttention,
        _ => throw new InvalidDataException("Stored create operation state is unsupported.")
    };

    private static string? FormatReason(DurableOperationReason? reason) => reason switch
    {
        null => null,
        DurableOperationReason.SecretUnavailable => nameof(DurableOperationReason.SecretUnavailable),
        DurableOperationReason.Connectivity => nameof(DurableOperationReason.Connectivity),
        DurableOperationReason.Conflict => nameof(DurableOperationReason.Conflict),
        DurableOperationReason.ServerRejected => nameof(DurableOperationReason.ServerRejected),
        DurableOperationReason.UncertainAfterDispatch => nameof(DurableOperationReason.UncertainAfterDispatch),
        DurableOperationReason.InputInvalid => nameof(DurableOperationReason.InputInvalid),
        DurableOperationReason.InputUnavailable => nameof(DurableOperationReason.InputUnavailable),
        DurableOperationReason.HandlerUnavailable => nameof(DurableOperationReason.HandlerUnavailable),
        _ => throw new ArgumentOutOfRangeException(nameof(reason), "Operation reason is unsupported.")
    };

    private static DurableOperationReason? ParseReason(string? value) => value switch
    {
        null => null,
        nameof(DurableOperationReason.InputInvalid) => DurableOperationReason.InputInvalid,
        nameof(DurableOperationReason.InputUnavailable) => DurableOperationReason.InputUnavailable,
        nameof(DurableOperationReason.HandlerUnavailable) => DurableOperationReason.HandlerUnavailable,

        nameof(DurableOperationReason.SecretUnavailable) => DurableOperationReason.SecretUnavailable,
        nameof(DurableOperationReason.Connectivity) => DurableOperationReason.Connectivity,
        nameof(DurableOperationReason.Conflict) => DurableOperationReason.Conflict,
        nameof(DurableOperationReason.ServerRejected) => DurableOperationReason.ServerRejected,
        nameof(DurableOperationReason.UncertainAfterDispatch) =>
            DurableOperationReason.UncertainAfterDispatch,
        _ => throw new InvalidDataException("Stored create operation reason is unsupported.")
    };

    private static DurableAttemptOutcome ParseOutcome(string value) => value switch
    {
        nameof(DurableAttemptOutcome.Succeeded) => DurableAttemptOutcome.Succeeded,
        nameof(DurableAttemptOutcome.Conflict) => DurableAttemptOutcome.Conflict,
        nameof(DurableAttemptOutcome.ServerRejected) => DurableAttemptOutcome.ServerRejected,
        nameof(DurableAttemptOutcome.Uncertain) => DurableAttemptOutcome.Uncertain,
        nameof(DurableAttemptOutcome.CanceledBeforeDispatch) =>
            DurableAttemptOutcome.CanceledBeforeDispatch,
        nameof(DurableAttemptOutcome.AbandonedBeforeDispatch) =>
            DurableAttemptOutcome.AbandonedBeforeDispatch,
        nameof(DurableAttemptOutcome.FailedBeforeDispatch) =>
            DurableAttemptOutcome.FailedBeforeDispatch,
        _ => throw new InvalidDataException("Stored create attempt outcome is unsupported.")
    };

    private static DurableAttemptOutcome MapFailureOutcome(DurableOperationState terminal,
        DurableOperationReason reason) => (terminal, reason) switch
        {
            (DurableOperationState.NeedsAttention, _) => DurableAttemptOutcome.Uncertain,
            (DurableOperationState.Failed, DurableOperationReason.Conflict) =>
                DurableAttemptOutcome.Conflict,
            (DurableOperationState.Failed, DurableOperationReason.ServerRejected) =>
                DurableAttemptOutcome.ServerRejected,
            (DurableOperationState.Failed, _) => DurableAttemptOutcome.FailedBeforeDispatch,
            _ => throw new InvalidOperationException("Create operation terminal state is unsupported.")
        };
}

public static class DurableOperationJournalServiceCollectionExtensions
{
    public static IServiceCollection AddPostgreSqlDurableOperationJournal(this IServiceCollection services)
    {
        services.TryAddSingleton<PostgreSqlDurableOperationJournal>();
        services.TryAddSingleton<IExternalOperationOutputs, PostgreSqlExternalOperationOutputs>();
        services.TryAddSingleton<IConfigurationOutputEvidence, PostgreSqlExternalOperationOutputs>();
        services.TryAddSingleton<IDurableOperationJournal>(provider =>
            provider.GetRequiredService<PostgreSqlDurableOperationJournal>());
        services.TryAddSingleton<IDurableOperationQuery>(provider =>
            provider.GetRequiredService<PostgreSqlDurableOperationJournal>());
        return services;
    }

    /// <summary>Retained for the desired-state committer path; the journal is the single read implementation.</summary>
    public static IServiceCollection AddPostgreSqlDurableOperationQuery(this IServiceCollection services) =>
        services.AddPostgreSqlDurableOperationJournal();
}
