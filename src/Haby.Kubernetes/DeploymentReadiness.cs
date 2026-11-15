using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Kubernetes.Contracts;
using k8s.Autorest;
using static Haby.Kubernetes.DeploymentIntents;

namespace Haby.Kubernetes;

internal sealed record DeploymentStatusEvidence(int Format, string ReceiptHash, string Uid, long Generation,
    long ObservedGeneration, int Desired, int Total, int Updated, int Ready, int Available, int Unavailable,
    int? Terminating, bool Paused, bool Deleting, int ConditionFlags);

/// <summary>apps/v1 completion criteria. This never proves recipient retirement or application correctness.</summary>
internal static class DeploymentReadiness
{
    internal const string Contract = "haby.kubernetes.deployment-readiness/v1";
    private static readonly string[] Codes = ["ready", "rollout-pending", "progress-deadline", "status-invalid",
        "owned-state-conflict", "object-absent", "api-access-denied", "observation-unavailable", "api-rejected", "input-invalid"];
    private static string Hash(DeploymentReceipt receipt) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Encode(receipt))));
    private static void Authority(DeploymentIntent intent, DeploymentReceipt receipt)
    {
        DeploymentIntents.Validate(intent); DeploymentIntents.Validate(receipt);
        Need(receipt.Binding == intent.Binding && receipt.Namespace == intent.Namespace && receipt.Name == intent.Name &&
            receipt.Owner == intent.Owner && receipt.ManagedWorkload == intent.ManagedWorkload && receipt.Operation == intent.Operation &&
            receipt.TargetGeneration == intent.TargetGeneration && Encode(intent with { Target = receipt.Projection }) == Encode(intent), "submission-authority");
    }
    internal static int Budget(string intent, string receipt)
    {
        var target = Decode(intent); Authority(target, DecodeReceipt(receipt)); return target.Target.ProgressDeadlineSeconds;
    }
    internal static bool Validate(string intent, string receipt, RolloutObservation observation)
    {
        try
        {
            var target = Decode(intent); var saved = DecodeReceipt(receipt); Authority(target, saved);
            if (!Enum.IsDefined(observation.Outcome) || !Codes.Contains(observation.Code) ||
                observation.Temporary && observation.Outcome != RolloutOutcome.Unavailable) return false;
            if (observation.Evidence is null) return observation.Contract is null && observation.Outcome != RolloutOutcome.Ready;
            if (observation.Contract != Contract || Encoding.UTF8.GetByteCount(observation.Evidence) > 16384) return false;
            var evidence = JsonSerializer.Deserialize(observation.Evidence, ReadinessMetadata.Default.DeploymentStatusEvidence);
            return evidence is { Format: 1 } && evidence.ReceiptHash == Hash(saved) && evidence.Uid == saved.Uid &&
                evidence.Desired == saved.Projection.Replicas && evidence.Generation >= saved.KubernetesGeneration &&
                ValidCounts(evidence) && evidence.ConditionFlags is >= 0 and <= 15 &&
                Result(evidence).Outcome == observation.Outcome && Result(evidence).Code == observation.Code && !observation.Temporary;
        }
        catch (Exception error) when (error is WorkloadContractException or JsonException or InvalidOperationException or ArgumentException) { return false; }
    }
    private static bool ValidCounts(DeploymentStatusEvidence e) => e.Generation > 0 && e.ObservedGeneration >= 0 && e.ObservedGeneration <= e.Generation &&
        e.Total >= 0 && e.Updated >= 0 && e.Ready >= 0 && e.Available >= 0 && e.Unavailable >= 0 &&
        e.Updated <= e.Total && e.Ready <= e.Total && e.Available <= e.Ready && e.Unavailable <= e.Desired && e.Terminating is not < 0;
    private static RolloutObservation Result(DeploymentStatusEvidence e)
    {
        var ready = !e.Paused && !e.Deleting && e.ObservedGeneration >= e.Generation &&
            e.Total == e.Desired && e.Updated == e.Desired && e.Ready == e.Desired && e.Available == e.Desired &&
            e.Unavailable == 0 && e.ConditionFlags == 0;
        return new(ready ? RolloutOutcome.Ready : RolloutOutcome.Pending,
            ready ? "ready" : (e.ConditionFlags & 8) != 0 ? "progress-deadline" : "rollout-pending",
            Contract: Contract, Evidence: JsonSerializer.Serialize(e, ReadinessMetadata.Default.DeploymentStatusEvidence));
    }
    internal static RolloutObservation Evaluate(JsonObject obj, DeploymentIntent intent, DeploymentReceipt receipt)
    {
        Authority(intent, receipt);
        try
        {
            OwnershipAndProjection(obj, intent, receipt);
        }
        catch (Exception error) when (error is WorkloadContractException or InvalidOperationException or FormatException or OverflowException)
        { return new(RolloutOutcome.Conflict, "owned-state-conflict"); }
        try
        {
            static long Integer(JsonNode? node, long missing = 0) => node is null ? missing :
                long.TryParse(node.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException();
            var generation = Integer(obj["metadata"]?["generation"]);
            var status = obj["status"] as JsonObject;
            if (obj["status"] is not null && status is null) throw new FormatException();
            int Count(string name) => checked((int)Integer(status?[name]));
            var paused = obj["spec"]?["paused"]?.GetValue<bool>() ?? false;
            var flags = 0;
            if (status?["conditions"] is { } raw)
            {
                if (raw is not JsonArray conditions || conditions.Count > 64) throw new FormatException();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var condition in conditions)
                {
                    var type = condition?["type"]?.GetValue<string>();
                    var value = condition?["status"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(type) || type.Length > 128 || !seen.Add(type) || value is not ("True" or "False" or "Unknown")) throw new FormatException();
                    if (type == "Available" && value != "True") flags |= 1;
                    if (type == "Progressing" && value != "True") flags |= 2;
                    if (type == "ReplicaFailure" && value != "False") flags |= 4;
                    if (type == "Progressing" && value == "False" && condition?["reason"]?.GetValue<string>() == "ProgressDeadlineExceeded") flags |= 8;
                }
            }
            var evidence = new DeploymentStatusEvidence(1, Hash(receipt), receipt.Uid, generation,
                Integer(status?["observedGeneration"]), receipt.Projection.Replicas, Count("replicas"), Count("updatedReplicas"),
                Count("readyReplicas"), Count("availableReplicas"), Count("unavailableReplicas"),
                status?["terminatingReplicas"] is null ? null : Count("terminatingReplicas"), paused,
                obj["metadata"]?["deletionTimestamp"] is not null, flags);
            if (generation < receipt.KubernetesGeneration || !ValidCounts(evidence) ||
                status?["unavailableReplicas"] is not null && evidence.Unavailable != Math.Max(evidence.Desired - evidence.Available, 0)) throw new FormatException();
            return Result(evidence);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { return new(RolloutOutcome.Pending, "status-invalid"); }
    }
    private static void OwnershipAndProjection(JsonObject obj, DeploymentIntent intent, DeploymentReceipt receipt)
    {
        DeploymentObjects.Ownership(obj, intent); DeploymentObjects.Matches(obj, receipt.Projection);
        Need(DeploymentObjects.HasMarkers(obj, intent) && DeploymentObjects.String(obj, "metadata", "uid") == receipt.Uid, "saved-object");
    }
}

