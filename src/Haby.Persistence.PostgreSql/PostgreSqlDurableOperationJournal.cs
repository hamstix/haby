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
        var metadata = await DurableAggregateCompletion.InspectMetadataAsync(db, Guid.Parse(deployment.ToString()), generation.Value, outputContracts, codecs ?? [], cancellationToken, admission);
        if (metadata is null || !await DurableAggregateCompletion.CheckCredentialMetadataAsync(db, metadata, cancellationToken)) return false;
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

    public async ValueTask<bool> TryCompleteGenerationAsync(ApplicationDeploymentId deployment, DeploymentGeneration generation, DurableActionAdmission admission, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var id = Guid.Parse(deployment.ToString());
        try
        {
            var prepared = await DurableAggregateCompletion.PrepareAsync(db, id, generation.Value, outputContracts, codecs ?? [], cancellationToken, admission);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            if (!await DurableAggregateCompletion.TryAdvanceAsync(db, id, generation.Value, cancellationToken,
                outputContracts, admission, codecs, prepared)) return false;
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
        if (gen is null || gen.InternalEffects) return null;
        _ = gen.Inputs();
        var graph = DurableOperationGraphCodec.Decode(gen.GraphJson);
        var owner = await context.ApplicationDeployments.AsNoTracking().SingleAsync(r => r.Id == id, cancellationToken);
        var checkpoint = await DurableAggregateCompletion.ReadAsync(context, gen, cancellationToken);
        if (owner.AppliedGeneration >= generation.Value && checkpoint is null)
            throw new InvalidDataException("Applied generation has no Resource checkpoint.");
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
        {
            Reused = graph.ReusedResources.Count,
            Retained = graph.RetainedResources.Count,
            Reintroduced = graph.ReusedResources.Count(x => x.Reintroduced),
            ResourcesReady = checkpoint
        };
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
        if (await FindAsync(key, cancellationToken) is null) return new DurableBeginAttemptResult.NotFound();
        var (deployment, generation, resourceId) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var row = await DurableOperationTransitions.BeginAsync(context, key, at, cancellationToken);
        if (row is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            var observed = await ReadAsync(context, key, cancellationToken);
            return observed is null ? new DurableBeginAttemptResult.NotFound() : new DurableBeginAttemptResult.Terminal(observed);
        }
        var operation = await ReadAsync(context, key, cancellationToken) ?? throw new InvalidDataException("Operation disappeared.");
        await transaction.CommitAsync(CancellationToken.None);
        return new DurableBeginAttemptResult.Started(operation, row.CurrentAttemptId!.Value, row.AttemptNumber);
    }

    public async ValueTask<DurableMarkerResult> TryCommitDispatchMarkerAsync(DurableOperationKey key,
        Guid attemptId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var local = await FindAsync(key, cancellationToken);
        if (local is null) throw new InvalidDataException("Operation is not publicly admitted.");
        if (GeneratedCredentialContracts.IsProducer(local.Node))
            return new DurableMarkerResult.Rejected(local);
        var (deployment, generation, resourceId) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await DurableOperationTransitions.MarkerAsync(context, key, attemptId, at, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DurableMarkerResult.Rejected(await ReadAsync(context, key, cancellationToken) ?? throw new InvalidDataException("Operation disappeared."));
        }
        await transaction.CommitAsync(CancellationToken.None);
        return new DurableMarkerResult.Committed();
    }

    public async ValueTask<bool> TryCloseAttemptBeforeDispatchAsync(DurableOperationKey key, Guid attemptId,
        DurableAttemptOutcome outcome, DurableOperationReason? reason, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (await FindAsync(key, cancellationToken) is null) return false;
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
        if (await FindAsync(key, cancellationToken) is null) return false;
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
        if (await FindAsync(key, cancellationToken) is null) return false;
        var (deployment, generation, resourceId) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await DurableOperationTransitions.RecoverAsync(context, key, at, cancellationToken)) return false;
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    public async ValueTask<bool> TryRecordSuccessAsync(DurableOperationKey key, Guid attemptId,
        DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var local = await FindAsync(key, cancellationToken);
        if (local is not null && (local.Node.RequiresExternalOutput || GeneratedCredentialContracts.IsProducer(local.Node) ||
            outputContracts.Any(c => c.Producer == local.Node.Action)))
            return false;
        if (local is null || local.State != DurableOperationState.Dispatching || local.AttemptId != attemptId) return false;
        var (deployment, generation, resourceId) = Identity(key);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var prepared = await DurableAggregateCompletion.PrepareAsync(context, deployment, generation, outputContracts, codecs ?? [], cancellationToken,
            pending: new(key, attemptId, null));
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await DurableAggregateCompletion.LockAsync(context, deployment, cancellationToken);
        if (!await DurableOperationTransitions.ConfirmAsync(context, key, attemptId, at, false, cancellationToken)) return false;
        _ = await DurableAggregateCompletion.TryAdvanceConfirmedAsync(context, deployment, generation, cancellationToken, outputContracts, codecs: codecs, prepared: prepared);
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
        if (joined is null || joined.gen.InternalEffects) return null;
        var row = joined.operation;
        if (joined.gen.Presence != nameof(DesiredPresence.Present))
            throw new InvalidDataException("Stored create operation is inconsistent.");
        var inputs = joined.gen.Inputs();
        var node = DurableOperationGraphCodec.Decode(joined.gen.GraphJson).Nodes.Single(n => n.Id == key.OperationId);
        var provider = inputs.Providers.Resource(node.ResourceId);
        return new DurableOperation(key, ParseState(row.State),
            EnvironmentRecordId.Parse(joined.gen.EnvironmentRecordId.ToString()),
            ApplicationRecordId.Parse(joined.gen.ApplicationRecordId.ToString()),
            ManifestRevisionRef.Create(ApplicationRecordId.Parse(joined.gen.ApplicationRecordId.ToString()),
                ManifestRevisionNumber.Create(joined.gen.RevisionNumber)),
            provider.Instance, provider.Configuration,
            provider.Implementation.Value, row.CreatedAt, row.UpdatedAt, ParseReason(row.ReasonCode),
            row.CurrentAttemptId, row.AttemptNumber, row.DispatchedAt, row.FinalizedAt)
        { Node = node };
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
