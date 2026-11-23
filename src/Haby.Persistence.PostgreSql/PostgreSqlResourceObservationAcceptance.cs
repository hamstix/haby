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
public sealed class PostgreSqlResourceObservationAcceptance(IDbContextFactory<ManifestCatalogDbContext> contexts,
    IEnumerable<IExternalOperationOutputContract> contracts, IEnumerable<IProviderConfigurationCodec> codecs, Func<DurableActionRegistry> registryFactory)
    : IResourceObservationAcceptance
{
    public async ValueTask<ResourceObservationReceipt?> FindAsync(DurableOperationKey operation, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        try { return await ResourceObservationReceiptIntegrity.ReadAsync(db, operation, cancellationToken); }
        catch (Exception e) when (e is InvalidDataException or JsonException or ArgumentException) { return null; }
    }

    public async ValueTask<ResourceObservationAcceptanceResult> CompleteAsync(ResourceObservationAcceptance acceptance, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        if (acceptance.Operations.Count is < 1 or > 256 || acceptance.Credentials.Count > 256 || acceptance.Reservations.Count > 1024 ||
            acceptance.Evidence.Format.Length is < 1 or > 128 ||
            System.Text.Encoding.UTF8.GetByteCount(acceptance.Evidence.Json) > 65536) return new(ResourceObservationAcceptanceStatus.InputUnavailable);
        acceptance = acceptance with { Operations = acceptance.Operations.ToArray(), Credentials = acceptance.Credentials.ToArray(), Reservations = acceptance.Reservations.ToArray() };
        var first = acceptance.Operations[0].Key;
        string? json = null;
        string? groupId = null;
        var privateCopies = new List<byte[]>();
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deployment = Guid.Parse(first.DeploymentId.ToString());
            var identity = await db.DeploymentGenerations.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == first.Generation.Value)
                .Select(x => new { x.EnvironmentRecordId, x.ApplicationRecordId }).SingleOrDefaultAsync(cancellationToken);
            if (identity is null) return new(ResourceObservationAcceptanceStatus.InputUnavailable);
            await ImportMutationGate.LockAsync(db, identity.EnvironmentRecordId, identity.ApplicationRecordId, cancellationToken);
            await DurableAggregateCompletion.LockAsync(db, deployment, cancellationToken);
            var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == first.Generation.Value, cancellationToken);
            if (source is null) return new(ResourceObservationAcceptanceStatus.InputUnavailable);
            var inputs = source.Inputs();
            var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
            var registry = registryFactory();
            var group = graph.ObservationGroups.SingleOrDefault(x => x.Operations.Contains(first.OperationId));
            if (group is null || graph.Baseline is not null || graph.ReusedResources.Count != 0 || graph.RetainedResources.Count != 0 ||
                DurableOperationGraphCodec.Fingerprint(inputs, graph) != source.Fingerprint ||
                !group.Operations.ToHashSet().SetEquals(acceptance.Operations.Select(x => x.Key.OperationId)) ||
                group.Operations.Count != acceptance.Operations.Count ||
                acceptance.Operations.Any(x => x.Key.DeploymentId != first.DeploymentId || x.Key.Generation != first.Generation) ||
                !new DurableActionAdmission(registry).Supports(graph)) return new(ResourceObservationAcceptanceStatus.InputUnavailable);
            groupId = group.Id;
            var members = graph.Nodes.Where(x => group.Operations.Contains(x.Id)).ToArray();
            if (group.Mode == ResourceAcquisitionMode.Reference && members.Any(x => registry.Find(x.Action) is not IReadOnlyResourceObservationHandler))
                return new(ResourceObservationAcceptanceStatus.InputUnavailable);
            var resources = members.Select(x => x.ResourceId).Distinct().ToArray();
            var associations = await db.ResourceManagement.AsNoTracking().Where(x => x.Deployment == deployment && x.SourceGeneration == source.Generation &&
                resources.Select(r => r.Value).Contains(x.Resource)).ToArrayAsync(cancellationToken);
            if (associations.Length != resources.Length || associations.Any(x => !ResourceContinuity.HasAcquisitionAuthority(x, inputs)))
                return new(ResourceObservationAcceptanceStatus.InputUnavailable);
            var document = Document(acceptance, group, associations);
            json = JsonSerializer.Serialize(document, ImportReceiptJsonContext.Default.ImportReceiptDocument);
            _ = ResourceObservationReceiptIntegrity.Decode(json);
            foreach (var result in acceptance.Operations)
            {
                var accepted = await PostgreSqlExternalOperationOutputs.AcceptedAsync(db, result.Key, cancellationToken);
                var validator = accepted is null ? null : contracts.SingleOrDefault(x => x.Producer == accepted.Node.Action);
                if (accepted is null || validator is null || !validator.ValidateObservationEvidence(accepted, acceptance.Evidence) ||
                    group.Mode == ResourceAcquisitionMode.Reference && !validator.ValidateReferenceIdentities(accepted, acceptance.Reservations)) return new(ResourceObservationAcceptanceStatus.InputUnavailable);
            }
            var prior = await db.ResourceObservationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Deployment == deployment && x.Generation == source.Generation && x.Group == group.Id, cancellationToken);
            if (prior is not null) return new(prior.Document == json && await ResourceObservationReceiptIntegrity.ReadAsync(db, first, cancellationToken) is not null
                ? ResourceObservationAcceptanceStatus.Existing : ResourceObservationAcceptanceStatus.Conflict);
            if (!await db.ApplicationDeployments.AsNoTracking().AnyAsync(x => x.Id == deployment && x.DesiredGeneration == source.Generation, cancellationToken))
                return new(ResourceObservationAcceptanceStatus.Conflict);
            if (group.Mode == ResourceAcquisitionMode.ImportUseAndRetain && inputs.Import is { } import &&
                !await ImportAdmissionAuthority.OwnsInputsAsync(db, source.EnvironmentRecordId, source.ApplicationRecordId, import, cancellationToken))
                return new(ResourceObservationAcceptanceStatus.Conflict);
            var expectedCredentials = inputs.Acquisitions.Resources.Where(x => resources.Contains(x.Resource) && x.Credential is not null).ToArray();
            if (acceptance.Credentials.Count != expectedCredentials.Length || acceptance.Credentials.Any(x =>
                x.Owner.EnvironmentRecordId != inputs.Environment || x.Owner.ApplicationRecordId != inputs.Revision.ApplicationRecordId ||
                !expectedCredentials.Any(e => e.Resource == x.Owner.ResourceLocalId && e.Credential == x.Staged))) return new(ResourceObservationAcceptanceStatus.InputUnavailable);
            if (acceptance.Reservations.Any(x => x.EnvironmentRecordId != inputs.Environment || x.ApplicationRecordId != inputs.Revision.ApplicationRecordId ||
                !members.Any(n => n.ResourceId == x.ResourceLocalId) || inputs.Providers.Resource(x.ResourceLocalId).Instance != x.ProviderInstanceId ||
                x.Namespace.Length is < 1 or > 128 || x.Value.Length is < 1 or > 512 || x.Namespace.Contains('\0') || x.Value.Contains('\0'))) return new(ResourceObservationAcceptanceStatus.InputUnavailable);
            // All-member prerequisites and active authority also govern direct acceptance calls.
            foreach (var association in associations)
                if (!await ResourceObservationReceiptIntegrity.IsActiveAsync(db, association.Id, cancellationToken))
                    return new(ResourceObservationAcceptanceStatus.Conflict);
            if (group.Mode == ResourceAcquisitionMode.Reference && resources.Any(r => !acceptance.Reservations.Any(x => x.ResourceLocalId == r)))
                return new(ResourceObservationAcceptanceStatus.InputUnavailable);
            foreach (var member in members)
                foreach (var dependency in member.Dependencies)
                {
                    var prerequisite = graph.Nodes.Single(x => x.Id == dependency);
                    var key = first with { OperationId = dependency };
                    var output = await db.OperationOutputs.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == source.Generation &&
                        x.OperationId == dependency.Value, cancellationToken);
                    var operation = await db.DurableOperations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == source.Generation && x.OperationId == dependency.Value, cancellationToken);
                    var validator = contracts.SingleOrDefault(x => x.Producer == prerequisite.Action);
                    if (output is null || operation?.CurrentAttemptId is not { } attempt || validator is null ||
                        prerequisite.RequiredOutput != new ExternalOperationOutputIdentity(output.Contract, output.Key) ||
                        member.OutputReferences.Where(x => x.ProducerOperationId == dependency).Any(x => x.OutputContract != output.Contract || x.OutputKey != output.Key) ||
                        !validator.Validate(prerequisite, new(output.Contract, output.Key, output.Payload)) ||
                        !await PostgreSqlExternalOperationOutputs.ConfirmPublicationAsync(db, key, attempt, new(output.Contract, output.Key, output.Payload), cancellationToken))
                        return new(ResourceObservationAcceptanceStatus.InputUnavailable);
                }
            var receipt = new ResourceObservationReceiptRow
            {
                Id = Guid.CreateVersion7(),
                Deployment = deployment,
                Generation = source.Generation,
                Group = group.Id,
                Fingerprint = ResourceObservationReceiptIntegrity.Fingerprint(source.Fingerprint, json),
                Document = json
            };
            db.ResourceObservationReceipts.Add(receipt);
            await db.SaveChangesAsync(cancellationToken);
            foreach (var credential in acceptance.Credentials)
                if (!await ConfirmCredentialAsync(db, credential, receipt.Id, at, privateCopies, cancellationToken)) return new(ResourceObservationAcceptanceStatus.InputUnavailable);
            foreach (var reservation in acceptance.Reservations)
            {
                var provider = Guid.Parse(reservation.ProviderInstanceId.ToString());
                if (group.Mode == ResourceAcquisitionMode.Reference)
                {
                    db.ResourceIdentityClaims.Add(new()
                    {
                        Provider = provider,
                        Namespace = reservation.Namespace,
                        Value = reservation.Value,
                        Environment = source.EnvironmentRecordId,
                        Application = source.ApplicationRecordId,
                        Resource = reservation.ResourceLocalId.Value,
                        Association = associations.Single(x => x.Resource == reservation.ResourceLocalId.Value).Id
                    });
                    continue;
                }
                var existing = await db.ProviderResourceReservations.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderInstanceId == provider &&
                    x.EnvironmentRecordId == source.EnvironmentRecordId && x.ApplicationRecordId == source.ApplicationRecordId &&
                    x.ResourceLocalId == reservation.ResourceLocalId.Value && x.Namespace == reservation.Namespace, cancellationToken);
                if (existing is not null) { if (existing.Value != reservation.Value) return new(ResourceObservationAcceptanceStatus.Conflict); continue; }
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
            // Intra-group dependencies were refused by graph validation; all other prerequisites were checked above.
            foreach (var result in acceptance.Operations.OrderBy(x => x.Key.OperationId.Value, StringComparer.Ordinal))
            {
                var pending = new DurableAggregateCompletion.PendingResult(result.Key, result.AttemptId, result.Output);
                var metadata = await DurableAggregateCompletion.InspectPublicationAsync(db, pending, contracts, codecs, cancellationToken);
                if (metadata?.Candidate is null || !await DurableAggregateCompletion.CheckCredentialMetadataAsync(db, metadata, cancellationToken) ||
                    !await DurableOperationTransitions.ConfirmAsync(db, result.Key, result.AttemptId, at, false, cancellationToken)) return new(ResourceObservationAcceptanceStatus.InputUnavailable);
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
            if (await ResourceObservationReceiptIntegrity.ReadAsync(db, first, cancellationToken) is null) return new(ResourceObservationAcceptanceStatus.InputUnavailable);
            // ResourcesReady is evaluated by the ordinary generation completion path after commit;
            // no provisional association or half pair is ever visible to consumers.
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            return new(ResourceObservationAcceptanceStatus.Accepted);
        }
        catch (Exception e) when (e is DbUpdateException or NpgsqlException or IOException or InvalidOperationException or JsonException or ArgumentException or OperationCanceledException)
        {
            try
            {
                await using var confirm = await contexts.CreateDbContextAsync(CancellationToken.None);
                var receipt = await confirm.ResourceObservationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Deployment == Guid.Parse(first.DeploymentId.ToString()) && x.Generation == first.Generation.Value && x.Group == groupId, CancellationToken.None);
                if (json is not null && receipt?.Document == json && await ResourceObservationReceiptIntegrity.ReadAsync(confirm, first, CancellationToken.None) is not null)
                    return new(ResourceObservationAcceptanceStatus.Existing);
                foreach (var reservation in acceptance.Reservations)
                {
                    var provider = Guid.Parse(reservation.ProviderInstanceId.ToString());
                    var environment = Guid.Parse(reservation.EnvironmentRecordId.ToString());
                    var application = Guid.Parse(reservation.ApplicationRecordId.ToString());
                    if (await confirm.ProviderResourceReservations.AsNoTracking().AnyAsync(x => x.ProviderInstanceId == provider &&
                        x.Namespace == reservation.Namespace && x.Value == reservation.Value &&
                        (x.EnvironmentRecordId != environment || x.ApplicationRecordId != application || x.ResourceLocalId != reservation.ResourceLocalId.Value), CancellationToken.None) ||
                        await confirm.ResourceIdentityClaims.AsNoTracking().AnyAsync(x => x.Provider == provider && x.Namespace == reservation.Namespace &&
                            (x.Value == reservation.Value || x.Environment == environment && x.Application == application && x.Resource == reservation.ResourceLocalId.Value), CancellationToken.None))
                        return new(ResourceObservationAcceptanceStatus.Conflict);
                }
                return new(ResourceObservationAcceptanceStatus.StorageUnavailable);
            }
            catch (Exception reread) when (reread is DbUpdateException or NpgsqlException or IOException or InvalidOperationException or JsonException or ArgumentException or OperationCanceledException)
            { return new(ResourceObservationAcceptanceStatus.StorageUnavailable); }
        }
        finally { foreach (var bytes in privateCopies) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task<bool> ConfirmCredentialAsync(ManifestCatalogDbContext db, ImportedCredentialConfirmation credential,
        Guid receipt, DateTimeOffset at, List<byte[]> privateCopies, CancellationToken token)
    {
        var row = await db.ProtectedApplicationCredentials.SingleOrDefaultAsync(x => x.Id == credential.Staged.Id, token);
        if (row is null) return false;
        try
        {
            if (row.Environment != Guid.Parse(credential.Owner.EnvironmentRecordId.ToString()) || row.Application != Guid.Parse(credential.Owner.ApplicationRecordId.ToString()) ||
                row.Resource != credential.Owner.ResourceLocalId.Value || PostgreSqlProtectedApplicationCredentialIntake.Reference(row) != credential.Staged ||
                !await db.ProtectedCredentialOwners.AsNoTracking().AnyAsync(x => x.Stage == row.Id && x.Request == row.Request, token) ||
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

    private static ImportReceiptDocument Document(ResourceObservationAcceptance acceptance, ResourceObservationGroup group, ResourceManagementRow[] associations) => new(2, acceptance.Evidence.Format, acceptance.Evidence.Json,
        acceptance.Operations.OrderBy(x => x.Key.OperationId.Value, StringComparer.Ordinal).Select(x => new ImportReceiptMember(x.Key.OperationId.Value,
            x.AttemptId, x.Output.Contract, x.Output.Key, ResourcesReadyCheckpointCodec.Hash(x.Output.Payload))).ToArray(),
        acceptance.Credentials.OrderBy(x => x.Owner.ResourceLocalId.Value, StringComparer.Ordinal).Select(x => new ImportReceiptCredential(x.Owner.ResourceLocalId.Value,
            x.Staged.Id, Guid.Parse(x.Staged.Reference.SecretId.ToString()), Guid.Parse(x.Staged.Reference.SecretVersionId.ToString()))).ToArray(),
        acceptance.Reservations.OrderBy(x => x.ResourceLocalId.Value, StringComparer.Ordinal).ThenBy(x => x.Namespace, StringComparer.Ordinal)
            .Select(x => new ImportReceiptReservation(Guid.Parse(x.ProviderInstanceId.ToString()), x.ResourceLocalId.Value, x.Namespace, x.Value)).ToArray(), group.Id, (int)group.Mode,
        associations.OrderBy(x => x.Resource, StringComparer.Ordinal).Select(x => new ImportReceiptAssociation(x.Resource, x.Id)).ToArray());
}

public static class ResourceObservationAcceptanceServices
{
    public static IServiceCollection AddPostgreSqlResourceObservationAcceptance(this IServiceCollection services)
    {
        services.TryAddSingleton<Func<DurableActionRegistry>>(p => () => p.GetRequiredService<DurableActionRegistry>());
        services.TryAddSingleton<IResourceObservationAcceptance, PostgreSqlResourceObservationAcceptance>();
        return services;
    }
}
