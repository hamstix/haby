using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>Atomic external evidence, exact attempt closure and aggregate completion.</summary>
public sealed class PostgreSqlExternalOperationOutputs(IDbContextFactory<ManifestCatalogDbContext> contexts,
    IEnumerable<IExternalOperationOutputContract> contracts, IEnumerable<IProviderConfigurationCodec>? codecs = null) : IExternalOperationOutputs, IConfigurationOutputEvidence
{
    public async ValueTask<AcceptedOperation?> FindAcceptedAsync(DurableOperationKey key, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await AcceptedAsync(context, key, cancellationToken);
    }

    internal static async Task<AcceptedOperation?> AcceptedAsync(ManifestCatalogDbContext context,
        DurableOperationKey key, CancellationToken token)
    {
        var accepted = await PostgreSqlGeneratedCredentialOperations.AcceptedAsync(context, key, token);
        if (accepted is null) return null;
        var generation = accepted.Value.Generation;
        var inputs = generation.Inputs();
        var graph = DurableOperationGraphCodec.Decode(generation.GraphJson);
        graph.ValidateReferences(inputs);
        var revision = await context.Revisions.AsNoTracking().SingleAsync(r =>
            r.ApplicationRecordId == generation.ApplicationRecordId && r.LocalRevisionNumber == generation.RevisionNumber, token);
        var snapshot = NormalizedSnapshotCodec.Decode(revision.Snapshot, revision.InputApiVersion,
            revision.NormalizedModelVersion, revision.ApplicationId, revision.SnapshotFormatVersion);
        return new(inputs, graph, accepted.Value.Node, snapshot.Spec);
    }

    public async ValueTask<bool> CompleteAsync(DurableOperationKey producer, Guid attempt, ExternalOperationOutput output,
        DateTimeOffset at, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            var accepted = await AcceptedAsync(context, producer, cancellationToken);
            if (accepted is null) return false;
            var deployment = Guid.Parse(producer.DeploymentId.ToString());
            var previous = await context.DurableOperations.AsNoTracking().SingleOrDefaultAsync(r => r.DeploymentId == deployment &&
                r.Generation == producer.Generation.Value && r.OperationId == producer.OperationId.Value, cancellationToken);
            if (previous?.State == nameof(DurableOperationState.Succeeded))
                return await ConfirmPublicationAsync(context, producer, attempt, output, cancellationToken);
            var pending = new DurableAggregateCompletion.PendingResult(producer, attempt, output);
            var metadata = await DurableAggregateCompletion.InspectPublicationAsync(context, pending, contracts, codecs ?? [], cancellationToken);
            if (metadata?.Candidate is not { } candidate || !await DurableAggregateCompletion.CheckCredentialMetadataAsync(context, metadata, cancellationToken)) return false;
            // Own confirmed publication is metadata authority. Whole readiness may be
            // unavailable independently; its absence must not erase this valid result.
            var prepared = await DurableAggregateCompletion.PrepareAsync(context, deployment, producer.Generation.Value,
                contracts, codecs ?? [], cancellationToken, pending: pending);
            // Adapter validation may resolve other immutable catalog inputs. Release each read's
            // connection before taking the write transaction, including with a one-connection pool.
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await DurableAggregateCompletion.LockAsync(context, deployment, cancellationToken);
            if (await context.DurableOperations.AnyAsync(r => r.DeploymentId == deployment && r.Generation == producer.Generation.Value &&
                r.OperationId == producer.OperationId.Value && r.State == nameof(DurableOperationState.Succeeded), cancellationToken))
                return await ConfirmPublicationAsync(context, producer, attempt, output, cancellationToken);
            var locked = await DurableAggregateCompletion.InspectPublicationAsync(context, pending, contracts, codecs ?? [], cancellationToken);
            if (locked is null || !DurableAggregateCompletion.SamePublication(metadata, locked) ||
                !await DurableAggregateCompletion.CheckCredentialMetadataAsync(context, locked, cancellationToken)) return false;
            var generation = producer.Generation.Value;
            var id = producer.OperationId.Value;
            var dependencies = accepted.Node.Dependencies.Select(d => d.Value).ToArray();
            if (await context.DurableOperations.CountAsync(r => r.DeploymentId == deployment && r.Generation == generation &&
                dependencies.Contains(r.OperationId) && r.State == nameof(DurableOperationState.Succeeded), cancellationToken) != dependencies.Length)
                return false;
            if (!await context.DurableOperations.AsNoTracking().AnyAsync(r => r.DeploymentId == deployment && r.Generation == generation &&
                r.OperationId == id && r.State == nameof(DurableOperationState.Dispatching) && r.CurrentAttemptId == attempt &&
                r.AttemptNumber == candidate.AttemptNumber && r.DispatchedAt == candidate.DispatchedAt, cancellationToken) ||
                !await DurableOperationTransitions.ConfirmAsync(context, producer, attempt, at, false, cancellationToken))
                return await ConfirmPublicationAsync(context, producer, attempt, output, cancellationToken);
            var existing = await OutputAsync(context, producer, output.Contract, output.Key, cancellationToken);
            if (existing is not null && existing.Payload != output.Payload) return false;
            if (existing is null) context.OperationOutputs.Add(new()
            {
                DeploymentId = deployment,
                Generation = generation,
                OperationId = id,
                Contract = output.Contract,
                Key = output.Key,
                Payload = output.Payload
            });
            await context.SaveChangesAsync(cancellationToken);
            _ = await DurableAggregateCompletion.TryAdvanceConfirmedAsync(context, deployment, generation, cancellationToken, contracts, codecs: codecs, prepared: prepared);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        }
        catch (Exception e) when (e is DbUpdateException or NpgsqlException or IOException or InvalidOperationException or OperationCanceledException)
        {
            // A fresh read may confirm this exact publication, never infer an external result.
            await using var confirm = await contexts.CreateDbContextAsync(CancellationToken.None);
            return await ConfirmPublicationAsync(confirm, producer, attempt, output, CancellationToken.None);
        }
    }

    public async ValueTask<ExternalOperationOutput?> ConsumeAsync(DurableOperationKey consumer, OperationOutputRef selector,
        CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        ExternalOperationOutput output;
        AcceptedOperation producerAccepted;
        IExternalOperationOutputContract contract;
        await using (var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken))
        {
            var accepted = await AcceptedAsync(context, consumer, cancellationToken);
            if (accepted is null || !accepted.Node.OutputReferences.Contains(selector) ||
                !accepted.Node.Dependencies.Contains(selector.ProducerOperationId)) return null;
            var producer = consumer with { OperationId = selector.ProducerOperationId };
            var row = await OutputAsync(context, producer, selector.OutputContract, selector.OutputKey, cancellationToken);
            if (row is null) return null;
            output = new ExternalOperationOutput(row.Contract, row.Key, row.Payload);
            var operation = await context.DurableOperations.AsNoTracking().SingleOrDefaultAsync(r =>
                r.DeploymentId == row.DeploymentId && r.Generation == row.Generation && r.OperationId == row.OperationId, cancellationToken);
            if (operation?.CurrentAttemptId is not { } attempt || !await ConfirmAsync(context, producer, attempt, output, cancellationToken, validateAccepted: false)) return null;
            producerAccepted = (await AcceptedAsync(context, producer, cancellationToken))!;
            contract = contracts.Single(c => c.Producer == producerAccepted.Node.Action);
        }
        return await contract.ValidateAcceptedAsync(consumer with { OperationId = selector.ProducerOperationId }, producerAccepted, output, cancellationToken) ? output : null;
    }

    public async ValueTask<ExternalOperationOutput?> ReadAsync(DurableOperationKey producer, ManagedResourceKey owner,
        DurableActionExecutionKey action, string contract, string key, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        try
        {
            var resolved = await ResourceContinuity.ResolveAsync(context, producer, owner.ResourceLocalId, cancellationToken);
            if (resolved is null) return null;
            producer = resolved;
            var accepted = await AcceptedAsync(context, producer, cancellationToken);
            if (accepted is null || accepted.Inputs.Environment != owner.EnvironmentRecordId ||
                accepted.Inputs.Revision.ApplicationRecordId != owner.ApplicationRecordId || accepted.Node.ResourceId != owner.ResourceLocalId ||
                accepted.Node.Action != action || !accepted.Spec.Resources.TryGetValue(owner.ResourceLocalId, out var resource) ||
                resource.Type != action.Admission.ResourceContract) return null;
            var row = await OutputAsync(context, producer, contract, key, cancellationToken);
            if (row is null) return null;
            var output = new ExternalOperationOutput(row.Contract, row.Key, row.Payload);
            var operation = await context.DurableOperations.AsNoTracking().SingleOrDefaultAsync(r =>
                r.DeploymentId == row.DeploymentId && r.Generation == row.Generation && r.OperationId == row.OperationId, cancellationToken);
            return operation?.CurrentAttemptId is { } attempt &&
                await ConfirmAsync(context, producer, attempt, output, cancellationToken, validateAccepted: false) ? output : null;
        }
        catch (InvalidDataException) { return null; }
    }

    private static async Task<bool> ConfirmPublicationAsync(ManifestCatalogDbContext context, DurableOperationKey key, Guid attempt,
        ExternalOperationOutput output, CancellationToken token)
    {
        var accepted = await AcceptedAsync(context, key, token);
        if (accepted is null || GeneratedCredentialContracts.IsProducer(accepted.Node) || !accepted.Node.RequiresExternalOutput ||
            accepted.Node.RequiredOutput != new ExternalOperationOutputIdentity(output.Contract, output.Key)) return false;
        var row = await OutputAsync(context, key, output.Contract, output.Key, token);
        if (row is null || row.Payload != output.Payload) return false;
        return await context.DurableOperations.AnyAsync(r => r.DeploymentId == row.DeploymentId && r.Generation == row.Generation &&
            r.OperationId == row.OperationId && r.State == nameof(DurableOperationState.Succeeded) && r.CurrentAttemptId == attempt && r.ReasonCode == null &&
            context.DurableOperationAttempts.Any(a => a.AttemptId == attempt && a.DeploymentId == r.DeploymentId && a.Generation == r.Generation &&
                a.OperationId == r.OperationId && a.AttemptNumber == r.AttemptNumber && a.Outcome == nameof(DurableAttemptOutcome.Succeeded) &&
                a.DispatchedAt != null && a.DispatchedAt == r.DispatchedAt && a.FinishedAt != null && a.FinishedAt == r.FinalizedAt), token);
    }

    private async Task<bool> ConfirmAsync(ManifestCatalogDbContext context, DurableOperationKey key, Guid attempt,
        ExternalOperationOutput output, CancellationToken token, bool validateAccepted = true)
    {
        var accepted = await AcceptedAsync(context, key, token);
        var contract = accepted is null ? null : contracts.SingleOrDefault(c => c.Producer == accepted.Node.Action);
        if (accepted is null || contract is null || accepted.Node.RequiredOutput != contract.Identity ||
            contract.Identity != new ExternalOperationOutputIdentity(output.Contract, output.Key) || !contract.Validate(accepted.Node, output) ||
            (validateAccepted && !await contract.ValidateAcceptedAsync(key, accepted, output, token))) return false;
        var row = await OutputAsync(context, key, output.Contract, output.Key, token);
        if (row is null || row.Payload != output.Payload) return false;
        return await context.DurableOperations.AnyAsync(r => r.DeploymentId == row.DeploymentId && r.Generation == row.Generation &&
            r.OperationId == row.OperationId && r.State == nameof(DurableOperationState.Succeeded) && r.CurrentAttemptId == attempt &&
            context.DurableOperationAttempts.Any(a => a.AttemptId == attempt && a.DeploymentId == r.DeploymentId &&
                a.Generation == r.Generation && a.OperationId == r.OperationId && a.AttemptNumber == r.AttemptNumber &&
                a.Outcome == nameof(DurableAttemptOutcome.Succeeded) && a.FinishedAt != null && a.DispatchedAt != null), token);
    }

    private static Task<OperationOutputRow?> OutputAsync(ManifestCatalogDbContext context, DurableOperationKey key,
        string contract, string outputKey, CancellationToken token)
    {
        var deployment = Guid.Parse(key.DeploymentId.ToString());
        return context.OperationOutputs.AsNoTracking().SingleOrDefaultAsync(r => r.DeploymentId == deployment &&
            r.Generation == key.Generation.Value && r.OperationId == key.OperationId.Value && r.Contract == contract && r.Key == outputKey, token);
    }
}
