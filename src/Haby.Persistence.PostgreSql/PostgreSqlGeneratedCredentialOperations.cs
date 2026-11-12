using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>Internal catalog-only producer completion and exact, owner-scoped consumption.</summary>
public sealed class PostgreSqlGeneratedCredentialOperations(IDbContextFactory<ManifestCatalogDbContext> contexts,
    ISecretStore secrets, IEnumerable<IExternalOperationOutputContract>? contracts = null,
    IEnumerable<IProviderConfigurationCodec>? codecs = null) : IGeneratedCredentialOperations
{
    private readonly PostgreSqlGeneratedCredentialReader reader = new(contexts, secrets);

    public ValueTask<GeneratedCredentialInput?> FindInputAsync(DurableOperationKey producer, CancellationToken token) => reader.FindInputAsync(producer, token);

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
            var metadata = await DurableAggregateCompletion.InspectMetadataAsync(context, Guid.Parse(producer.DeploymentId.ToString()), producer.Generation.Value,
                contracts ?? [], codecs ?? [], cancellationToken, pending: new(producer,
                    new(GeneratedCredentialContracts.OutputContract, GeneratedCredentialContracts.Password, GeneratedCredentialOutputCodec.Encode(output))));
            if (metadata is null) return false;
            var prepared = await DurableAggregateCompletion.PrepareAsync(context, metadata, cancellationToken);
            if (prepared is null) return false;
            // Resolve material before the aggregate lock. Inside the transaction recheck
            // exact slot metadata on this connection; never resolve secrets under that lock.
            if (!await reader.ValidMaterialAsync(context, input, output.Reference, cancellationToken)) return false;
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await DurableAggregateCompletion.LockAsync(context, Guid.Parse(producer.DeploymentId.ToString()), cancellationToken);
            var slot = await PostgreSqlGeneratedCredentialSlots.ReadAsync(context, new(input.Owner, input.SlotId), input.GenerationProfile, cancellationToken, validateMaterial: false);
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
                return await reader.ConfirmAsync(context, producer, output, cancellationToken);
            }
            var existing = await PostgreSqlGeneratedCredentialReader.OutputAsync(context, producer, cancellationToken);
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
            _ = await DurableAggregateCompletion.TryAdvanceConfirmedAsync(context, deployment, generation, cancellationToken, contracts, codecs, prepared);
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
                return await reader.ConfirmAsync(reread, producer, output, CancellationToken.None);
            }
            catch (Exception reread) when (StorageFailure(reread)) { return false; }
        }
    }

    public ValueTask<GeneratedCredentialOutput?> ConsumeAsync(DurableOperationKey consumer, OperationOutputRef selector, CancellationToken token) =>
        reader.ConsumeAsync(consumer, selector, token);

    private static async Task<bool> DependenciesReadyAsync(ManifestCatalogDbContext context, DurableOperationKey producer,
        string[] dependencyIds, CancellationToken token)
    {
        var deployment = Guid.Parse(producer.DeploymentId.ToString());
        return await context.DurableOperations.CountAsync(r => r.DeploymentId == deployment &&
            r.Generation == producer.Generation.Value && dependencyIds.Contains(r.OperationId) &&
            r.State == nameof(DurableOperationState.Succeeded), token) == dependencyIds.Length;
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
