using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Domain;

namespace Haby.Application;

// Planning capability only. Implementing this interface confers no dispatch authority.
internal interface IInternalWorkloadUpdatePlanner
{
    DurableActionPayload CaptureUpdate(InternalActionContext previous, DurablePlanningInputs target, string targetVersion, string intent, string receipt);
}

internal enum WorkloadUpdateRefusal { BaselineUnavailable, InputChanged, EvidenceUnavailable, UnsupportedWorkload }
internal sealed class WorkloadUpdateRefusedException(WorkloadUpdateRefusal reason) : Exception("Workload update planning refused.")
{
    internal WorkloadUpdateRefusal Reason { get; } = reason;
}

// Generic persisted authority: adapter data remains in typed, versioned payloads.
// Target documents are obligations, never invented revision IDs or permissions.
internal sealed record WorkloadUpdateMember(string Component, string Workload, Guid ManagedIdentity,
    string TargetContract, int TargetFormat, string TargetPayload, string Operation, Guid Attempt,
    Guid? Observation, DurableActionPayload Delta, string[] PrepareDocuments);
internal sealed record WorkloadUpdateObject(Guid ManagedIdentity, string Role, string Contract, int Format,
    string Payload, long OriginGeneration, string OriginOperation);
internal sealed record WorkloadUpdateBinding(Guid Id, Guid ManagedIdentity, Guid Token, string Contract, int Format, string Payload);
internal sealed record WorkloadUpdatePublication(Guid Permission, Guid Binding, Guid Revision, Guid PermissionToken,
    Guid BindingToken, string Request, Guid Receipt, string ReceiptHash);
internal sealed record WorkloadUpdateDocument(int Version, Guid Deployment, long BaselineGeneration, long TargetGeneration, Guid Plan,
    Guid ChangeSet, string Fingerprint, Guid Completion, string CompletionHash, WorkloadUpdateMember[] Members,
    WorkloadUpdateObject[] RetainedObjects, WorkloadUpdateBinding[] RetainedBindings,
    WorkloadUpdatePublication[] RetainedPublications, WorkloadUpdatePublication[] ObsoletePublications);

internal sealed class WorkloadUpdateInventory
{
    private readonly string json;
    internal WorkloadUpdateInventory(WorkloadUpdateDocument document)
    {
        if (document.Version != 1 || document.Deployment == Guid.Empty || document.BaselineGeneration < 1 ||
            document.TargetGeneration < document.BaselineGeneration || document.TargetGeneration > document.BaselineGeneration + 1 ||
            document.Plan == Guid.Empty || document.ChangeSet == Guid.Empty || document.Completion == Guid.Empty ||
            !InitialRetirementInventory.Hash(document.CompletionHash) || document.Fingerprint is not { Length: 67 } ||
            document.Members is not { Length: > 0 and <= 512 } || document.RetainedObjects is not { Length: <= 1024 } ||
            document.RetainedBindings is not { Length: <= 512 } || document.RetainedPublications is not { Length: <= 512 } || document.ObsoletePublications is not { Length: <= 512 } ||
            document.Members.Any(x => x.ManagedIdentity == Guid.Empty || x.Attempt == Guid.Empty || x.Delta is null || x.PrepareDocuments is null) ||
            document.Members.Select(x => (x.Component, x.Workload)).Distinct().Count() != document.Members.Length)
            throw new InvalidDataException("Invalid Workload update inventory.");
        json = JsonSerializer.Serialize(document, WorkloadUpdateMetadata.Default.WorkloadUpdateDocument);
        if (Encoding.UTF8.GetByteCount(json) > 1_048_576) throw new InvalidDataException("Workload update inventory exceeds bound.");
    }
    internal WorkloadUpdateDocument Document => JsonSerializer.Deserialize(json, WorkloadUpdateMetadata.Default.WorkloadUpdateDocument)!;
    internal string Fingerprint => ResourcesReadyCheckpointCodec.Hash(json);
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(WorkloadUpdateDocument))]
internal partial class WorkloadUpdateMetadata : JsonSerializerContext;
