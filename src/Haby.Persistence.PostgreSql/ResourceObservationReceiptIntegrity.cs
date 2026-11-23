using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed record ImportReceiptMember(string Operation, Guid Attempt, string Contract, string Key, string Fingerprint);
internal sealed record ImportReceiptCredential(string Resource, Guid Staged, Guid Secret, Guid Version);
internal sealed record ImportReceiptReservation(Guid Provider, string Resource, string Namespace, string Value);
internal sealed record ImportReceiptDocument(int Version, string EvidenceFormat, string EvidenceJson,
    ImportReceiptMember[] Operations, ImportReceiptCredential[] Credentials, ImportReceiptReservation[] Reservations, string Group, int Mode, ImportReceiptAssociation[] Associations);
internal sealed record ImportReceiptAssociation(string Resource, Guid Management);
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ImportReceiptDocument))]
internal partial class ImportReceiptJsonContext : JsonSerializerContext;

internal static class ResourceObservationReceiptIntegrity
{
    internal static string Fingerprint(string sourceFingerprint, string document) =>
        ResourcesReadyCheckpointCodec.Hash(sourceFingerprint + "\n" + document);
    internal static ImportReceiptDocument Decode(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1048576) throw new InvalidDataException("Observation receipt exceeds bound.");
        var value = JsonSerializer.Deserialize(json, ImportReceiptJsonContext.Default.ImportReceiptDocument);
        if (value is null || value.Version != 2 || value.Operations.Length is < 1 or > 256 ||
            value.Operations.Select(x => x.Operation).Distinct(StringComparer.Ordinal).Count() != value.Operations.Length ||
            value.Credentials.Select(x => x.Resource).Distinct(StringComparer.Ordinal).Count() != value.Credentials.Length)
            throw new InvalidDataException("Invalid observation receipt.");
        return value;
    }

    internal static Task<bool> IsActiveAsync(ManifestCatalogDbContext db, Guid association, CancellationToken token) =>
        (from head in db.ResourceManagementHeads.AsNoTracking()
         join history in db.ResourceManagement.AsNoTracking() on head.Management equals history.Id
         where history.Id == association && head.Environment == history.Environment && head.Application == history.Application && head.Resource == history.Resource
         select history.Id).AnyAsync(token);

    internal static async Task<bool> HasActiveAssociationAsync(ManifestCatalogDbContext db, DurableOperationKey key, CancellationToken token)
    {
        var receipt = await ReadAsync(db, key, token);
        if (receipt is null) return false;
        var row = await db.ResourceObservationReceipts.AsNoTracking().SingleAsync(x => x.Id == receipt.Id, token);
        var document = Decode(row.Document);
        foreach (var association in document.Associations)
            if (!await IsActiveAsync(db, association.Management, token)) return false;
        return true;
    }

    internal static async Task<ResourceObservationReceipt?> ReadAsync(ManifestCatalogDbContext db,
        DurableOperationKey key, CancellationToken token)
    {
        var deployment = Guid.Parse(key.DeploymentId.ToString());
        var sourceGraph = await db.DeploymentGenerations.AsNoTracking().Where(x => x.DeploymentId == deployment && x.Generation == key.Generation.Value).Select(x => x.GraphJson).SingleOrDefaultAsync(token);
        if (sourceGraph is null) return null;
        var frozenGroup = DurableOperationGraphCodec.Decode(sourceGraph).ObservationGroups.SingleOrDefault(x => x.Operations.Contains(key.OperationId));
        if (frozenGroup is null) return null;
        var row = await db.ResourceObservationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Deployment == deployment && x.Generation == key.Generation.Value && x.Group == frozenGroup.Id, token);
        if (row is null) return null;
        var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == key.Generation.Value, token);
        if (source is null || row.Fingerprint != Fingerprint(source.Fingerprint, row.Document)) return null;
        var inputs = source.Inputs();
        var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        var document = Decode(row.Document);
        var group = graph.ObservationGroups.SingleOrDefault(x => x.Id == document.Group);
        if (DurableOperationGraphCodec.Fingerprint(inputs, graph) != source.Fingerprint || group is null || row.Group != group.Id ||
            document.Mode != (int)group.Mode || !group.Operations.Select(x => x.Value).ToHashSet(StringComparer.Ordinal).SetEquals(document.Operations.Select(x => x.Operation)) ||
            document.Operations.All(x => x.Operation != key.OperationId.Value)) return null;
        var resources = group.Operations.Select(id => graph.Nodes.Single(x => x.Id == id).ResourceId).Distinct().ToArray();
        if (document.Associations.Length != resources.Length || document.Associations.Select(x => x.Resource).Distinct().Count() != resources.Length) return null;
        foreach (var resource in resources)
        {
            var association = document.Associations.SingleOrDefault(x => x.Resource == resource.Value);
            if (association is null) return null;
            // Historical proof uses immutable source identity, never the current head.
            var managed = await db.ResourceManagement.AsNoTracking().SingleOrDefaultAsync(x => x.Id == association.Management, token);
            if (managed is null || managed.Deployment != deployment || managed.Resource != resource.Value ||
                managed.SourceGeneration != source.Generation || !ResourceContinuity.HasAcquisitionAuthority(managed, inputs)) return null;
        }
        var operations = new List<ObservationOperationConfirmation>();
        foreach (var member in document.Operations)
        {
            var node = graph.Nodes.Single(x => x.Id.Value == member.Operation);
            if (node.RequiredOutput != new ExternalOperationOutputIdentity(member.Contract, member.Key)) return null;
            var operation = await db.DurableOperations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment &&
                x.Generation == row.Generation && x.OperationId == member.Operation, token);
            if (operation is null || operation.State != nameof(DurableOperationState.Succeeded) || operation.CurrentAttemptId != member.Attempt ||
                operation.ReasonCode is not null || operation.DispatchedAt is null || operation.FinalizedAt is null ||
                !await db.DurableOperationAttempts.AsNoTracking().AnyAsync(x => x.AttemptId == member.Attempt && x.DeploymentId == deployment &&
                    x.Generation == row.Generation && x.OperationId == member.Operation && x.AttemptNumber == operation.AttemptNumber &&
                    x.Outcome == nameof(DurableAttemptOutcome.Succeeded) && x.DispatchedAt == operation.DispatchedAt && x.FinishedAt == operation.FinalizedAt, token)) return null;
            var output = await db.OperationOutputs.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == row.Generation &&
                x.OperationId == member.Operation && x.Contract == member.Contract && x.Key == member.Key, token);
            if (output is null || ResourcesReadyCheckpointCodec.Hash(output.Payload) != member.Fingerprint) return null;
            operations.Add(new(key with { OperationId = node.Id }, member.Attempt, new(output.Contract, output.Key, output.Payload)));
        }
        var expectedCredentials = inputs.Acquisitions.Resources.Where(x => resources.Contains(x.Resource) && x.Credential is not null).ToArray();
        if (document.Credentials.Length != expectedCredentials.Length) return null;
        foreach (var member in document.Credentials)
        {
            var expected = expectedCredentials.SingleOrDefault(x => x.Resource.Value == member.Resource)?.Credential;
            if (expected is null || expected.Id != member.Staged || expected.Reference.SecretId.ToString() != member.Secret.ToString() ||
                expected.Reference.SecretVersionId.ToString() != member.Version.ToString() ||
                !await db.ProtectedApplicationCredentials.AsNoTracking().AnyAsync(x => x.Id == member.Staged && x.Environment == source.EnvironmentRecordId &&
                    x.Application == source.ApplicationRecordId && x.Resource == member.Resource && x.Secret == member.Secret && x.Version == member.Version && x.ConfirmedReceipt == row.Id, token)) return null;
            var definition = new SecretDefinition(new SecretOwner.Resource(new(inputs.Environment, inputs.Revision.ApplicationRecordId,
                ManifestLocalId.Create(member.Resource))), SecretPurpose.ApplicationLogin);
            var secret = await db.Secrets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == member.Secret, token);
            if (secret is null || !SecretRowMapping.Matches(definition, secret) || secret.CurrentVersionId != member.Version ||
                !await db.SecretVersions.AnyAsync(x => x.Id == member.Version && x.SecretId == member.Secret &&
                    x.ProtectionFormat == PlaintextProtectionFormat.Name && x.FormatVersion == PlaintextProtectionFormat.Version, token)) return null;
        }
        foreach (var reservation in document.Reservations)
        {
            if (group.Mode == ResourceAcquisitionMode.Reference)
            {
                var association = document.Associations.Single(a => a.Resource == reservation.Resource).Management;
                if (!await db.ResourceIdentityClaims.AsNoTracking().AnyAsync(x => x.Provider == reservation.Provider && x.Namespace == reservation.Namespace &&
                    x.Value == reservation.Value && x.Association == association, token)) return null;
            }
            else if (!await db.ProviderResourceReservations.AsNoTracking().AnyAsync(x => x.ProviderInstanceId == reservation.Provider &&
                x.EnvironmentRecordId == source.EnvironmentRecordId && x.ApplicationRecordId == source.ApplicationRecordId && x.ResourceLocalId == reservation.Resource &&
                x.Namespace == reservation.Namespace && x.Value == reservation.Value, token)) return null;
        }
        return new(row.Id, new(document.EvidenceFormat, document.EvidenceJson), operations);
    }
}
