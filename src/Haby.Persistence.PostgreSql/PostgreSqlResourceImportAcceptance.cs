using System.Security.Cryptography;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

/// <summary>Provider-neutral, all-or-nothing catalog acceptance after adapter verification.
/// No Provider/network calls or material comparison API. An uncertain response only triggers a reread.</summary>
public sealed class PostgreSqlResourceImportAcceptance(IDbContextFactory<ManifestCatalogDbContext> contexts,
    IEnumerable<IExternalOperationOutputContract> contracts, IEnumerable<IProviderConfigurationCodec> codecs)
    : IResourceImportAcceptance
{
    public async ValueTask<ResourceImportReceipt?> FindAsync(DurableOperationKey operation, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        try { return await ResourceImportReceiptIntegrity.ReadAsync(db, operation, cancellationToken); }
        catch (Exception e) when (e is InvalidDataException or JsonException or ArgumentException) { return null; }
    }

    public async ValueTask<ResourceImportAcceptanceResult> CompleteAsync(ResourceImportAcceptance acceptance, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        if (acceptance.Operations.Count is < 1 or > 256 || acceptance.Credentials.Count > 256 || acceptance.Reservations.Count > 1024 ||
            acceptance.Evidence.Format.Length is < 1 or > 128 ||
            System.Text.Encoding.UTF8.GetByteCount(acceptance.Evidence.Json) > 65536) return new(ResourceImportAcceptanceStatus.InputUnavailable);
        acceptance = acceptance with { Operations = acceptance.Operations.ToArray(), Credentials = acceptance.Credentials.ToArray(), Reservations = acceptance.Reservations.ToArray() };
        var first = acceptance.Operations[0].Key;
        var document = Document(acceptance);
        var json = JsonSerializer.Serialize(document, ImportReceiptJsonContext.Default.ImportReceiptDocument);
        _ = ResourceImportReceiptIntegrity.Decode(json);
        var privateCopies = new List<byte[]>();
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deployment = Guid.Parse(first.DeploymentId.ToString());
            await DurableAggregateCompletion.LockAsync(db, deployment, cancellationToken);
            var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == first.Generation.Value, cancellationToken);
            if (source is null) return new(ResourceImportAcceptanceStatus.InputUnavailable);
            var inputs = source.Inputs();
            var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
            if (inputs.Import is null || graph.Baseline is not null || graph.ReusedResources.Count != 0 || graph.RetainedResources.Count != 0 ||
                DurableOperationGraphCodec.Fingerprint(inputs, graph) != source.Fingerprint ||
                !graph.Nodes.Select(x => x.Id).ToHashSet().SetEquals(acceptance.Operations.Select(x => x.Key.OperationId)) ||
                graph.Nodes.Count != acceptance.Operations.Count || graph.Nodes.Any(x => x.Action.Admission.Mode != ResourceAcquisitionMode.ImportUseAndRetain ||
                    !x.RequiresExternalOutput || GeneratedCredentialContracts.IsProducer(x)) ||
                acceptance.Operations.Any(x => x.Key.DeploymentId != first.DeploymentId || x.Key.Generation != first.Generation)) return new(ResourceImportAcceptanceStatus.InputUnavailable);
            foreach (var result in acceptance.Operations)
            {
                var accepted = await PostgreSqlExternalOperationOutputs.AcceptedAsync(db, result.Key, cancellationToken);
                var validator = accepted is null ? null : contracts.SingleOrDefault(x => x.Producer == accepted.Node.Action);
                if (accepted is null || validator is null || !validator.ValidateImportEvidence(accepted, acceptance.Evidence)) return new(ResourceImportAcceptanceStatus.InputUnavailable);
            }
            var prior = await db.ResourceImportReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Deployment == deployment && x.Generation == source.Generation, cancellationToken);
            if (prior is not null) return new(prior.Document == json && await ResourceImportReceiptIntegrity.ReadAsync(db, first, cancellationToken) is not null
                ? ResourceImportAcceptanceStatus.Existing : ResourceImportAcceptanceStatus.Conflict);
            if (!await db.ApplicationDeployments.AsNoTracking().AnyAsync(x => x.Id == deployment && x.DesiredGeneration == source.Generation, cancellationToken))
                return new(ResourceImportAcceptanceStatus.Conflict);
            var expectedCredentials = inputs.Import.Resources.Where(x => x.Credential is not null).ToArray();
            if (acceptance.Credentials.Count != expectedCredentials.Length || acceptance.Credentials.Any(x =>
                x.Owner.EnvironmentRecordId != inputs.Environment || x.Owner.ApplicationRecordId != inputs.Revision.ApplicationRecordId ||
                !expectedCredentials.Any(e => e.Resource == x.Owner.ResourceLocalId && e.Credential == x.Staged))) return new(ResourceImportAcceptanceStatus.InputUnavailable);
            if (acceptance.Reservations.Any(x => x.EnvironmentRecordId != inputs.Environment || x.ApplicationRecordId != inputs.Revision.ApplicationRecordId ||
                !graph.Nodes.Any(n => n.ResourceId == x.ResourceLocalId) || inputs.Providers.Resource(x.ResourceLocalId).Instance != x.ProviderInstanceId ||
                x.Namespace.Length is < 1 or > 128 || x.Value.Length is < 1 or > 512 || x.Namespace.Contains('\0') || x.Value.Contains('\0'))) return new(ResourceImportAcceptanceStatus.InputUnavailable);
            var receipt = new ResourceImportReceiptRow
            {
                Id = Guid.CreateVersion7(),
                Deployment = deployment,
                Generation = source.Generation,
                Fingerprint = ResourceImportReceiptIntegrity.Fingerprint(source.Fingerprint, json),
                Document = json
            };
            db.ResourceImportReceipts.Add(receipt);
            await db.SaveChangesAsync(cancellationToken);
            foreach (var credential in acceptance.Credentials)
                if (!await ConfirmCredentialAsync(db, credential, receipt.Id, at, privateCopies, cancellationToken)) return new(ResourceImportAcceptanceStatus.InputUnavailable);
            foreach (var reservation in acceptance.Reservations)
            {
                var provider = Guid.Parse(reservation.ProviderInstanceId.ToString());
                var existing = await db.ProviderResourceReservations.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderInstanceId == provider &&
                    x.EnvironmentRecordId == source.EnvironmentRecordId && x.ApplicationRecordId == source.ApplicationRecordId &&
                    x.ResourceLocalId == reservation.ResourceLocalId.Value && x.Namespace == reservation.Namespace, cancellationToken);
                if (existing is not null) { if (existing.Value != reservation.Value) return new(ResourceImportAcceptanceStatus.Conflict); continue; }
                db.ProviderResourceReservations.Add(new()
                {
                    ProviderInstanceId = provider,
                    EnvironmentRecordId = source.EnvironmentRecordId,
                    ApplicationRecordId = source.ApplicationRecordId,
                    ResourceLocalId = reservation.ResourceLocalId.Value,
                    Namespace = reservation.Namespace,
                    Value = reservation.Value
                });
            }
            await db.SaveChangesAsync(cancellationToken);
            // Candidate validation and every exact attempt transition remain inside this transaction.
            // Nodes may not depend on each other: the adapter verifies their coupled state before acceptance.
            if (graph.Nodes.Any(x => x.Dependencies.Count != 0 || x.OutputReferences.Count != 0)) return new(ResourceImportAcceptanceStatus.InputUnavailable);
            foreach (var result in acceptance.Operations.OrderBy(x => x.Key.OperationId.Value, StringComparer.Ordinal))
            {
                var pending = new DurableAggregateCompletion.PendingResult(result.Key, result.AttemptId, result.Output);
                var metadata = await DurableAggregateCompletion.InspectPublicationAsync(db, pending, contracts, codecs, cancellationToken);
                if (metadata?.Candidate is null || !await DurableAggregateCompletion.CheckCredentialMetadataAsync(db, metadata, cancellationToken) ||
                    !await DurableOperationTransitions.ConfirmAsync(db, result.Key, result.AttemptId, at, false, cancellationToken)) return new(ResourceImportAcceptanceStatus.InputUnavailable);
                db.OperationOutputs.Add(new()
                {
                    DeploymentId = deployment,
                    Generation = source.Generation,
                    OperationId = result.Key.OperationId.Value,
                    Contract = result.Output.Contract,
                    Key = result.Output.Key,
                    Payload = result.Output.Payload
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            if (await ResourceImportReceiptIntegrity.ReadAsync(db, first, cancellationToken) is null) return new(ResourceImportAcceptanceStatus.InputUnavailable);
            // ResourcesReady is evaluated by the ordinary generation completion path after commit;
            // no provisional association or half pair is ever visible to consumers.
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            return new(ResourceImportAcceptanceStatus.Accepted);
        }
        catch (Exception e) when (e is DbUpdateException or NpgsqlException or IOException or InvalidOperationException or JsonException or ArgumentException or OperationCanceledException)
        {
            try
            {
                await using var confirm = await contexts.CreateDbContextAsync(CancellationToken.None);
                var receipt = await confirm.ResourceImportReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Deployment == Guid.Parse(first.DeploymentId.ToString()) && x.Generation == first.Generation.Value, CancellationToken.None);
                if (receipt?.Document == json && await ResourceImportReceiptIntegrity.ReadAsync(confirm, first, CancellationToken.None) is not null)
                    return new(ResourceImportAcceptanceStatus.Existing);
                foreach (var reservation in acceptance.Reservations)
                {
                    var provider = Guid.Parse(reservation.ProviderInstanceId.ToString());
                    var environment = Guid.Parse(reservation.EnvironmentRecordId.ToString());
                    var application = Guid.Parse(reservation.ApplicationRecordId.ToString());
                    if (await confirm.ProviderResourceReservations.AsNoTracking().AnyAsync(x => x.ProviderInstanceId == provider &&
                        x.Namespace == reservation.Namespace && x.Value == reservation.Value &&
                        (x.EnvironmentRecordId != environment || x.ApplicationRecordId != application || x.ResourceLocalId != reservation.ResourceLocalId.Value), CancellationToken.None))
                        return new(ResourceImportAcceptanceStatus.Conflict);
                }
                return new(ResourceImportAcceptanceStatus.StorageUnavailable);
            }
            catch (Exception reread) when (reread is DbUpdateException or NpgsqlException or IOException or InvalidOperationException or JsonException or ArgumentException or OperationCanceledException)
            { return new(ResourceImportAcceptanceStatus.StorageUnavailable); }
        }
        finally { foreach (var bytes in privateCopies) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task<bool> ConfirmCredentialAsync(ManifestCatalogDbContext db, ImportCredentialConfirmation credential,
        Guid receipt, DateTimeOffset at, List<byte[]> privateCopies, CancellationToken token)
    {
        var row = await db.ProtectedApplicationCredentials.SingleOrDefaultAsync(x => x.Id == credential.Staged.Id, token);
        if (row is null) return false;
        try
        {
            if (row.Environment != Guid.Parse(credential.Owner.EnvironmentRecordId.ToString()) || row.Application != Guid.Parse(credential.Owner.ApplicationRecordId.ToString()) ||
                row.Resource != credential.Owner.ResourceLocalId.Value || PostgreSqlProtectedApplicationCredentialIntake.Reference(row) != credential.Staged ||
                row.ConfirmedReceipt is not null || await db.Secrets.AnyAsync(x => x.EnvironmentRecordId == row.Environment && x.ApplicationRecordId == row.Application && x.ResourceLocalId == row.Resource, token)) return false;
            var secret = SecretRowMapping.Create(new(new SecretOwner.Resource(credential.Owner), SecretPurpose.ApplicationLogin), row.Secret);
            secret.CreatedAt = at;
            db.Secrets.Add(secret);
            db.SecretVersions.Add(new()
            {
                Id = row.Version,
                SecretId = row.Secret,
                Number = 1,
                CreatedAt = at,
                ProtectionFormat = PlaintextProtectionFormat.Name,
                FormatVersion = PlaintextProtectionFormat.Version
            });
            var copy = (byte[])row.Material.Clone(); privateCopies.Add(copy);
            db.SecretPayloads.Add(new() { SecretVersionId = row.Version, Bytes = copy });
            await db.SaveChangesAsync(token);
            secret.CurrentVersionId = row.Version;
            row.ConfirmedReceipt = receipt;
            await db.SaveChangesAsync(token);
            db.Entry(row).State = EntityState.Detached;
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(row.Material); }
    }

    private static ImportReceiptDocument Document(ResourceImportAcceptance acceptance) => new(1, acceptance.Evidence.Format, acceptance.Evidence.Json,
        acceptance.Operations.OrderBy(x => x.Key.OperationId.Value, StringComparer.Ordinal).Select(x => new ImportReceiptMember(x.Key.OperationId.Value,
            x.AttemptId, x.Output.Contract, x.Output.Key, ResourcesReadyCheckpointCodec.Hash(x.Output.Payload))).ToArray(),
        acceptance.Credentials.OrderBy(x => x.Owner.ResourceLocalId.Value, StringComparer.Ordinal).Select(x => new ImportReceiptCredential(x.Owner.ResourceLocalId.Value,
            x.Staged.Id, Guid.Parse(x.Staged.Reference.SecretId.ToString()), Guid.Parse(x.Staged.Reference.SecretVersionId.ToString()))).ToArray(),
        acceptance.Reservations.OrderBy(x => x.ResourceLocalId.Value, StringComparer.Ordinal).ThenBy(x => x.Namespace, StringComparer.Ordinal)
            .Select(x => new ImportReceiptReservation(Guid.Parse(x.ProviderInstanceId.ToString()), x.ResourceLocalId.Value, x.Namespace, x.Value)).ToArray());
}

public static class ResourceImportAcceptanceServices
{
    public static IServiceCollection AddPostgreSqlResourceImportAcceptance(this IServiceCollection services)
    {
        services.TryAddSingleton<IResourceImportAcceptance, PostgreSqlResourceImportAcceptance>();
        return services;
    }
}
