using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Kubernetes.Contracts;
using k8s;
using k8s.Models;
using static Haby.Kubernetes.DeploymentIntents;

namespace Haby.Kubernetes;

internal sealed record RecipientSetIdentity(string Uid, string Name, string OwnerUid, bool Target);
internal sealed record RecipientPodIdentity(string Uid, string ReplicaSetUid, bool Target, bool Terminating);
internal sealed record RecipientSnapshot(int Version, string ReceiptHash, string Uid, long Generation,
    string ReplicaSetVersion, string PodVersion, int ReplicaSets, int Pods, int OldPods,
    RecipientObservationOptions Bounds, RecipientSetIdentity[] OwnedSets, RecipientPodIdentity[] OwnedPods);

/// <summary>Controller UID chains, including terminating Pods. Labels only narrow no authority.</summary>
internal static class DeploymentRecipients
{
    internal const string Contract = "haby.kubernetes.deployment-recipients/v1";
    private static string Hash(string receipt) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(receipt)));
    internal static RecipientObservation Evaluate(DeploymentIntent intent, DeploymentReceipt receipt,
        V1ReplicaSetList sets, V1PodList pods, RecipientObservationOptions options)
    {
        options.Validate();
        try
        {
            _ = DeploymentReadiness.Budget(Encode(intent), Encode(receipt));
            Need(intent.ExpectedUid == receipt.Uid, "update-uid");
            Need(sets.Metadata is not null && pods.Metadata is not null &&
                !string.IsNullOrEmpty(sets.Metadata.ResourceVersion) && !string.IsNullOrEmpty(pods.Metadata.ResourceVersion) &&
                string.IsNullOrEmpty(sets.Metadata.ContinueProperty) && string.IsNullOrEmpty(pods.Metadata.ContinueProperty) &&
                sets.Metadata.RemainingItemCount is null or 0 && pods.Metadata.RemainingItemCount is null or 0 &&
                sets.Items is not null && pods.Items is not null && sets.Items.Count <= options.MaximumObjects && pods.Items.Count <= options.MaximumObjects,
                "incomplete-list");
            var known = new Dictionary<string, (string Name, bool Owned, bool Target)>(StringComparer.Ordinal);
            var setEvidence = new List<RecipientSetIdentity>(); var podEvidence = new List<RecipientPodIdentity>();
            foreach (var set in sets.Items!)
            {
                Need(set.Metadata is { } && set.Metadata.NamespaceProperty == intent.Namespace &&
                    !string.IsNullOrEmpty(set.Metadata.Uid) && !string.IsNullOrEmpty(set.Metadata.Name), "set-identity");
                var owner = Controller(set.Metadata!.OwnerReferences);
                var owned = owner is { ApiVersion: "apps/v1", Kind: "Deployment" } && owner.Uid == receipt.Uid && owner.Name == receipt.Name;
                Need(owner is null || owner.Uid != receipt.Uid || owned, "set-owner");
                Need(owner is not { ApiVersion: "apps/v1", Kind: "Deployment" } || owner.Name != receipt.Name || owned, "replacement-controller");
                var target = owned && Target(set.Spec?.Template, intent);
                if (owned) setEvidence.Add(new(set.Metadata.Uid, set.Metadata.Name, owner!.Uid, target));
                Need(known.TryAdd(set.Metadata.Uid, (set.Metadata.Name, owned, target)), "duplicate-set");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal); var old = 0;
            foreach (var pod in pods.Items!)
            {
                Need(pod.Metadata is { } && pod.Metadata.NamespaceProperty == intent.Namespace &&
                    !string.IsNullOrEmpty(pod.Metadata.Uid) && seen.Add(pod.Metadata.Uid), "pod-identity");
                var owner = Controller(pod.Metadata!.OwnerReferences);
                // An orphan may be an old recipient whose ReplicaSet disappeared between snapshots.
                Need(owner is not null && owner.ApiVersion == "apps/v1" && owner.Kind == "ReplicaSet" &&
                    known.TryGetValue(owner.Uid, out var parent) && parent.Name == owner.Name, "pod-owner-unresolved");
                var set = known[owner!.Uid];
                if (set.Owned)
                {
                    var target = set.Target && Target(new V1PodTemplateSpec { Metadata = pod.Metadata, Spec = pod.Spec }, intent);
                    if (!target) old++;
                    podEvidence.Add(new(pod.Metadata.Uid, owner.Uid, target, pod.Metadata.DeletionTimestamp is not null));
                }
            }
            var evidence = new RecipientSnapshot(1, Hash(Encode(receipt)), receipt.Uid, receipt.KubernetesGeneration,
                sets.Metadata!.ResourceVersion, pods.Metadata!.ResourceVersion, sets.Items!.Count, pods.Items!.Count, old, options,
                setEvidence.OrderBy(x => x.Uid, StringComparer.Ordinal).ToArray(), podEvidence.OrderBy(x => x.Uid, StringComparer.Ordinal).ToArray());
            var json = JsonSerializer.Serialize(evidence, RecipientMetadata.Default.RecipientSnapshot);
            Need(Encoding.UTF8.GetByteCount(json) <= options.MaximumResponseBytes, "evidence-bound");
            return new(old == 0, old == 0 ? "old-recipients-absent" : "old-recipients-present",
                new(Contract, json));
        }
        catch (Exception error) when (error is WorkloadContractException or InvalidOperationException or ArgumentException or NullReferenceException)
        { return new(false, "recipient-evidence-incomplete"); }
    }
    private static V1OwnerReference? Controller(IList<V1OwnerReference>? owners)
    {
        Need(owners is not null && owners.Count <= 64, "owner-list");
        var controllers = owners!.Where(x => x.Controller == true).ToArray();
        Need(controllers.Length <= 1 && controllers.All(x => !string.IsNullOrEmpty(x.Uid) && !string.IsNullOrEmpty(x.Name)), "controller");
        return controllers.SingleOrDefault();
    }
    private static bool Target(V1PodTemplateSpec? template, DeploymentIntent intent)
    {
        if (template?.Spec is null) return false;
        try
        {
            var projection = DeploymentObjects.Read(DeploymentObjects.Create(intent));
            projection["spec"]!["template"] = JsonNode.Parse(KubernetesJson.Serialize(template));
            DeploymentObjects.Matches(projection, intent.Target); return true;
        }
        catch (Exception error) when (error is WorkloadContractException or InvalidOperationException or ArgumentException) { return false; }
    }
    internal static bool Validate(string intent, string receipt, RecipientObservation observation)
    {
        try
        {
            _ = DeploymentReadiness.Budget(intent, receipt);
            if (observation.Evidence is not { Format: Contract } payload || payload.Json.Length > 16777216) return false;
            var saved = DecodeReceipt(receipt);
            var evidence = JsonSerializer.Deserialize(payload.Json, RecipientMetadata.Default.RecipientSnapshot);
            if (evidence is null) return false;
            evidence.Bounds.Validate();
            return Encoding.UTF8.GetByteCount(payload.Json) <= evidence.Bounds.MaximumResponseBytes &&
                evidence.ReplicaSets <= evidence.Bounds.MaximumObjects && evidence.Pods <= evidence.Bounds.MaximumObjects &&
                evidence.OwnedSets.Length <= evidence.ReplicaSets && evidence.OwnedPods.Length <= evidence.Pods &&
                evidence.OwnedSets.Select(x => x.Uid).Distinct(StringComparer.Ordinal).Count() == evidence.OwnedSets.Length &&
                evidence.OwnedPods.Select(x => x.Uid).Distinct(StringComparer.Ordinal).Count() == evidence.OwnedPods.Length &&
                evidence.OwnedSets.All(x => x.OwnerUid == saved.Uid) &&
                evidence.OwnedPods.All(x => evidence.OwnedSets.Any(s => s.Uid == x.ReplicaSetUid && (!x.Target || s.Target))) &&
                evidence.OwnedPods.Count(x => !x.Target) == evidence.OldPods &&
                evidence is { Version: 1, ReplicaSets: >= 0, Pods: >= 0, OldPods: >= 0 } &&
                evidence.OldPods <= evidence.Pods && evidence.ReceiptHash == Hash(receipt) && evidence.Uid == saved.Uid &&
                Decode(intent).ExpectedUid == saved.Uid && evidence.Generation == saved.KubernetesGeneration && !string.IsNullOrEmpty(evidence.ReplicaSetVersion) && !string.IsNullOrEmpty(evidence.PodVersion) &&
                observation.Absent == (evidence.OldPods == 0) && observation.Code == (observation.Absent ? "old-recipients-absent" : "old-recipients-present");
        }
        catch (Exception error) when (error is JsonException or WorkloadContractException or InvalidOperationException or ArgumentException) { return false; }
    }
}

