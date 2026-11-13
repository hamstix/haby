using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using k8s;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class RemovedFieldConfirmationTests
{
    [Theory]
    [InlineData("node", false, true)]
    [InlineData("pull", false, true)]
    [InlineData("node", true, true)]
    [InlineData("pull", true, true)]
    [InlineData("node", false, false)]
    [InlineData("pull", false, false)]
    [InlineData("node", true, false)]
    [InlineData("pull", true, false)]
    public async Task Confirmation_requires_removed_owned_entries_absent_but_preserves_never_owned_entries(
        string field, bool observe, bool retained)
    {
        var old = KernelTests.Intent();
        var intent = old with
        {
            ExpectedUid = "saved-uid",
            LastConfirmed = old.Target,
            TargetGeneration = 2,
            Operation = Guid.NewGuid(),
            Target = old.Target with { Image = "registry.example/app:v2", NodeSelector = old.Target.NodeSelector.Clear(), ImagePullSecrets = [] },
        };
        var before = KernelTests.Object(old); var after = KernelTests.Object(intent);
        foreach (var obj in new[] { before, after })
        {
            var spec = obj["spec"]!["template"]!["spec"]!;
            spec["nodeSelector"]!["injected"] = "preserve";
            spec["imagePullSecrets"]!.AsArray().Insert(0, new JsonObject { ["name"] = "injected" });
        }
        // Correct UID, owner, new operation/generation markers and all target scalar values.
        // Only a formerly owned entry that the target removed is restored by the response/live state.
        if (retained && field == "node") after["spec"]!["template"]!["spec"]!["nodeSelector"]!["disk"] = "ssd";
        if (retained && field == "pull") after["spec"]!["template"]!["spec"]!["imagePullSecrets"]!.AsArray().Add(new JsonObject { ["name"] = "registry" });
        using var api = new ConfirmationApi(before, after, observe);
        var kernel = new DeploymentKernel(intent.Binding, _ => ValueTask.FromResult(new KernelClient(
            new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api))));
        var result = observe ? await kernel.ObserveAsync(intent) : await kernel.ExecuteAsync(intent);
        var expected = !retained ? KernelOutcome.Confirmed : observe ? KernelOutcome.NotConfirmed : KernelOutcome.Uncertain;
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(observe ? 0 : 1, result.WriteDispatches);
        Assert.Equal(observe ? new[] { "GET" } : new[] { "GET", "PATCH" }, api.Methods);
        if (retained)
        {
            Assert.Null(result.Receipt);
            Assert.Equal(observe ? "evidence-not-confirmed" : "response-unconfirmed", result.Code);
        }
        else
        {
            Assert.NotNull(result.Receipt); Assert.Empty(result.Receipt.Projection.NodeSelector); Assert.Empty(result.Receipt.Projection.ImagePullSecrets);
            Assert.Equal(observe ? ConfirmationSource.Observation : ConfirmationSource.Response, result.Receipt.Source);
        }
        Assert.Equal("preserve", after["spec"]!["template"]!["spec"]!["nodeSelector"]!["injected"]!.GetValue<string>());
        Assert.Equal("injected", after["spec"]!["template"]!["spec"]!["imagePullSecrets"]![0]!["name"]!.GetValue<string>());
    }

    private sealed class ConfirmationApi(JsonObject before, JsonObject after, bool observe) : DelegatingHandler
    {
        internal List<string> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method.Method);
            var obj = observe || request.Method == HttpMethod.Patch ? after : before;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }
}
