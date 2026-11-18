using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Configuration.Contracts;
using Haby.Kubernetes;
using Haby.Persistence.PostgreSql;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

internal static class ConfiguredUpdateRetirement
{
    private const string Hold = "qualification.haby.dev/old-recipient";
    internal static async Task<V1Pod> HoldAsync(Kubernetes admin, DeploymentReceipt prior, V1PodList pods, Action dispatching, CancellationToken token)
    {
        Console.WriteLine("Q243 configured update-retirement-hold start");
        var pod = pods.Items.Single(x => x.Metadata.DeletionTimestamp is null && x.Spec.Containers.Any(c =>
            c.Name == prior.Projection.Container && c.Image == prior.Projection.Image));
        var owner = pod.Metadata.OwnerReferences.Single(x => x.Controller == true);
        var set = await admin.AppsV1.ReadNamespacedReplicaSetAsync(owner.Name, prior.Namespace, cancellationToken: token);
        var parent = set.Metadata.OwnerReferences.Single(x => x.Controller == true);
        if (owner.ApiVersion != "apps/v1" || owner.Kind != "ReplicaSet" || set.Metadata.Uid != owner.Uid ||
            parent.Kind != "Deployment" || parent.ApiVersion != "apps/v1" || parent.Uid != prior.Uid || parent.Name != prior.Name ||
            pod.Metadata.Finalizers is { Count: > 0 }) throw new InvalidOperationException("Held recipient ownership refused.");
        var patch = new JsonArray(
            new JsonObject { ["op"] = "test", ["path"] = "/metadata/uid", ["value"] = pod.Metadata.Uid },
            new JsonObject { ["op"] = "test", ["path"] = "/metadata/resourceVersion", ["value"] = pod.Metadata.ResourceVersion },
            new JsonObject { ["op"] = "add", ["path"] = "/metadata/finalizers", ["value"] = new JsonArray(Hold) });
        dispatching();
        await admin.CoreV1.PatchNamespacedPodAsync(new(patch.ToJsonString(), V1Patch.PatchType.JsonPatch), pod.Metadata.Name, prior.Namespace, cancellationToken: token);
        Console.WriteLine("Q243 configured update-retirement-hold completed"); return pod;
    }
    internal static async Task ReleaseAsync(Kubernetes admin, string ns, V1Pod held, CancellationToken token)
    {
        Console.WriteLine(ConfiguredRetirementDiagnostics.Finalizer(null));
        V1Pod pod;
        try { pod = await admin.CoreV1.ReadNamespacedPodAsync(held.Metadata.Name, ns, cancellationToken: token); }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.NotFound) { Console.WriteLine(ConfiguredRetirementDiagnostics.Finalizer(false)); return; }
        if (pod.Metadata.Uid != held.Metadata.Uid) throw new InvalidOperationException("Held recipient replacement refused.");
        var index = pod.Metadata.Finalizers?.IndexOf(Hold) ?? -1;
        Console.WriteLine(ConfiguredRetirementDiagnostics.Finalizer(index >= 0));
        if (index < 0) return;
        var patch = new JsonArray(
            new JsonObject { ["op"] = "test", ["path"] = "/metadata/uid", ["value"] = held.Metadata.Uid },
            new JsonObject { ["op"] = "test", ["path"] = "/metadata/resourceVersion", ["value"] = pod.Metadata.ResourceVersion },
            new JsonObject { ["op"] = "test", ["path"] = "/metadata/finalizers/" + index, ["value"] = Hold },
            new JsonObject { ["op"] = "remove", ["path"] = "/metadata/finalizers/" + index });
        await admin.CoreV1.PatchNamespacedPodAsync(new(patch.ToJsonString(), V1Patch.PatchType.JsonPatch), held.Metadata.Name, ns, cancellationToken: token);
    }
    internal static async Task CompleteAsync(Kubernetes admin, IDbContextFactory<ManifestCatalogDbContext> contexts,
        Func<InternalDurableActions> actions, DurableChangeSetCommit accepted, DeploymentReceipt prior, RuntimeBootstrap target,
        V1Pod held, ConcurrentQueue<Guid> requests,
        Func<CancellationToken, Action<DeploymentRecipientDiagnostic>, Task<RecipientObservation>> recipients,
        Func<RecipientObservation, bool> validateRecipients, Func<CancellationToken, Task> drain, CancellationToken token)
    {
        Console.WriteLine("Q243 configured update-retirement-ready start");
        var options = new RolloutOptions(MaximumCalls: 128, MaximumRounds: 128, PollMilliseconds: 250, RequestMilliseconds: 60000);
        using var completionBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
        completionBudget.CancelAfter(TimeSpan.FromMilliseconds(options.RequestMilliseconds));
        var completionToken = completionBudget.Token;
        var waiting = await actions().WaitUpdateAsync(accepted, "configured-update-held-recipient", "qualification", options, new(), completionToken);
        var old = await admin.CoreV1.ReadNamespacedPodAsync(held.Metadata.Name, prior.Namespace, cancellationToken: completionToken);
        Check(waiting.Ready && !waiting.Applied && waiting.CompletionReason == "old-recipients-present" &&
            old.Metadata.Uid == held.Metadata.Uid && old.Metadata.DeletionTimestamp is not null,
            "U09-fresh-target-ready-terminating-old-UID-chain-blocks-retirement");
        await using (var db = await contexts.CreateDbContextAsync(completionToken))
        {
            var id = Guid.Parse(accepted.DeploymentId.ToString());
            Check((await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.Id == id, completionToken)).AppliedGeneration == accepted.Generation.Value - 1 &&
                !await db.GenerationStageCheckpoints.AnyAsync(x => x.DeploymentId == id && x.Generation == accepted.Generation.Value && x.Stage == "RetirementStarted", completionToken) &&
                await db.RuntimeConfigurationPermissions.CountAsync(x => x.BindingId == Guid.Parse(target.BindingId) && x.Status == "Published", completionToken) == 4,
                "U10-old-permissions-retained-no-retirement-start-previous-Applied");
        }
        Console.WriteLine("Q243 configured update-retirement-release start");
        await ReleaseAsync(admin, prior.Namespace, held, completionToken);
        var last = new DeploymentRecipientDiagnostic(-1, -1, -1, RecipientProjectionFailure.Unknown, RecipientProjectionFailure.Unknown);
        await ConfiguredRecipientWait.UntilAbsentAsync(ct => recipients(ct, value => last = value), validateRecipients,
            options.MaximumRounds, options.PollMilliseconds, () =>
            {
                Console.WriteLine(ConfiguredRetirementDiagnostics.Recipients(last));
                Console.WriteLine("Q243 configured retirement RecipientWaitExpired");
            }, completionToken);
        Console.WriteLine("Q243 configured update-retirement-complete start");
        var complete = await ConfiguredRetirementDiagnostics.WaitAsync(() => actions().WaitUpdateAsync(accepted,
            "configured-update-finished-recipients", "qualification", options, new(), completionToken), accepted.Generation.Value);
        Require("U11Ready", complete.Ready);
        Require("U11Applied", complete.Applied);
        Require("U11Generation", complete.AppliedGeneration == accepted.Generation.Value);
        Check(true, "U11-exact-obsolete-retirement-before-update-Applied");
        var binding = Guid.Parse(target.BindingId);
        foreach (var revision in RuntimeBootstrapCodec.Decode(prior.Projection.Runtime!.Bootstrap).Documents.Values)
            if (await RuntimePublicationAuthorization.AuthorizeAsync(contexts, binding, Guid.Parse(revision), token) is not null)
                throw new InvalidOperationException("Obsolete runtime permission still authorizes.");
        foreach (var revision in target.Documents.Values)
            if (await RuntimePublicationAuthorization.AuthorizeAsync(contexts, binding, Guid.Parse(revision), token) is null)
                throw new InvalidOperationException("Target runtime permission unavailable.");
        requests.Clear();
        var targetPods = await admin.CoreV1.ListNamespacedPodAsync(prior.Namespace, labelSelector: DeploymentIntents.ManagedKey + "=" + prior.ManagedWorkload.ToString("N"), cancellationToken: token);
        var restart = targetPods.Items.Single(x => x.Metadata.DeletionTimestamp is null && x.Spec.Containers.Any(c => c.Name == prior.Projection.Container &&
            c.Env?.Any(e => e.Name == RuntimeBootstrap.Variable && e.Value == RuntimeBootstrapCodec.Encode(target)) == true));
        await admin.CoreV1.DeleteNamespacedPodAsync(restart.Metadata.Name, prior.Namespace,
            new V1DeleteOptions { Preconditions = new() { Uid = restart.Metadata.Uid } }, cancellationToken: token);
        using (var delivery = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            delivery.CancelAfter(TimeSpan.FromSeconds(35));
            while (!target.Documents.Values.Select(Guid.Parse).All(requests.Contains)) await Task.Delay(100, delivery.Token);
            Check(target.Documents.Values.Select(Guid.Parse).All(requests.Contains), "U12-old-rights-denied-target-exact-delivery-stable-binding");
            try { await drain(delivery.Token); }
            catch (Exception error) { throw new ConfiguredFixtureDrain.Failure(error); }
        }
        Console.WriteLine("Q243 configured update-retirement-complete completed");
    }
    private static void Require(string condition, bool value)
    {
        Console.WriteLine(ConfiguredRetirementDiagnostics.Condition(condition, value));
        if (!value) throw new InvalidOperationException("Configured update retirement condition failed.");
    }
    private static void Check(bool valid, string name)
    {
        if (!valid) throw new InvalidOperationException("Configured update retirement condition failed.");
        Console.WriteLine("PASS configured-" + name);
    }
}