public sealed partial class DeploymentKernel
{
    internal async Task<RecipientObservation> RecipientsAsync(DeploymentIntent intent, DeploymentReceipt receipt,
        RecipientObservationOptions options, CancellationToken token)
    {
        options.Validate();
        try
        {
            _ = DeploymentReadiness.Budget(Encode(intent), Encode(receipt)); Need(intent.Binding == _binding, "provider-binding");
            using var lease = await _connectRecipients(options, token);
            await RequireRuntimeAccount(lease.Client, intent, token);
            var before = await Read(lease.Client, intent, token);
            if (before is null || DeploymentReadiness.Evaluate(before, intent, receipt).Outcome != RolloutOutcome.Ready)
                return new(false, "target-not-ready");
            using var sets = await lease.Client.AppsV1.ListNamespacedReplicaSetWithHttpMessagesAsync(intent.Namespace,
                limit: options.MaximumObjects, cancellationToken: token);
            using var pods = await lease.Client.CoreV1.ListNamespacedPodWithHttpMessagesAsync(intent.Namespace,
                limit: options.MaximumObjects, cancellationToken: token);
            var result = DeploymentRecipients.Evaluate(intent, receipt, sets.Body, pods.Body, options);
            var after = await Read(lease.Client, intent, token);
            if (after is null || DeploymentReadiness.Evaluate(after, intent, receipt).Outcome != RolloutOutcome.Ready ||
                before["metadata"]?["generation"]?.ToJsonString() != after["metadata"]?["generation"]?.ToJsonString())
                return new(false, "target-changed");
            token.ThrowIfCancellationRequested(); return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new(false, "recipient-observation-unavailable"); }
    }
}

internal sealed class BoundedRecipientResponse(int maximumBytes) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        try
        {
            if (response.Content.Headers.ContentLength > maximumBytes) throw new IOException("Recipient response exceeds bound.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream(); var buffer = new byte[Math.Min(8192, maximumBytes + 1)];
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes + 1 - (int)output.Length)), cancellationToken);
                if (read == 0) break;
                output.Write(buffer, 0, read);
                if (output.Length > maximumBytes) throw new IOException("Recipient response exceeds bound.");
            }
            var content = new ByteArrayContent(output.ToArray());
            foreach (var header in response.Content.Headers) content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content.Dispose(); response.Content = content; return response;
        }
        catch { response.Dispose(); throw; }
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(RecipientSnapshot))]
internal partial class RecipientMetadata : JsonSerializerContext;
