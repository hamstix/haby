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
    ImportReceiptMember[] Operations, ImportReceiptCredential[] Credentials, ImportReceiptReservation[] Reservations);
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ImportReceiptDocument))]
internal partial class ImportReceiptJsonContext : JsonSerializerContext;

internal static class ResourceImportReceiptIntegrity
{
    internal static string Fingerprint(string sourceFingerprint, string document) =>
        ResourcesReadyCheckpointCodec.Hash(sourceFingerprint + "\n" + document);
    internal static ImportReceiptDocument Decode(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1048576) throw new InvalidDataException("Import receipt exceeds bound.");
        var value = JsonSerializer.Deserialize(json, ImportReceiptJsonContext.Default.ImportReceiptDocument);
        if (value is null || value.Version != 1 || value.Operations.Length is < 1 or > 256 ||
            value.Operations.Select(x => x.Operation).Distinct(StringComparer.Ordinal).Count() != value.Operations.Length ||
            value.Credentials.Select(x => x.Resource).Distinct(StringComparer.Ordinal).Count() != value.Credentials.Length)
            throw new InvalidDataException("Invalid import receipt.");
        return value;
    }

    internal static async Task<ResourceImportReceipt?> ReadAsync(ManifestCatalogDbContext db,
        DurableOperationKey key, CancellationToken token)
    {
        var deployment = Guid.Parse(key.DeploymentId.ToString());
        var row = await db.ResourceImportReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Deployment == deployment && x.Generation == key.Generation.Value, token);
        if (row is null) return null;
        var source = await db.DeploymentGenerations.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deployment && x.Generation == key.Generation.Value, token);
        if (source is null || row.Fingerprint != Fingerprint(source.Fingerprint, row.Document)) return null;
        var inputs = source.Inputs();
        var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        var document = Decode(row.Document);
        if (DurableOperationGraphCodec.Fingerprint(inputs, graph) != source.Fingerprint || inputs.Import is null || !graph.Nodes.Select(x => x.Id.Value).ToHashSet(StringComparer.Ordinal).SetEquals(document.Operations.Select(x => x.Operation)) ||
            graph.Nodes.Any(x => x.Action.Admission.Mode != ResourceAcquisitionMode.ImportUseAndRetain) ||
            document.Operations.All(x => x.Operation != key.OperationId.Value)) return null;
        foreach (var resource in inputs.Import.Resources)
        {
            var managed = await db.CurrentResourceManagement.AsNoTracking().SingleOrDefaultAsync(x => x.Deployment == deployment && x.Resource == resource.Resource.Value, token);
            if (managed is null || managed.Environment != source.EnvironmentRecordId || managed.Application != source.ApplicationRecordId ||
                managed.SourceGeneration != source.Generation || !ResourceContinuity.HasAcquisitionAuthority(managed, inputs)) return null;
        }
        var operations = new List<ImportOperationConfirmation>();
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
        var expectedCredentials = inputs.Import.Resources.Where(x => x.Credential is not null).ToArray();
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
            if (!await db.ProviderResourceReservations.AsNoTracking().AnyAsync(x => x.ProviderInstanceId == reservation.Provider &&
                x.EnvironmentRecordId == source.EnvironmentRecordId && x.ApplicationRecordId == source.ApplicationRecordId && x.ResourceLocalId == reservation.Resource &&
                x.Namespace == reservation.Namespace && x.Value == reservation.Value, token)) return null;
        return new(row.Id, new(document.EvidenceFormat, document.EvidenceJson), operations);
    }
}
