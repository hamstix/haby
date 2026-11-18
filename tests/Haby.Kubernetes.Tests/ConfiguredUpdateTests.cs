using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using k8s;
using k8s.Models;
using Haby.Configuration.Contracts;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class ConfiguredUpdateTests
{
    [Fact]
    public async Task Configured_patch_sdk_wire_preserves_test_values_and_server_compatible_bootstrap_encoding()
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
        var current = KernelTests.Object(previous);
        current["spec"]!["template"]!["spec"]!["containers"]!.AsArray().Insert(0, new JsonObject { ["name"] = "foreign", ["image"] = "foreign:v1" });
        var patch = DeploymentObjects.Patch(current, next);
        var api = new KernelTests.FakeApi((_, _) => KernelTests.Reply(200, current));
        using var client = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api);
        using var response = await client.AppsV1.PatchNamespacedDeploymentWithHttpMessagesAsync(
            new V1Patch(DeploymentPatchTransport.Serialize(patch), V1Patch.PatchType.JsonPatch), next.Name, next.Namespace);
        using var wire = JsonDocument.Parse(Assert.Single(api.Bodies));
        var tests = wire.RootElement.EnumerateArray().Where(x => x.GetProperty("op"u8).GetString() == "test").ToArray();
        Assert.NotEmpty(tests);
        foreach (var operation in tests)
        {
            JsonNode? live = current;
            foreach (var segment in operation.GetProperty("path"u8).GetString()!.Split('/').Skip(1))
                live = live is JsonArray array ? array[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)] : live![segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
            Assert.Equal(live!.GetValue<string>(), operation.GetProperty("value"u8).GetString());
        }
        var oldValue = tests.Single(x => x.GetProperty("path"u8).GetString() == "/spec/template/spec/containers/1/env/0/value").GetProperty("value"u8);
        var goStyleScalar = JsonSerializer.Serialize(previous.Target.Runtime.Bootstrap,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        // Source-audited evanphx/json-patch v4 compares compact scalar JSON bytes.
        Assert.Equal(goStyleScalar, oldValue.GetRawText());
        Assert.Equal(previous.Target.Runtime.Bootstrap, JsonSerializer.Deserialize<string>(goStyleScalar));
    }
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
