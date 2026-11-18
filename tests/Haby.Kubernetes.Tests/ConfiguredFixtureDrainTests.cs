using System.Text.Json.Nodes;
using Haby.Kubernetes;
using k8s;
using k8s.Models;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class ConfiguredFixtureDrainTests
{
    private static (DeploymentReceipt Receipt, V1Namespace Namespace, V1Deployment Deployment, V1ReplicaSetList Sets, V1PodList Pods) Fixture()
    {
        var intent = ConfiguredDeploymentTests.Intent();
        var obj = DeploymentReadinessTests.ReadyObject(intent);
        var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        var deployment = KubernetesJson.Deserialize<V1Deployment>(obj.ToJsonString());
        return (receipt, new() { Metadata = new() { Name = receipt.Namespace, Uid = "run-namespace", Labels = new Dictionary<string, string> { ["haby.dev/qualification"] = receipt.Namespace } } },
            deployment, new()
            {
                Metadata = new() { ResourceVersion = "11" },
                Items = [new()
            {
                Metadata = new() { Name = "set", NamespaceProperty = receipt.Namespace, Uid = "set-uid", OwnerReferences =
                    [new() { ApiVersion = "apps/v1", Kind = "Deployment", Name = receipt.Name, Uid = receipt.Uid, Controller = true }] }
            }]
            }, new()
            {
                Metadata = new() { ResourceVersion = "12" },
                Items = new[] { "pre-restart", "replacement" }.Select(name => new V1Pod
                {
                    Metadata = new()
                    {
                        Name = name,
                        NamespaceProperty = receipt.Namespace,
                        Uid = name,
                        DeletionTimestamp = name == "pre-restart" ? DateTime.UtcNow : null,
                        OwnerReferences = [new() { ApiVersion = "apps/v1", Kind = "ReplicaSet", Name = "set", Uid = "set-uid", Controller = true }]
                    }
                }).ToList()
            });
    }
    private static HttpResponseMessage Reply<T>(T value) => KernelTests.Reply(200, JsonNode.Parse(KubernetesJson.Serialize(value))!.AsObject());

    [Theory]
    [InlineData("foreign-deployment")]
    [InlineData("replaced-deployment")]
    [InlineData("replaced-namespace")]
    [InlineData("foreign-pod")]
    [InlineData("paged-pods")]
    public async Task Unknown_or_replaced_ownership_refuses_before_scale(string defect)
    {
        var f = Fixture();
        if (defect == "replaced-namespace") f.Namespace.Metadata.Uid = "different-run";
        if (defect == "foreign-deployment") f.Deployment.Metadata.Labels[DeploymentIntents.OwnerKey] = Guid.NewGuid().ToString("N");
        if (defect == "foreign-pod") f.Pods.Items[0].Metadata.OwnerReferences[0].Uid = "foreign-set";
        if (defect == "paged-pods") f.Pods.Metadata.ContinueProperty = "more";
        var namedReads = 0;
        using var api = new KernelTests.FakeApi((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert.Equal(HttpMethod.Get, request.Method);
            if (path.EndsWith("/namespaces/" + f.Receipt.Namespace, StringComparison.Ordinal)) return Reply(f.Namespace);
            if (path.EndsWith("/deployments", StringComparison.Ordinal)) return Reply(new V1DeploymentList { Metadata = new() { ResourceVersion = "10" }, Items = [f.Deployment] });
            if (path.EndsWith("/replicasets", StringComparison.Ordinal)) return Reply(f.Sets);
            if (path.EndsWith("/pods", StringComparison.Ordinal)) return Reply(f.Pods);
            namedReads++;
            if (defect == "replaced-deployment") f.Deployment.Metadata.Uid = "replacement";
            return Reply(f.Deployment);
        });
        using var admin = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api);
        var owned = new V1Namespace { Metadata = new() { Name = f.Receipt.Namespace, Uid = "run-namespace" } };
        await Assert.ThrowsAsync<ConfiguredFixtureDrain.OwnershipRefused>(() => ConfiguredFixtureDrain.RunAsync(admin, owned, [f.Receipt], default));
        Assert.DoesNotContain("PATCH", api.Methods);
        if (defect == "replaced-deployment") Assert.Equal(1, namedReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Drain_keeps_the_original_deadline_and_waits_for_both_pre_restart_and_replacement_absence(bool cancel)
    {
        var f = Fixture(); var scaled = false; var observations = 0; var pauses = 0; var cleanup = 0;
        using var budget = new CancellationTokenSource();
        using var api = new KernelTests.FakeApi((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Patch) { scaled = true; f.Deployment.Spec.Replicas = 0; return Reply(f.Deployment); }
            Assert.Equal(HttpMethod.Get, request.Method);
            if (path.EndsWith("/namespaces/" + f.Receipt.Namespace, StringComparison.Ordinal)) return Reply(f.Namespace);
            if (path.EndsWith("/replicasets", StringComparison.Ordinal)) return Reply(f.Sets);
            if (path.EndsWith("/pods", StringComparison.Ordinal))
            {
                if (scaled && ++observations > 1) f.Pods.Items.RemoveAt(0);
                return Reply(f.Pods);
            }
            return path.EndsWith("/deployments", StringComparison.Ordinal)
                ? Reply(new V1DeploymentList { Metadata = new() { ResourceVersion = "10" }, Items = [f.Deployment] }) : Reply(f.Deployment);
        });
        using var admin = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api);
        async Task Run()
        {
            try
            {
                await ConfiguredFixtureDrain.RunAsync(admin, f.Namespace, [f.Receipt], budget.Token, (interval, ct) =>
                {
                    Assert.Equal(budget.Token, ct); Assert.Equal(TimeSpan.FromMilliseconds(250), interval);
                    pauses++; if (cancel && pauses == 2) budget.Cancel();
                    ct.ThrowIfCancellationRequested(); return Task.CompletedTask;
                });
            }
            finally { cleanup++; }
        }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Run); else await Run();
        Assert.Equal(1, cleanup); Assert.Equal(1, api.Methods.Count(x => x == "PATCH"));
        Assert.DoesNotContain("DELETE", api.Methods);
        Assert.Equal(cancel ? 2 : 3, observations); Assert.Equal(2, pauses);
        var patch = JsonNode.Parse(Assert.Single(api.Bodies))!.AsArray();
        Assert.Equal(f.Receipt.Uid, patch[0]!["value"]!.GetValue<string>());
        Assert.Equal(f.Deployment.Metadata.ResourceVersion, patch[1]!["value"]!.GetValue<string>());
        Assert.Equal(0, patch[2]!["value"]!.GetValue<int>());
        Assert.DoesNotContain("gracePeriodSeconds", api.Bodies[0]);
        Assert.Equal(cancel ? 1 : 0, f.Pods.Items.Count);
    }
}
