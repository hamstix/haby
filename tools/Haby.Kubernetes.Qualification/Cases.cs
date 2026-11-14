using System.Diagnostics;
using k8s;
using k8s.Autorest;
using k8s.Models;

internal static class Cases
{
    internal static async Task Run(Kubernetes client, Uri endpoint, string directory, CancellationToken token)
    {
        var name = "haby179-" + Guid.NewGuid().ToString("N")[..12];
        var ns = await client.CoreV1.CreateNamespaceAsync(new V1Namespace
        {
            Metadata = new V1ObjectMeta { Name = name, Labels = new Dictionary<string, string> { ["haby.dev/qualification"] = name } },
        }, cancellationToken: token);
        try
        {
            var labels = new Dictionary<string, string> { ["app"] = name };
            var deployment = new V1Deployment
            {
                Metadata = new V1ObjectMeta { Name = "target", Annotations = new Dictionary<string, string> { ["haby.dev/operation"] = name, ["haby.dev/owner"] = name, ["unrelated"] = "preserve" } },
                Spec = new V1DeploymentSpec
                {
                    Replicas = 1,
                    Selector = new V1LabelSelector { MatchLabels = labels },
                    Template = new V1PodTemplateSpec
                    {
                        Metadata = new V1ObjectMeta { Labels = labels },
                        Spec = new V1PodSpec { Containers = new List<V1Container> { new() { Name = "injected", Image = "registry.k8s.io/pause:3.10" }, new() { Name = "app", Image = "registry.k8s.io/pause:3.10" } } },
                    },
                },
            };
            var created = await client.AppsV1.CreateNamespacedDeploymentAsync(deployment, name, cancellationToken: token);
            Check(!string.IsNullOrEmpty(created.Metadata.Uid) && !string.IsNullOrEmpty(created.Metadata.ResourceVersion), "A identity");
            await Refuses(() => client.AppsV1.CreateNamespacedDeploymentAsync(deployment, name, cancellationToken: token), "A occupied", 409);
            var read = await client.AppsV1.ReadNamespacedDeploymentAsync("target", name, cancellationToken: token);
            Check(read.Metadata.Uid == created.Metadata.Uid && read.Metadata.Generation == created.Metadata.Generation && read.Metadata.Annotations["haby.dev/operation"] == name && read.Metadata.Annotations["unrelated"] == "preserve" && read.Spec.Template.Spec.Containers.Single(c => c.Name == "app").Image == "registry.k8s.io/pause:3.10", "A unchanged identity and desired state");
            var index = read.Spec.Template.Spec.Containers.ToList().FindIndex(c => c.Name == "app");
            V1Patch Patch(string uid, string rv) => new($$"""
                [{"op":"test","path":"/metadata/uid","value":"{{uid}}"},{"op":"test","path":"/metadata/resourceVersion","value":"{{rv}}"},{"op":"test","path":"/spec/template/spec/containers/{{index}}/name","value":"app"},{"op":"replace","path":"/spec/template/spec/containers/{{index}}/image","value":"registry.k8s.io/pause:3.9"}]
                """, V1Patch.PatchType.JsonPatch);
            V1Deployment? updated = null;
            for (var attempt = 0; attempt < 10 && updated is null; attempt++)
            {
                try
                {
                    updated = await client.AppsV1.PatchNamespacedDeploymentAsync(Patch(read.Metadata.Uid, read.Metadata.ResourceVersion), "target", name, cancellationToken: token);
                }
                catch (HttpOperationException error) when ((int)error.Response.StatusCode == 422)
                {
                    var fresh = await client.AppsV1.ReadNamespacedDeploymentAsync("target", name, cancellationToken: token);
                    if (fresh.Metadata.Uid != read.Metadata.Uid || fresh.Metadata.ResourceVersion == read.Metadata.ResourceVersion) throw;
                    read = fresh;
                    await Task.Delay(100, token);
                }
            }
            if (updated is null) throw new InvalidOperationException("bounded patch stabilization exhausted");
            Check(updated.Metadata.Annotations["unrelated"] == "preserve" && updated.Spec.Template.Spec.Containers[0].Name == "injected", "B preserve");
            await Refuses(() => client.AppsV1.PatchNamespacedDeploymentAsync(Patch(read.Metadata.Uid, read.Metadata.ResourceVersion), "target", name, cancellationToken: token), "B stale RV", 422);
            await Refuses(() => client.AppsV1.PatchNamespacedDeploymentAsync(Patch(Guid.NewGuid().ToString(), updated.Metadata.ResourceVersion), "target", name, cancellationToken: token), "B wrong UID", 422);
            var after = await client.AppsV1.ReadNamespacedDeploymentAsync("target", name, cancellationToken: token);
            Check(after.Metadata.Uid == updated.Metadata.Uid && after.Metadata.Generation == updated.Metadata.Generation && after.Spec.Template.Spec.Containers[index].Image == updated.Spec.Template.Spec.Containers[index].Image && after.Metadata.Annotations["unrelated"] == "preserve", "B refusals unchanged");
            Console.WriteLine($"B keyed index={index}; default restartPolicy={after.Spec.Template.Spec.RestartPolicy}; strategy={after.Spec.Strategy.Type}");
            var map = await client.CoreV1.CreateNamespacedConfigMapAsync(new V1ConfigMap { Metadata = new V1ObjectMeta { Name = "replacement" } }, name, cancellationToken: token);
            await client.CoreV1.DeleteNamespacedConfigMapAsync("replacement", name, new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = map.Metadata.Uid } }, cancellationToken: token);
            await Refuses(() => client.CoreV1.ReadNamespacedConfigMapAsync("replacement", name, cancellationToken: token), "C observed absence", 404);
            var replacement = await client.CoreV1.CreateNamespacedConfigMapAsync(new V1ConfigMap { Metadata = new V1ObjectMeta { Name = "replacement" } }, name, cancellationToken: token);
            await Refuses(() => client.CoreV1.DeleteNamespacedConfigMapAsync("replacement", name, new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = map.Metadata.Uid } }, cancellationToken: token), "C foreign replacement", 409);
            Check((await client.CoreV1.ReadNamespacedConfigMapAsync("replacement", name, cancellationToken: token)).Metadata.Uid == replacement.Metadata.Uid, "C replacement preserved");
            var sa = await client.CoreV1.CreateNamespacedServiceAccountAsync(new V1ServiceAccount { Metadata = new V1ObjectMeta { Name = "runtime" } }, name, cancellationToken: token);
            var pod = await client.CoreV1.CreateNamespacedPodAsync(new V1Pod { Metadata = new V1ObjectMeta { Name = "bound" }, Spec = new V1PodSpec { ServiceAccountName = "runtime", Containers = new List<V1Container> { new() { Name = "app", Image = "registry.k8s.io/pause:3.10" } } } }, name, cancellationToken: token);
            var audience = "urn:haby:" + Guid.NewGuid() + ":" + Guid.NewGuid();
            async Task<string> Issue(bool bound) => (await client.CoreV1.CreateNamespacedServiceAccountTokenAsync(new Authenticationv1TokenRequest { Spec = new V1TokenRequestSpec { Audiences = new List<string> { audience }, ExpirationSeconds = 600, BoundObjectRef = bound ? new V1BoundObjectReference { ApiVersion = "v1", Kind = "Pod", Name = pod.Metadata.Name, Uid = pod.Metadata.Uid } : null } }, "runtime", name, cancellationToken: token)).Status.Token;
            async Task<V1TokenReview> Review(string credential, string expected) => await client.AuthenticationV1.CreateTokenReviewAsync(new V1TokenReview { Spec = new V1TokenReviewSpec { Token = credential, Audiences = new List<string> { expected } } }, cancellationToken: token);
            bool Policy(V1TokenReview review) => review.Status.Authenticated == true && review.Status.Audiences.Contains(audience) && review.Status.User.Uid == sa.Metadata.Uid && review.Status.User.Username == $"system:serviceaccount:{name}:runtime" && review.Status.User.Extra.TryGetValue("authentication.kubernetes.io/pod-uid", out var ids) && ids.Contains(pod.Metadata.Uid) && review.Status.User.Extra.TryGetValue("authentication.kubernetes.io/pod-name", out var names) && names.Contains(pod.Metadata.Name);
            var apiToken = await client.CoreV1.CreateNamespacedServiceAccountTokenAsync(new Authenticationv1TokenRequest { Spec = new V1TokenRequestSpec { Audiences = new List<string>(), ExpirationSeconds = 600 } }, "runtime", name, cancellationToken: token);
            using var bearerClient = ClientFactory.Create(endpoint, directory, apiToken.Status.Token);
            var subject = await bearerClient.AuthenticationV1.CreateSelfSubjectReviewAsync(new V1SelfSubjectReview(), cancellationToken: token);
            Check(subject.Status.UserInfo.Uid == sa.Metadata.Uid && subject.Status.UserInfo.Username == $"system:serviceaccount:{name}:runtime", "F bearer authenticated identity");
            using var invalidClient = ClientFactory.Create(endpoint, directory, "invalid");
            await Refuses(() => invalidClient.AuthenticationV1.CreateSelfSubjectReviewAsync(new V1SelfSubjectReview(), cancellationToken: token), "F invalid bearer", 401);
            await ProviderAccessCases.Run(endpoint, directory, apiToken.Status.Token, $"system:serviceaccount:{name}:runtime", sa.Metadata.Uid, token);
            var boundToken = await Issue(true);
            Check(Policy(await Review(boundToken, audience)), "D bound identity");
            Check(!Policy(await Review(boundToken, audience + ":wrong")), "D wrong audience");
            Check(!Policy(await Review("invalid", audience)), "D invalid token");
            Check(!Policy(await Review(await Issue(false), audience)), "D unbound denied");
            using var loss = new ResponseLossProbe();
            using var lossClient = ClientFactory.Create(endpoint, directory, handlers: new DelegatingHandler[] { loss });
            try
            {
                await lossClient.CoreV1.CreateNamespacedConfigMapAsync(new V1ConfigMap { Metadata = new V1ObjectMeta { Name = "uncertain", Annotations = new Dictionary<string, string> { ["haby.dev/operation"] = name } }, Data = new Dictionary<string, string> { ["state"] = "accepted" } }, name, cancellationToken: token);
                throw new InvalidOperationException("response loss not injected");
            }
            catch (HttpRequestException)
            {
                var observed = await client.CoreV1.ReadNamespacedConfigMapAsync("uncertain", name, cancellationToken: token);
                Check(loss.Attempts == 1 && observed.Metadata.Uid == loss.AcceptedUid && observed.Metadata.Annotations["haby.dev/operation"] == name && observed.Data["state"] == "accepted", "E accepted write response loss no SDK replay; exact GET");
            }
            using var watchDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            watchDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            var timer = Stopwatch.StartNew();
            var watchEvents = 0;
            try
            {
                await foreach (var item in client.CoreV1.WatchListNamespacedPodAsync(name, timeoutSeconds: 60, onError: _ => throw new InvalidOperationException("watch API error"), cancellationToken: watchDeadline.Token)) { watchEvents++; }
                throw new InvalidOperationException("watch ended without cancellation");
            }
            catch (OperationCanceledException) when (watchDeadline.IsCancellationRequested && !token.IsCancellationRequested)
            {
                Check(timer.Elapsed < TimeSpan.FromSeconds(10) && watchEvents > 0, "E bounded watch cancellation after real events");
                Console.WriteLine($"E watch elapsed milliseconds={timer.ElapsedMilliseconds}");
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var current = await client.CoreV1.ReadNamespaceAsync(name, cancellationToken: cleanup.Token);
            Check(current.Metadata.Uid == ns.Metadata.Uid && current.Metadata.Labels["haby.dev/qualification"] == name, "cleanup ownership");
            await client.CoreV1.DeleteNamespaceAsync(name, new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = ns.Metadata.Uid } }, cancellationToken: cleanup.Token);
            var absent = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try { await client.CoreV1.ReadNamespaceAsync(name, cancellationToken: cleanup.Token); }
                catch (HttpOperationException error) when ((int)error.Response.StatusCode == 404)
                {
                    Console.WriteLine("PASS namespace absence observed after UID DELETE");
                    absent = true;
                    break;
                }
                await Task.Delay(200, cleanup.Token);
            }
            if (!absent) throw new InvalidOperationException("namespace absence deadline exceeded; finalizers retained");
        }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private static async Task Refuses<T>(Func<Task<T>> action, string label, int expected)
    {
        try { await action(); }
        catch (HttpOperationException error) when ((int)error.Response.StatusCode == expected)
        {
            Console.WriteLine($"PASS {label}: HTTP {(int)error.Response.StatusCode}");
            return;
        }
        throw new InvalidOperationException(label);
    }
}
