using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Domain;

namespace Haby.Application;

/// <summary>Explicit exact incomplete-generation intent; never an execution capability.</summary>
public sealed record CorrectiveChangeSetIntent(ApplicationDeploymentId Deployment, DeploymentGeneration ExpectedIncompleteDesiredGeneration);

internal interface IInternalWorkloadCorrectionPlanner
{
    CorrectiveConfirmedObject CaptureConfirmedObject(InternalActionContext previous, string intent, string receipt);
    CorrectiveImageDelta CaptureCorrection(DurablePlanningInputs previous, string previousVersion,
        DurablePlanningInputs target, string targetVersion, ProviderInput.Workload subject);
}
internal sealed record CorrectiveConfirmedObject(string Identity, string Image);
internal sealed record CorrectiveImageDelta(string PreviousImage, string TargetImage);
internal sealed record CorrectiveMember(string Component, string Workload, string State, Guid? ManagedIdentity,
    long? OriginGeneration, string? Operation, Guid? Attempt, string? IntentHash, string? ReceiptHash, CorrectiveConfirmedObject? Confirmation,
    CorrectiveImageDelta Image, string[] Documents);
internal sealed record CorrectivePublication(Guid Permission, Guid Binding, Guid Revision, Guid Token,
    string Status, string OriginHash, string LastRequest, Guid Receipt, string ReceiptHash);
internal sealed record CorrectivePreparation(long Generation, string Component, string Workload, string Document,
    Guid Revision, Guid Checkpoint, string InputFingerprint);
internal sealed record CorrectiveDocument(int Version, Guid Deployment, long ExpectedDesired, long? LastApplied,
    Guid OriginalPlan, Guid OriginalChangeSet, string OriginalFingerprint, Guid ResourcesReady, string ResourceProof,
    CorrectiveMember[] Members, WorkloadUpdateObject[] Objects, WorkloadUpdateBinding[] Bindings,
    CorrectivePublication[] Publications, CorrectivePreparation[] Preparations, string[] InheritedInventoryHashes);
internal sealed class CorrectiveInventory
{
    private readonly string json;
    internal CorrectiveInventory(CorrectiveDocument value)
    {
        if (value.Version != 1 || value.Deployment == Guid.Empty || value.ExpectedDesired < 1 ||
            value.LastApplied >= value.ExpectedDesired || value.OriginalPlan == Guid.Empty || value.OriginalChangeSet == Guid.Empty ||
            value.OriginalFingerprint is not { Length: 67 } || !value.OriginalFingerprint.StartsWith("v6:", StringComparison.Ordinal) ||
            value.OriginalFingerprint[3..].Any(c => !char.IsAsciiHexDigit(c)) ||
            value.ResourcesReady == Guid.Empty || value.Members is not { Length: > 0 and <= 512 } ||
            value.Objects is not { Length: <= 1024 } || value.Bindings is not { Length: <= 512 } ||
            value.Publications is not { Length: <= 1024 } || value.Preparations is not { Length: <= 2048 } ||
            value.InheritedInventoryHashes is not { Length: <= 256 } ||
            value.Publications.Any(x => x.Permission == Guid.Empty || x.Binding == Guid.Empty || x.Revision == Guid.Empty || x.Token == Guid.Empty ||
                x.Status is not ("Published" or "Revoked") || !Hash(x.OriginHash) || !Hash(x.ReceiptHash)) ||
            value.Members.Select(x => (x.Component, x.Workload)).Distinct().Count() != value.Members.Length ||
            value.Members.Any(x => x.State is not ("ConfirmedOwned" or "NeverConfirmedInitialCreate") || x.Documents is not { Length: <= 256 } ||
                x.Image.PreviousImage is not { Length: > 0 and <= 2048 } || x.Image.TargetImage is not { Length: > 0 and <= 2048 } ||
                x.State == "ConfirmedOwned" && (x.ManagedIdentity is null || x.ManagedIdentity == Guid.Empty || x.OriginGeneration is not > 0 || x.Operation is null ||
                    x.Attempt is null || x.Attempt == Guid.Empty || !Hash(x.IntentHash) || !Hash(x.ReceiptHash) || x.Confirmation is null || x.Confirmation.Identity is not { Length: > 0 and <= 256 } || x.Confirmation.Image is not { Length: > 0 and <= 2048 }) ||
                x.State == "NeverConfirmedInitialCreate" && (x.Attempt is not null || x.ReceiptHash is not null || x.Confirmation is not null)))
            throw new InvalidDataException("Invalid corrective inventory.");
        var proof = ResourcesReadyCheckpointCodec.Decode(value.ResourceProof);
        if (proof.Id != value.ResourcesReady || proof.DeploymentId != value.Deployment || proof.Generation > value.ExpectedDesired ||
            proof.Generation == value.ExpectedDesired && proof.InputFingerprint != value.OriginalFingerprint) throw new InvalidDataException("Corrective Resource proof mismatch.");
        json = JsonSerializer.Serialize(value, CorrectiveMetadata.Default.CorrectiveDocument);
        if (Encoding.UTF8.GetByteCount(json) > 1_048_576) throw new InvalidDataException("Corrective inventory exceeds bound.");
    }
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal CorrectiveDocument Document => JsonSerializer.Deserialize(json, CorrectiveMetadata.Default.CorrectiveDocument)!;
    internal string Fingerprint => ResourcesReadyCheckpointCodec.Hash(json);
}
internal static class CorrectiveIntentCodec
{
    internal static string? Encode(CorrectiveChangeSetIntent? value) => value is null ? null :
        JsonSerializer.Serialize(new CorrectiveIntentDocument(1, Guid.Parse(value.Deployment.ToString()), value.ExpectedIncompleteDesiredGeneration.Value), CorrectiveMetadata.Default.CorrectiveIntentDocument);
    internal static CorrectiveChangeSetIntent? Decode(string? json)
    {
        if (json is null) return null;
        try
        {
            if (json.Length > 256) throw new InvalidDataException("Corrective intent exceeds bound.");
            var value = JsonSerializer.Deserialize(json, CorrectiveMetadata.Default.CorrectiveIntentDocument);
            if (value is null || value.Version != 1 || value.Deployment == Guid.Empty || value.ExpectedDesired < 1)
                throw new InvalidDataException("Invalid corrective intent.");
            return new(ApplicationDeploymentId.Parse(value.Deployment.ToString()), DeploymentGeneration.Create(value.ExpectedDesired));
        }
        catch (JsonException) { throw new InvalidDataException("Invalid corrective intent."); }
    }
}
internal sealed record CorrectiveIntentDocument(int Version, Guid Deployment, long ExpectedDesired);
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(CorrectiveDocument))]
[JsonSerializable(typeof(CorrectiveIntentDocument))]
internal partial class CorrectiveMetadata : JsonSerializerContext;
