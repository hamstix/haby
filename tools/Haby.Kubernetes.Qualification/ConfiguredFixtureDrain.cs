using System.Text.Json.Nodes;
using Haby.Kubernetes;
using k8s;
using k8s.Models;

// Qualification teardown only: this never constitutes a product UPDATE or completion proof.
internal static class ConfiguredFixtureDrain
{
    internal sealed class OwnershipRefused() : InvalidOperationException("Fixture drain ownership refused.");
    internal sealed class Failure(Exception error) : Exception("Fixture drain failed.", error);
    private const int Bound = 64;
    internal static async Task RunAsync(Kubernetes admin, V1Namespace owned, DeploymentReceipt[] expected,
        CancellationToken token, Func<TimeSpan, CancellationToken, Task>? pause = null)
    {
        Console.WriteLine("Q243 configured retirement DrainStart");
        var remaining = -1;
        try
        {
            Need(expected.Length is > 0 and <= 16 && expected.All(x => x.Namespace == owned.Metadata.Name) &&
                expected.Select(x => x.Uid).Distinct(StringComparer.Ordinal).Count() == expected.Length &&
                expected.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() == expected.Length);
            // Recheck the complete namespace inventory and exact object immediately before EACH mutation.
            foreach (var receipt in expected)
            {
                _ = await VerifyOwnedAsync(admin, owned, expected, token);
                var current = await admin.AppsV1.ReadNamespacedDeploymentAsync(receipt.Name, receipt.Namespace, cancellationToken: token);
                Verify(current, receipt);
                token.ThrowIfCancellationRequested();
                var patch = new JsonArray(
                    new JsonObject { ["op"] = "test", ["path"] = "/metadata/uid", ["value"] = receipt.Uid },
                    new JsonObject { ["op"] = "test", ["path"] = "/metadata/resourceVersion", ["value"] = current.Metadata.ResourceVersion },
                    new JsonObject { ["op"] = "add", ["path"] = "/spec/replicas", ["value"] = 0 });
                await admin.AppsV1.PatchNamespacedDeploymentAsync(new(patch.ToJsonString(), V1Patch.PatchType.JsonPatch),
                    receipt.Name, receipt.Namespace, cancellationToken: token);
            }
            // Consume the caller's existing absolute restart-delivery token; never create or renew a deadline.
            for (var round = 0; round < 141; round++)
            {
                token.ThrowIfCancellationRequested();
                remaining = await VerifyOwnedAsync(admin, owned, expected, token, requireQuiesced: true);
                token.ThrowIfCancellationRequested();
                if (remaining == 0) { Console.WriteLine("Q243 configured retirement DrainComplete"); return; }
                if (round < 140) await (pause?.Invoke(TimeSpan.FromMilliseconds(250), token) ?? Task.Delay(250, token));
            }
            throw new TimeoutException("Fixture drain round bound exhausted.");
        }
        catch (OwnershipRefused) { Console.WriteLine("Q243 configured retirement DrainOwnershipRefused"); throw; }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            Console.WriteLine("Q243 configured retirement DrainExhausted");
            Console.WriteLine("Q243 configured retirement DrainRemaining " + (remaining < 0 ? "unknown" : remaining.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            throw;
        }
    }
    internal static async Task<int> VerifyOwnedAsync(Kubernetes admin, V1Namespace owned, DeploymentReceipt[] expected,
        CancellationToken token, bool requireQuiesced = false)
    {
        token.ThrowIfCancellationRequested();
        var ns = await admin.CoreV1.ReadNamespaceAsync(owned.Metadata.Name, cancellationToken: token);
        Need(ns.Metadata.Uid == owned.Metadata.Uid && ns.Metadata.DeletionTimestamp is null &&
            Label(ns.Metadata.Labels, "haby.dev/qualification") == owned.Metadata.Name);
        var deployments = await admin.AppsV1.ListNamespacedDeploymentAsync(owned.Metadata.Name, limit: Bound, cancellationToken: token);
        Complete(deployments.Metadata, deployments.Items.Count);
        Need(deployments.Items.Count == expected.Length);
        foreach (var current in deployments.Items)
        {
            var receipt = expected.SingleOrDefault(x => x.Name == current.Metadata.Name);
            Need(receipt is not null); Verify(current, receipt!);
            if (requireQuiesced) Need(current.Spec.Replicas == 0);
        }
        Need(deployments.Items.Select(x => x.Metadata.Uid).Distinct(StringComparer.Ordinal).Count() == expected.Length);
        var sets = await admin.AppsV1.ListNamespacedReplicaSetAsync(owned.Metadata.Name, limit: Bound, cancellationToken: token);
        var pods = await admin.CoreV1.ListNamespacedPodAsync(owned.Metadata.Name, limit: Bound, cancellationToken: token);
        return Remaining(owned.Metadata.Name, expected, sets, pods);
    }
    private static void Verify(V1Deployment value, DeploymentReceipt receipt)
    {
        Need(value.Metadata is not null && value.Spec?.Template?.Metadata is not null && value.Spec.Selector is not null);
        Need(value.Metadata.NamespaceProperty == receipt.Namespace && value.Metadata.Name == receipt.Name && value.Metadata.Uid == receipt.Uid &&
            !string.IsNullOrEmpty(value.Metadata.ResourceVersion) && value.Metadata.DeletionTimestamp is null);
        foreach (var labels in new[] { value.Metadata.Labels, value.Spec.Template.Metadata.Labels, value.Spec.Selector.MatchLabels })
            Need(labels is { Count: >= 2 } && Label(labels, DeploymentIntents.OwnerKey) == receipt.Owner.ToString("N") &&
                Label(labels, DeploymentIntents.ManagedKey) == receipt.ManagedWorkload.ToString("N"));
    }
    internal static int Remaining(string ns, DeploymentReceipt[] expected, V1ReplicaSetList sets, V1PodList pods)
    {
        Complete(sets.Metadata, sets.Items.Count); Complete(pods.Metadata, pods.Items.Count);
        Need(sets.Items.Select(x => x.Metadata.Uid).Distinct(StringComparer.Ordinal).Count() == sets.Items.Count &&
            sets.Items.Select(x => x.Metadata.Name).Distinct(StringComparer.Ordinal).Count() == sets.Items.Count &&
            pods.Items.Select(x => x.Metadata.Uid).Distinct(StringComparer.Ordinal).Count() == pods.Items.Count &&
            pods.Items.Select(x => x.Metadata.Name).Distinct(StringComparer.Ordinal).Count() == pods.Items.Count);
        foreach (var set in sets.Items)
        {
            Identity(set.Metadata, ns);
            var owner = Controller(set.Metadata);
            Need(owner.ApiVersion == "apps/v1" && owner.Kind == "Deployment" && expected.Any(x => x.Name == owner.Name && x.Uid == owner.Uid));
        }
        foreach (var pod in pods.Items)
        {
            Identity(pod.Metadata, ns);
            var owner = Controller(pod.Metadata);
            Need(owner.ApiVersion == "apps/v1" && owner.Kind == "ReplicaSet" && sets.Items.Any(x => x.Metadata.Name == owner.Name && x.Metadata.Uid == owner.Uid));
        }
        return pods.Items.Count; // Includes every terminating, pre-restart and replacement Pod; no label filter.
    }
    private static V1OwnerReference Controller(V1ObjectMeta value)
    {
        Need(value.OwnerReferences is { Count: > 0 and <= 16 } && value.OwnerReferences.Count(x => x.Controller == true) == 1);
        return value.OwnerReferences.Single(x => x.Controller == true);
    }
    private static void Identity(V1ObjectMeta value, string ns) => Need(value.NamespaceProperty == ns && !string.IsNullOrEmpty(value.Name) && !string.IsNullOrEmpty(value.Uid));
    private static void Complete(V1ListMeta value, int count) => Need(count <= Bound && value is not null && !string.IsNullOrEmpty(value.ResourceVersion) &&
        string.IsNullOrEmpty(value.ContinueProperty) && value.RemainingItemCount is null or 0);
    private static string? Label(IDictionary<string, string>? labels, string key) => labels is not null && labels.TryGetValue(key, out var value) ? value : null;
    private static void Need([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value) { if (!value) throw new OwnershipRefused(); }
}
