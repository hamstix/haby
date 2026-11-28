using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Kubernetes.Contracts;

namespace Haby.Kubernetes;

internal sealed record DeploymentImageUpdateDocument(int Version, string Classification, string PreviousImage,
    string TargetImage, string Uid, string PreviousIntent, string PreviousReceipt);

internal static class DeploymentImageUpdate
{
    internal const string Format = "haby.kubernetes.deployment-image-update/v1";
    internal static DeploymentImageUpdateDocument Capture(InternalActionContext previous,
        DurablePlanningInputs target, string targetVersion, string intentJson, string receiptJson, int remaining = 256)
    {
        try
        {
            if (remaining < 1) throw new InvalidDataException("Confirmed update history exceeds bound.");
            if (previous.Subject is not ProviderInput.Workload workload || !WorkloadUpdateInputs.Same(previous.Inputs, target))
                throw new WorkloadUpdateRefusedException(WorkloadUpdateRefusal.InputChanged);
            var old = InternalDeploymentAction.Resolve(previous);
            var next = InternalDeploymentAction.Resolve(target, targetVersion, workload);
            DeploymentIntents.ValidatePreparation(next);
            var intent = DeploymentIntents.Decode(intentJson);
            var receipt = DeploymentIntents.DecodeReceipt(receiptJson);
            // Reconstruct the accepted old managed projection with its original bootstrap.
            // No target revision IDs or generation-specific runtime authority are invented.
            var previousReceipt = previous.Update is null ? null : InternalDeploymentAction.UpdateReceipt(previous, remaining - 1);
            var expected = DeploymentIntents.Prepare(old, intent.Owner, intent.ManagedWorkload,
                intent.TargetGeneration, intent.Operation, previousReceipt, runtime: receipt.Projection.Runtime);
            if (intent.Owner.ToString() != previous.Key.DeploymentId.ToString() || intent.TargetGeneration != previous.Key.Generation.Value ||
                intent.Operation != previous.Marker || intent.ManagedWorkload != previous.ManagedIdentity ||
                DeploymentIntents.Encode(expected) != intentJson || receipt.Binding != intent.Binding ||
                receipt.Namespace != intent.Namespace || receipt.Name != intent.Name || receipt.Owner != intent.Owner ||
                receipt.ManagedWorkload != intent.ManagedWorkload || receipt.TargetGeneration != intent.TargetGeneration ||
                receipt.Operation != intent.Operation || DeploymentIntents.Encode(intent with { Target = receipt.Projection }) != intentJson)
                throw new InvalidDataException("Original Deployment proof mismatch.");
            // Exact input comparison above also preserves all nonimage authored/default
            // fields and exact Placement identity, including unchanged effective images.
            return new(1, old.ImageReference == next.ImageReference ? "UnchangedImage" : "ImageChanged",
                old.ImageReference, next.ImageReference, receipt.Uid, intentJson, receiptJson);
        }
        catch (Exception exception) when (exception is WorkloadContractException or JsonException) { throw new InvalidDataException("Deployment update shape or proof refused."); }
    }
    internal static DurableActionPayload Payload(DeploymentImageUpdateDocument document) =>
        new(Format, JsonSerializer.Serialize(document, DeploymentUpdateMetadata.Default.DeploymentImageUpdateDocument));
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(DeploymentImageUpdateDocument))]
internal partial class DeploymentUpdateMetadata : JsonSerializerContext;