public sealed partial class DeploymentKernel
{
    internal async Task<RolloutObservation> ReadinessAsync(DeploymentIntent intent, DeploymentReceipt receipt, CancellationToken token)
    {
        try
        {
            _ = DeploymentReadiness.Budget(Encode(intent), Encode(receipt)); Need(intent.Binding == _binding, "provider-binding");
        }
        catch (WorkloadContractException) { return new(RolloutOutcome.Refused, "input-invalid"); }
        try
        {
            token.ThrowIfCancellationRequested();
            using var lease = await _connect(token);
            var current = await Read(lease.Client, intent, token);
            return current is null ? new(RolloutOutcome.Conflict, "object-absent") : DeploymentReadiness.Evaluate(current, intent, receipt);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (JsonException) { return new(RolloutOutcome.Pending, "status-invalid"); }
        catch (WorkloadContractException) { return new(RolloutOutcome.Unavailable, "api-access-denied"); }
        catch (HttpOperationException error)
        {
            var status = (int?)error.Response?.StatusCode;
            return new(RolloutOutcome.Unavailable, status is 401 or 403 ? "api-access-denied" : status is 429 or >= 500 ? "observation-unavailable" : "api-rejected",
                status is null or 429 or >= 500);
        }
        catch (Exception) { return new(RolloutOutcome.Unavailable, "observation-unavailable", true); }
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(DeploymentStatusEvidence))]
internal partial class ReadinessMetadata : JsonSerializerContext;
