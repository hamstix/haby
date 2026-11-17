using System.Net;
using System.Text.Json.Nodes;
using k8s.Autorest;
using k8s;
using k8s.Models;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class KernelReplacementFixtureTests
{
    [Fact]
    public async Task Delayed_404_after_fifty_reads_precedes_the_single_replacement_create()
    {
        var reads = 0; var absent = false; var pauses = 0; var elapsed = TimeSpan.Zero; var observed = (Absent: false, Attempts: 0);
        var replacement = Target();
        using var api = new KernelTests.FakeApi((request, _) =>
        {
            if (request.Method == HttpMethod.Delete) return KernelTests.Reply(200, new JsonObject { ["kind"] = "Status", ["status"] = "Success" });
            if (request.Method == HttpMethod.Get)
            {
                reads++;
                if (reads == 53) { absent = true; return KernelTests.Reply(404); }
                return KernelTests.Reply(200, JsonNode.Parse("""{"metadata":{"name":"replacement","uid":"original"}}"""u8)!.AsObject());
            }
            Assert.Equal(HttpMethod.Post, request.Method); Assert.True(absent);
            return KernelTests.Reply(201, JsonNode.Parse("""{"metadata":{"name":"replacement","uid":"new"}}"""u8)!.AsObject());
        });
        using var admin = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api);
        using var parent = new CancellationTokenSource();
        await DeploymentKernelReplacement.RunAsync(admin, "owned", "replacement", "original", replacement, parent.Token,
            (_, _) => { }, (value, attempts) => observed = (value, attempts), (interval, token) =>
            { Assert.Equal(TimeSpan.FromMilliseconds(250), interval); Assert.Equal(parent.Token, token); elapsed += interval; Assert.True(elapsed < TimeSpan.FromSeconds(150)); pauses++; return Task.CompletedTask; });
        Assert.Equal((true, 53), observed); Assert.Equal(52, pauses);
        Assert.Equal(1, api.Methods.Count(x => x == "DELETE")); Assert.Equal(1, api.Methods.Count(x => x == "POST"));
        var deletion = JsonNode.Parse(api.Bodies[0])!;
        Assert.Equal("original", deletion["preconditions"]!["uid"]!.GetValue<string>());
        Assert.Equal("Orphan", deletion["propagationPolicy"]!.GetValue<string>());
        Assert.DoesNotContain("gracePeriodSeconds", api.Bodies[0]);
        var created = JsonNode.Parse(api.Bodies[1])!;
        Assert.Equal("replacement", created["metadata"]!["name"]!.GetValue<string>());
        Assert.Equal(3, created["spec"]!["replicas"]!.GetValue<int>());
    }

    [Fact]
    public async Task Parent_deadline_exhaustion_retains_count_and_cleanup_without_replacement()
    {
        using var parent = new CancellationTokenSource();
        var elapsed = TimeSpan.Zero; var reads = 0; var cleanup = 0; var observed = (Absent: true, Attempts: -1);
        using var api = new KernelTests.FakeApi((request, _) =>
        {
            if (request.Method == HttpMethod.Delete) return Success();
            Assert.Equal(HttpMethod.Get, request.Method); reads++; return Present();
        });
        using var admin = Client(api);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            try
            {
                await DeploymentKernelReplacement.RunAsync(admin, "owned", "replacement", "original", Target(), parent.Token,
                    (_, _) => { }, (value, attempts) => observed = (value, attempts), (interval, token) =>
                    {
                        Assert.Equal(parent.Token, token); elapsed += interval;
                        if (elapsed >= TimeSpan.FromSeconds(150)) parent.Cancel();
                        token.ThrowIfCancellationRequested(); return Task.CompletedTask;
                    });
            }
            finally { cleanup++; }
        });
        Assert.Equal(TimeSpan.FromSeconds(150), elapsed); Assert.Equal(600, reads);
        Assert.Equal((false, 600), observed); Assert.Equal(1, cleanup);
        Assert.Equal(1, api.Methods.Count(x => x == "DELETE")); Assert.DoesNotContain("POST", api.Methods);
        Assert.Equal("Q243 kernel k14 absence-poll exception canceled unknown",
            DeploymentKernelReplacement.FailureEvidence("k14", "absence-poll", error));
    }

    [Theory]
    [InlineData("before-delete")]
    [InlineData("before-read")]
    [InlineData("after-404")]
    [InlineData("before-create")]
    public async Task Cancellation_at_dispatch_boundaries_prevents_replacement(string boundary)
    {
        using var parent = new CancellationTokenSource(); if (boundary == "before-delete") parent.Cancel();
        var phases = new List<string>(); var observations = new List<(bool, int)>(); var cleanup = 0;
        using var api = new KernelTests.FakeApi((request, _) =>
        {
            if (request.Method == HttpMethod.Delete) return Success();
            Assert.Equal(HttpMethod.Get, request.Method); return KernelTests.Reply(404);
        });
        using var admin = Client(api);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            try
            {
                await DeploymentKernelReplacement.RunAsync(admin, "owned", "replacement", "original", Target(), parent.Token,
                    (phase, state) =>
                    {
                        phases.Add(phase + " " + state);
                        if (boundary == "before-read" && phase == "absence-poll" || boundary == "before-create" && phase == "replacement-create") parent.Cancel();
                    }, (value, attempts) =>
                    {
                        observations.Add((value, attempts));
                        if (boundary == "after-404") parent.Cancel();
                    }, (_, _) => throw new InvalidOperationException("No pause expected."));
            }
            finally { cleanup++; }
        });
        Assert.Equal(1, cleanup); Assert.DoesNotContain("POST", api.Methods);
        Assert.Equal(boundary == "before-delete" ? 0 : 1, api.Methods.Count(x => x == "DELETE"));
        if (boundary == "before-delete") Assert.Empty(phases);
        else Assert.Contains("uid-delete completed", phases);
        if (boundary == "before-read") Assert.Equal(new[] { (false, 0) }, observations);
        if (boundary is "after-404" or "before-create") Assert.Equal(new[] { (true, 1) }, observations);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task Non404_read_failure_propagates_exact_status_without_retry_or_replacement(int status)
    {
        var phases = new List<string>(); var observed = (Absent: true, Attempts: 0); var cleanup = 0;
        using var api = new KernelTests.FakeApi((request, _) => request.Method == HttpMethod.Delete ? Success() : KernelTests.Reply(status));
        using var admin = Client(api); using var parent = new CancellationTokenSource();
        var error = await Assert.ThrowsAsync<HttpOperationException>(async () =>
        {
            try
            {
                await DeploymentKernelReplacement.RunAsync(admin, "owned", "replacement", "original", Target(), parent.Token,
                    (phase, state) => phases.Add(phase + " " + state), (value, attempts) => observed = (value, attempts),
                    (_, _) => throw new InvalidOperationException("No pause allowed after an HTTP error."));
            }
            finally { cleanup++; }
        });
        Assert.Equal((HttpStatusCode)status, error.Response.StatusCode); Assert.Equal((false, 1), observed);
        Assert.Equal(new[] { "DELETE", "GET" }, api.Methods); Assert.Equal(1, cleanup);
        Assert.DoesNotContain("replacement-create start", phases);
        Assert.Equal($"Q243 kernel k24 absence-poll exception http {status}", DeploymentKernelReplacement.FailureEvidence("k24", "absence-poll", error));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("")]
    public async Task Unexpected_present_uid_refuses_without_touching_the_replacement(string uid)
    {
        var observed = (Absent: true, Attempts: 0); var cleanup = 0;
        using var api = new KernelTests.FakeApi((request, _) => request.Method == HttpMethod.Delete ? Success() :
            KernelTests.Reply(200, new JsonObject { ["metadata"] = new JsonObject { ["uid"] = uid } }));
        using var admin = Client(api); using var parent = new CancellationTokenSource();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            try
            {
                await DeploymentKernelReplacement.RunAsync(admin, "owned", "replacement", "original", Target(), parent.Token,
                    (_, _) => { }, (value, attempts) => observed = (value, attempts), (_, _) => throw new InvalidOperationException("No pause expected."));
            }
            finally { cleanup++; }
        });
        Assert.Equal((false, 1), observed); Assert.Equal(new[] { "DELETE", "GET" }, api.Methods); Assert.Equal(1, cleanup);
    }

    [Fact]
    public async Task Finite_read_ceiling_refuses_even_if_a_test_pause_does_not_advance_the_parent_clock()
    {
        var reads = 0; var pauses = 0; var observed = (Absent: true, Attempts: 0);
        using var api = new KernelTests.FakeApi((request, _) =>
        {
            if (request.Method == HttpMethod.Delete) return Success();
            Assert.Equal(HttpMethod.Get, request.Method); reads++; return Present();
        });
        using var admin = Client(api); using var parent = new CancellationTokenSource();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeploymentKernelReplacement.RunAsync(admin, "owned", "replacement", "original", Target(), parent.Token,
            (_, _) => { }, (value, attempts) => observed = (value, attempts), (_, _) => { pauses++; return Task.CompletedTask; }));
        Assert.Equal(601, reads); Assert.Equal(600, pauses); Assert.Equal((false, 601), observed);
        Assert.Equal(1, api.Methods.Count(x => x == "DELETE")); Assert.DoesNotContain("POST", api.Methods);
    }

    [Fact]
    public async Task Observation_writer_failure_does_not_replace_the_original_http_error()
    {
        using var api = new KernelTests.FakeApi((request, _) => request.Method == HttpMethod.Delete ? Success() : KernelTests.Reply(409));
        using var admin = Client(api); using var parent = new CancellationTokenSource();
        var error = await Assert.ThrowsAsync<HttpOperationException>(() => DeploymentKernelReplacement.RunAsync(admin, "owned", "replacement", "original", Target(), parent.Token,
            (_, _) => { }, (_, _) => throw new IOException("Protected writer failure."), (_, _) => Task.CompletedTask));
        Assert.Equal(HttpStatusCode.Conflict, error.Response.StatusCode); Assert.Equal(new[] { "DELETE", "GET" }, api.Methods);
        Assert.DoesNotContain("Protected", DeploymentKernelReplacement.FailureEvidence("k14", "absence-poll", error));
    }

    private static V1Deployment Target() => new() { Metadata = new() { Name = "replacement" }, Spec = new() { Replicas = 3 } };
    private static k8s.Kubernetes Client(DelegatingHandler api) => new(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api);
    private static HttpResponseMessage Success() => KernelTests.Reply(200, new JsonObject { ["kind"] = "Status", ["status"] = "Success" });
    private static HttpResponseMessage Present() => KernelTests.Reply(200, JsonNode.Parse("""{"metadata":{"name":"replacement","uid":"original"}}"""u8)!.AsObject());
}
