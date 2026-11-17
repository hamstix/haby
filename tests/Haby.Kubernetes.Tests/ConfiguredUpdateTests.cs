using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using k8s;
using Haby.Configuration.Contracts;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class ConfiguredUpdateTests
{
    [Theory]
    [InlineData("uid")]
    [InlineData("owner")]
    [InlineData("image")]
    [InlineData("bootstrap")]
    [InlineData("token-volume")]
    [InlineData("token-mount")]
    [InlineData("token-audience")]
    [InlineData("sidecar-token")]
    public async Task Configured_update_refuses_prior_identity_or_owned_security_drift_before_write(string fault)
    {
        var prior = ConfiguredDeploymentTests.Intent();
        var next = prior with
        {
            ExpectedUid = "saved-uid",
            LastConfirmed = prior.Target,
            TargetGeneration = 2,
            Operation = Guid.NewGuid(),
            Target = prior.Target with { Image = "registry.example/app:v2" }
        };
        var current = KernelTests.Object(prior);
        var spec = current["spec"]!["template"]!["spec"]!;
        var main = spec["containers"]![0]!;
        switch (fault)
        {
            case "uid": current["metadata"]!["uid"] = "replacement"; break;
            case "owner": current["metadata"]!["labels"]![DeploymentIntents.OwnerKey] = "foreign"; break;
            case "image": main["image"] = "registry.example/hotfix:v1"; break;
            case "bootstrap": main["env"]![0]!["value"] = "{}"; break;
            case "token-volume": spec["volumes"]![0]!["projected"]!["sources"]!.AsArray().Add(new JsonObject()); break;
            case "token-mount": main["volumeMounts"]![0]!["subPath"] = "token"; break;
            case "token-audience": spec["volumes"]![0]!["projected"]!["sources"]![0]!["serviceAccountToken"]!["audience"] = "foreign"; break;
            case "sidecar-token": spec["containers"]!.AsArray().Add(new JsonObject { ["name"] = "foreign", ["image"] = "foreign:v1", ["volumeMounts"] = main["volumeMounts"]!.DeepClone() }); break;
        }
        var api = new PriorApi(prior, current);
        var kernel = new DeploymentKernel(next.Binding, _ => ValueTask.FromResult(new KernelClient(
            new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api))));
        var result = await kernel.ExecuteAsync(next);
        Assert.Equal(KernelOutcome.Conflict, result.Outcome);
        Assert.Equal(0, api.Writes); Assert.Equal(0, result.WriteDispatches);
    }

    private sealed class PriorApi(DeploymentIntent prior, JsonObject current) : DelegatingHandler
    {
        internal int Writes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get) Writes++;
            var body = request.RequestUri!.AbsolutePath.Contains("/serviceaccounts/", StringComparison.Ordinal) ? new JsonObject
            {
                ["metadata"] = new JsonObject
                {
                    ["name"] = prior.Target.Runtime!.ServiceAccount,
                    ["namespace"] = prior.Namespace,
                    ["uid"] = prior.Target.Runtime.ServiceAccountUid,
                    ["labels"] = new JsonObject
                    { [DeploymentIntents.OwnerKey] = prior.Owner.ToString("N"), [DeploymentIntents.ManagedKey] = prior.ManagedWorkload.ToString("N") }
                },
                ["automountServiceAccountToken"] = false
            } : current;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public void Configured_patch_changes_exact_bootstrap_by_named_index_and_preserves_foreign_fields()
    {
        var previous = ConfiguredDeploymentTests.Intent();
        var bootstrap = RuntimeBootstrapCodec.Decode(previous.Target.Runtime!.Bootstrap);
        var next = previous with
        {
            ExpectedUid = "saved-uid",
            LastConfirmed = previous.Target,
            TargetGeneration = 2,
            Operation = Guid.NewGuid(),
            Target = previous.Target with
            {
                Image = "registry.example/app:v2",
                Runtime = previous.Target.Runtime with
                {
                    Bootstrap = RuntimeBootstrapCodec.Encode(bootstrap with
                    { Documents = bootstrap.Documents.ToImmutableSortedDictionary(x => x.Key, _ => Guid.CreateVersion7().ToString("D"), StringComparer.Ordinal) })
                }
            }
        };
        Assert.Equal(DeploymentIntents.Encode(next), DeploymentIntents.Encode(DeploymentIntents.Decode(DeploymentIntents.Encode(next))));
        var current = KernelTests.Object(previous);
        var containers = current["spec"]!["template"]!["spec"]!["containers"]!.AsArray();
        containers.Insert(0, new JsonObject { ["name"] = "foreign", ["image"] = "foreign:v1" });
        containers[1]!["env"]!.AsArray().Insert(0, new JsonObject { ["name"] = "FOREIGN", ["value"] = "retained" });
        DeploymentObjects.Matches(current, next.LastConfirmed);
        var patch = DeploymentObjects.Patch(current, next);
        Assert.Contains(patch, x => x!["op"]!.GetValue<string>() == "test" && x["path"]!.GetValue<string>() == "/metadata/resourceVersion" && x["value"]!.GetValue<string>() == "19");
        Assert.Contains(patch, x => x!["op"]!.GetValue<string>() == "test" && x["path"]!.GetValue<string>() == "/metadata/uid");
        Assert.Contains(patch, x => x!["path"]!.GetValue<string>() == "/spec/template/spec/containers/1/env/1/value" && x["value"]!.GetValue<string>() == next.Target.Runtime!.Bootstrap);
        Assert.DoesNotContain(patch, x => x!["path"]!.GetValue<string>() is "/spec/template/spec" or "/spec/template/spec/containers" or "/spec/template/spec/containers/1/env" or "/spec/template/spec/volumes");
        Assert.DoesNotContain(patch, x => x!["path"]!.GetValue<string>().StartsWith("/spec/template/spec/containers/0", StringComparison.Ordinal));
    }
}
