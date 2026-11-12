using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>Owner-scoped credential evidence reads, independent of completion registrations.</summary>
public sealed class PostgreSqlGeneratedCredentialReader(IDbContextFactory<ManifestCatalogDbContext> contexts,
    ISecretStore secrets) : IGeneratedCredentialReader
{
    public async ValueTask<GeneratedCredentialInput?> FindInputAsync(DurableOperationKey producer, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            var accepted = await PostgreSqlGeneratedCredentialOperations.AcceptedAsync(context, producer, cancellationToken);
            return accepted is not null && GeneratedCredentialContracts.IsProducer(accepted.Value.Node)
                ? GeneratedCredentialInputCodec.Decode(accepted.Value.Node.Input) : null;
        }
        catch (Exception e) when (StorageFailure(e)) { return null; }
    }

    public async ValueTask<GeneratedCredentialOutput?> ConsumeAsync(DurableOperationKey consumer, OperationOutputRef selector,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            var accepted = await PostgreSqlGeneratedCredentialOperations.AcceptedAsync(context, consumer, cancellationToken);
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

    internal static async Task<bool> ConfirmPublicationAsync(ManifestCatalogDbContext context, DurableOperationKey producer,
        Guid attempt, GeneratedCredentialOutput expected, CancellationToken token)
    {
        if (await InputAsync(context, producer, token) is null) return false;
        var row = await OutputAsync(context, producer, token);
        if (row is null || GeneratedCredentialOutputCodec.Decode(row.Contract, row.Payload) != expected) return false;
        var deployment = Guid.Parse(producer.DeploymentId.ToString());
        return await context.DurableOperations.AnyAsync(r => r.DeploymentId == deployment && r.Generation == producer.Generation.Value &&
            r.OperationId == producer.OperationId.Value && r.State == nameof(DurableOperationState.Succeeded) && r.CurrentAttemptId == attempt && r.ReasonCode == null &&
            context.DurableOperationAttempts.Any(a => a.AttemptId == attempt && a.DeploymentId == r.DeploymentId && a.Generation == r.Generation &&
                a.OperationId == r.OperationId && a.AttemptNumber == r.AttemptNumber && a.Outcome == nameof(DurableAttemptOutcome.Succeeded) &&
                a.DispatchedAt == null && r.DispatchedAt == null && a.FinishedAt != null && a.FinishedAt == r.FinalizedAt), token);
    }

    internal async Task<bool> ConfirmAsync(ManifestCatalogDbContext context, DurableOperationKey producer,
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

    internal async Task<bool> ValidMaterialAsync(ManifestCatalogDbContext context, GeneratedCredentialInput input,
        SecretRef reference, CancellationToken token)
    {
        var metadata = await PostgreSqlGeneratedCredentialSlots.ReadAsync(context, new(input.Owner, input.SlotId), input.GenerationProfile, token, validateMaterial: false);
        if (metadata?.Outcome != GeneratedCredentialOutcome.Existing || metadata.Reference != reference) return false;
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
        var accepted = await PostgreSqlGeneratedCredentialOperations.AcceptedAsync(context, key, token);
        return accepted is not null && GeneratedCredentialContracts.IsProducer(accepted.Value.Node)
            ? GeneratedCredentialInputCodec.Decode(accepted.Value.Node.Input) : null;
    }

    internal static Task<OperationOutputRow?> OutputAsync(ManifestCatalogDbContext context, DurableOperationKey key, CancellationToken token)
    {
        var deployment = Guid.Parse(key.DeploymentId.ToString());
        return context.OperationOutputs.AsNoTracking().SingleOrDefaultAsync(r => r.DeploymentId == deployment &&
            r.Generation == key.Generation.Value && r.OperationId == key.OperationId.Value &&
            r.Contract == GeneratedCredentialContracts.OutputContract && r.Key == GeneratedCredentialContracts.Password, token);
    }

    private static bool StorageFailure(Exception error) =>
        error is DbUpdateException or NpgsqlException or InvalidOperationException or IOException or TimeoutException or ArgumentException;
}
