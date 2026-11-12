using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>Internal catalog-only producer completion and exact, owner-scoped consumption.</summary>
public sealed class PostgreSqlGeneratedCredentialOperations(IDbContextFactory<ManifestCatalogDbContext> contexts,
    ISecretStore secrets) : IGeneratedCredentialOperations
{
    public async ValueTask<GeneratedCredentialInput?> FindInputAsync(DurableOperationKey producer, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            var accepted = await AcceptedAsync(context, producer, cancellationToken);
            return accepted is not null && GeneratedCredentialContracts.IsProducer(accepted.Value.Node)
                ? GeneratedCredentialInputCodec.Decode(accepted.Value.Node.Input) : null;
        }
        catch (Exception e) when (StorageFailure(e)) { return null; }
    }

    public async ValueTask<bool> CompleteAsync(DurableOperationKey producer, Guid attempt, GeneratedCredentialOutput output,
        DateTimeOffset at, CancellationToken cancellationToken)
    {
        var committing = false;
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            var accepted = await AcceptedAsync(context, producer, cancellationToken);
            if (accepted is null || !GeneratedCredentialContracts.IsProducer(accepted.Value.Node)) return false;
            var input = GeneratedCredentialInputCodec.Decode(accepted.Value.Node.Input)!;
            var dependencyIds = accepted.Value.Node.Dependencies.Select(d => d.Value).ToArray();
            if (!await DependenciesReadyAsync(context, producer, dependencyIds, cancellationToken)) return false;
            // Resolve material before the aggregate lock. Inside the transaction recheck
            // exact slot metadata on this connection; never resolve secrets under that lock.
            if (!await ValidMaterialAsync(context, input, output.Reference, cancellationToken)) return false;
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await DurableAggregateCompletion.LockAsync(context, Guid.Parse(producer.DeploymentId.ToString()), cancellationToken);
            var slot = await PostgreSqlGeneratedCredentialSlots.ReadAsync(context, new(input.Owner, input.SlotId), input.GenerationProfile, cancellationToken);
            if (slot?.Outcome != GeneratedCredentialOutcome.Existing || slot.Reference != output.Reference) return false;
            if (!await DependenciesReadyAsync(context, producer, dependencyIds, cancellationToken)) return false;
            var deployment = Guid.Parse(producer.DeploymentId.ToString());
            var generation = producer.Generation.Value;
            var id = producer.OperationId.Value;
            // Conditional UPDATE takes the operation row lock and arbitrates independent executors.
            // No Dispatching state is used for local catalog work.
            var held = await context.DurableOperations.Where(r => r.DeploymentId == deployment && r.Generation == generation &&
                r.OperationId == id && r.State == nameof(DurableOperationState.Preparing) && r.CurrentAttemptId == attempt)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, nameof(DurableOperationState.Succeeded))
                    .SetProperty(r => r.FinalizedAt, at).SetProperty(r => r.UpdatedAt, at)
                    .SetProperty(r => r.ReasonCode, (string?)null), cancellationToken);
            if (held != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
                return await ConfirmAsync(context, producer, output, cancellationToken);
            }
            var existing = await OutputAsync(context, producer, cancellationToken);
            if (existing is not null && GeneratedCredentialOutputCodec.Decode(existing.Contract, existing.Payload) != output)
                return false;
            if (existing is null)
                context.OperationOutputs.Add(new()
                {
                    DeploymentId = deployment,
                    Generation = generation,
                    OperationId = id,
                    Contract = GeneratedCredentialContracts.OutputContract,
                    Key = GeneratedCredentialContracts.Password,
                    Payload = GeneratedCredentialOutputCodec.Encode(output)
                });
            await context.SaveChangesAsync(cancellationToken);
            var closed = await context.DurableOperationAttempts.Where(r => r.AttemptId == attempt &&
                r.DeploymentId == deployment && r.Generation == generation && r.OperationId == id &&
                r.DispatchedAt == null && r.FinishedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.FinishedAt, at)
                    .SetProperty(r => r.Outcome, nameof(DurableAttemptOutcome.Succeeded)), cancellationToken);
            if (closed != 1) return false;
            _ = await DurableAggregateCompletion.TryAdvanceConfirmedAsync(context, deployment, generation, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            committing = true;
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        }
        catch (Exception e) when (StorageFailure(e) || (committing && e is OperationCanceledException))
        {
            if (!committing) cancellationToken.ThrowIfCancellationRequested();
            // Includes successful ambiguous commits and concurrency losers, with a fresh context
            // after disposal/rollback. Never regenerate or republish in this invocation.
            try
            {
                await using var reread = await contexts.CreateDbContextAsync(CancellationToken.None);
                return await ConfirmAsync(reread, producer, output, CancellationToken.None);
            }
            catch (Exception reread) when (StorageFailure(reread)) { return false; }
        }
    }

    public async ValueTask<GeneratedCredentialOutput?> ConsumeAsync(DurableOperationKey consumer, OperationOutputRef selector,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            var accepted = await AcceptedAsync(context, consumer, cancellationToken);
            if (accepted is null || !accepted.Value.Node.OutputReferences.Contains(selector)) return null;
            var producer = consumer with { OperationId = selector.ProducerOperationId };
            var input = await InputAsync(context, producer, cancellationToken);
            if (input is null || selector.OutputContract != GeneratedCredentialContracts.OutputContract ||
                selector.OutputKey != GeneratedCredentialContracts.Password ||
                input.Owner != new ManagedResourceKey(EnvironmentRecordId.Parse(accepted.Value.Generation.EnvironmentRecordId.ToString()),
                    ApplicationRecordId.Parse(accepted.Value.Generation.ApplicationRecordId.ToString()), accepted.Value.Node.ResourceId)) return null;
            var row = await OutputAsync(context, producer, cancellationToken);
            var output = row is null ? null : GeneratedCredentialOutputCodec.Decode(row.Contract, row.Payload);
            return output is not null && await ConfirmAsync(context, producer, output, cancellationToken) ? output : null;
        }
        catch (Exception e) when (StorageFailure(e)) { return null; }
    }

    private static async Task<bool> DependenciesReadyAsync(ManifestCatalogDbContext context, DurableOperationKey producer,
        string[] dependencyIds, CancellationToken token)
    {
        var deployment = Guid.Parse(producer.DeploymentId.ToString());
        return await context.DurableOperations.CountAsync(r => r.DeploymentId == deployment &&
            r.Generation == producer.Generation.Value && dependencyIds.Contains(r.OperationId) &&
            r.State == nameof(DurableOperationState.Succeeded), token) == dependencyIds.Length;
    }

    private async Task<bool> ConfirmAsync(ManifestCatalogDbContext context, DurableOperationKey producer,
        GeneratedCredentialOutput expected, CancellationToken token)
    {
        var input = await InputAsync(context, producer, token);
        if (input is null) return false;
        var row = await OutputAsync(context, producer, token);
        if (row is null || GeneratedCredentialOutputCodec.Decode(row.Contract, row.Payload) != expected) return false;
        var deployment = Guid.Parse(producer.DeploymentId.ToString());
        if (!await context.DurableOperations.AnyAsync(r => r.DeploymentId == deployment && r.Generation == producer.Generation.Value &&
            r.OperationId == producer.OperationId.Value && r.State == nameof(DurableOperationState.Succeeded) &&
            context.DurableOperationAttempts.Any(a => a.AttemptId == r.CurrentAttemptId && a.DeploymentId == r.DeploymentId &&
                a.Generation == r.Generation && a.OperationId == r.OperationId && a.Outcome == nameof(DurableAttemptOutcome.Succeeded) &&
                a.FinishedAt != null && a.DispatchedAt == null), token)) return false;
        return await ValidMaterialAsync(context, input, expected.Reference, token);
    }

    private async Task<bool> ValidMaterialAsync(ManifestCatalogDbContext context, GeneratedCredentialInput input,
        SecretRef reference, CancellationToken token)
    {
        var slot = await PostgreSqlGeneratedCredentialSlots.ReadAsync(context, new(input.Owner, input.SlotId), input.GenerationProfile, token);
        if (slot?.Outcome != GeneratedCredentialOutcome.Existing || slot.Reference != reference) return false;
        var definition = new SecretDefinition(new SecretOwner.Resource(input.Owner), SecretPurpose.ApplicationLogin);
        // Reuse the selected catalog backend's transaction/connection, including one-connection pools.
        // Other SecretStore implementations retain their own owner-scoped exact resolution boundary.
        using var material = secrets is PostgreSqlPlaintextSecretStore plaintext
            ? await plaintext.ResolveAsync(context, definition, reference, token)
            : await secrets.ResolveAsync(definition, reference, token);
        return material is not null;
    }

    private static async Task<GeneratedCredentialInput?> InputAsync(ManifestCatalogDbContext context,
        DurableOperationKey key, CancellationToken token)
    {
        var accepted = await AcceptedAsync(context, key, token);
        return accepted is not null && GeneratedCredentialContracts.IsProducer(accepted.Value.Node)
            ? GeneratedCredentialInputCodec.Decode(accepted.Value.Node.Input) : null;
    }

    private static Task<OperationOutputRow?> OutputAsync(ManifestCatalogDbContext context, DurableOperationKey key, CancellationToken token)
    {
        var deployment = Guid.Parse(key.DeploymentId.ToString());
        return context.OperationOutputs.AsNoTracking().SingleOrDefaultAsync(r => r.DeploymentId == deployment &&
            r.Generation == key.Generation.Value && r.OperationId == key.OperationId.Value &&
            r.Contract == GeneratedCredentialContracts.OutputContract && r.Key == GeneratedCredentialContracts.Password, token);
    }

    internal static async Task<(DurableOperationNode Node, DeploymentGenerationRow Generation)?> AcceptedAsync(
        ManifestCatalogDbContext context, DurableOperationKey key, CancellationToken token)
    {
        var id = Guid.Parse(key.DeploymentId.ToString());
        var generation = await context.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(r =>
            r.DeploymentId == id && r.Generation == key.Generation.Value, token);
        var owner = await context.ApplicationDeployments.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, token);
        if (generation is null || owner is null || generation.ApplicationRecordId != owner.ApplicationRecordId ||
            generation.EnvironmentRecordId != owner.EnvironmentRecordId || generation.Presence != nameof(DesiredPresence.Present)) return null;
        var graph = DurableOperationGraphCodec.Decode(generation.GraphJson);
        var node = graph.Nodes.SingleOrDefault(n => n.Id == key.OperationId);
        if (node is null) return null;
        // Exact committed graph, persisted deployment and pinned revision, never caller metadata.
        var environment = EnvironmentRecordId.Parse(generation.EnvironmentRecordId.ToString());
        var application = ApplicationRecordId.Parse(generation.ApplicationRecordId.ToString());
        var inputs = generation.Inputs();
        if (DurableOperationGraphCodec.Fingerprint(inputs, graph) != generation.Fingerprint) return null;
        var revision = await context.Revisions.AsNoTracking().SingleOrDefaultAsync(r =>
            r.ApplicationRecordId == generation.ApplicationRecordId && r.LocalRevisionNumber == generation.RevisionNumber, token);
        if (revision is null) return null;
        var spec = NormalizedSnapshotCodec.Decode(revision.Snapshot, revision.InputApiVersion,
            revision.NormalizedModelVersion, revision.ApplicationId, revision.SnapshotFormatVersion);
        if (!spec.Spec.Resources.TryGetValue(node.ResourceId, out var resource) || resource.Type != node.Action.Admission.ResourceContract ||
            resource.ProviderRef != inputs.Providers.Resource(node.ResourceId).Alias)
            return null;
        return (node, generation);
    }

    private static bool StorageFailure(Exception error) =>
        error is DbUpdateException or NpgsqlException or InvalidOperationException or IOException or TimeoutException or ArgumentException;
}
