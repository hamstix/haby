using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using k8s;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class DeploymentReadinessTests
{
    internal static JsonObject ReadyObject(DeploymentIntent intent)
    {
        var obj = KernelTests.Object(intent);
        var desired = intent.Target.Replicas;
        obj["status"] = new JsonObject
        {
            ["observedGeneration"] = 1,
            ["replicas"] = desired,
            ["updatedReplicas"] = desired,
            ["readyReplicas"] = desired,
            ["availableReplicas"] = desired
        };
        return obj;
    }
    [Theory]
    [InlineData("replicas")]
    [InlineData("updatedReplicas")]
    [InlineData("readyReplicas")]
    [InlineData("availableReplicas")]
    public void Every_positive_completion_count_is_required_and_absent_never_defaults_to_desired(string field)
    {
        var intent = ConfiguredDeploymentTests.Intent(); var obj = ReadyObject(intent);
        var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        foreach (var absent in new JsonNode?[] { null, JsonValue.Create(0) })
        {
            obj["status"]![field] = absent;
            Assert.NotEqual(RolloutOutcome.Ready, DeploymentReadiness.Evaluate(obj, intent, receipt).Outcome);
        }
    }
    [Theory]
    [InlineData("replicas")]
    [InlineData("updatedReplicas")]
    [InlineData("readyReplicas")]
    [InlineData("availableReplicas")]
    [InlineData("unavailableReplicas")]
    [InlineData("terminatingReplicas")]
    public void Negative_fractional_overflow_and_string_counts_are_invalid(string field)
    {
        var intent = KernelTests.Intent(); var obj = ReadyObject(intent); var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        foreach (var value in new[] { "-1", "1.5", "2147483648", "\"1\"", "true" })
        {
            obj["status"]![field] = JsonNode.Parse(value);
            var result = DeploymentReadiness.Evaluate(obj, intent, receipt);
            Assert.Equal(RolloutOutcome.Pending, result.Outcome); Assert.Equal("status-invalid", result.Code);
        }
    }
    [Theory]
    [InlineData("old-active")]
    [InlineData("surge")]
    [InlineData("available-over-ready")]
    [InlineData("updated-over-total")]
    [InlineData("unavailable-contradicts")]
    [InlineData("stale-controller")]
    [InlineData("new-live-generation")]
    [InlineData("future-controller")]
    [InlineData("missing-status")]
    [InlineData("missing-controller")]
    [InlineData("paused")]
    [InlineData("deleting")]
    [InlineData("duplicate-conditions")]
    [InlineData("malformed-conditions")]
    [InlineData("malformed-paused")]
    [InlineData("malformed-controller")]
    [InlineData("negative-controller")]
    [InlineData("missing-generation")]
    public void Incomplete_or_contradictory_evidence_cannot_be_ready(string scenario)
    {
        var intent = KernelTests.Intent() with { Target = KernelTests.Intent().Target with { Replicas = 2, MinReadySeconds = 5 } };
        var obj = ReadyObject(intent); var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        switch (scenario)
        {
            case "old-active": obj["status"]!["updatedReplicas"] = 1; break;
            case "surge": obj["status"]!["replicas"] = 3; break;
            case "available-over-ready": obj["status"]!["readyReplicas"] = 1; break;
            case "updated-over-total": obj["status"]!["updatedReplicas"] = 3; break;
            case "unavailable-contradicts": obj["status"]!["unavailableReplicas"] = 1; break;
            case "stale-controller": obj["status"]!["observedGeneration"] = 0; break;
            case "new-live-generation": obj["metadata"]!["generation"] = 2; break;
            case "future-controller": obj["status"]!["observedGeneration"] = 2; break;
            case "missing-status": obj.Remove("status"); break;
            case "missing-controller": obj["status"]!.AsObject().Remove("observedGeneration"); break;
            case "paused": obj["spec"]!["paused"] = true; break;
            case "deleting": obj["metadata"]!["deletionTimestamp"] = "2026-11-15T00:00:00Z"; break;
            case "duplicate-conditions": obj["status"]!["conditions"] = JsonNode.Parse("""[{"type":"Available","status":"True"},{"type":"Available","status":"True"}]"""u8); break;
            case "malformed-conditions": obj["status"]!["conditions"] = new JsonObject(); break;
            case "malformed-paused": obj["spec"]!["paused"] = "false"; break;
            case "malformed-controller": obj["status"]!["observedGeneration"] = "1"; break;
            case "negative-controller": obj["status"]!["observedGeneration"] = -1; break;
            case "missing-generation": obj["metadata"]!.AsObject().Remove("generation"); break;
        }
        Assert.NotEqual(RolloutOutcome.Ready, DeploymentReadiness.Evaluate(obj, intent, receipt).Outcome);
    }
    [Theory]
    [InlineData("Available", "False")]
    [InlineData("Available", "Unknown")]
    [InlineData("Progressing", "False")]
    [InlineData("Progressing", "Unknown")]
    [InlineData("ReplicaFailure", "True")]
    [InlineData("ReplicaFailure", "Unknown")]
    [InlineData("Available", "invalid")]
    public void Conditions_cannot_override_counts_and_disagreement_is_incomplete(string type, string status)
    {
        var intent = KernelTests.Intent(); var obj = ReadyObject(intent); var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        obj["status"]!["conditions"] = new JsonArray(new JsonObject { ["type"] = type, ["status"] = status, ["message"] = "SECRET-MESSAGE" });
        var result = DeploymentReadiness.Evaluate(obj, intent, receipt);
        Assert.NotEqual(RolloutOutcome.Ready, result.Outcome); Assert.DoesNotContain("SECRET", result.Evidence ?? result.Code);
        obj["status"]!["replicas"] = 0; obj["status"]!["conditions"]![0]!["status"] = "True";
        Assert.NotEqual(RolloutOutcome.Ready, DeploymentReadiness.Evaluate(obj, intent, receipt).Outcome);
    }
    [Fact]
    public void Progress_deadline_is_diagnostic_and_later_controller_recovery_can_be_ready()
    {
        var intent = ConfiguredDeploymentTests.Intent(); var obj = ReadyObject(intent); var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        obj["status"]!["conditions"] = new JsonArray(new JsonObject { ["type"] = "Progressing", ["status"] = "False", ["reason"] = "ProgressDeadlineExceeded", ["message"] = "PRIVATE" });
        var pending = DeploymentReadiness.Evaluate(obj, intent, receipt);
        Assert.Equal("progress-deadline", pending.Code); Assert.DoesNotContain("PRIVATE", pending.Evidence!);
        obj["status"]!.AsObject().Remove("conditions");
        var recovered = DeploymentReadiness.Evaluate(obj, intent, receipt);
        Assert.Equal(RolloutOutcome.Ready, recovered.Outcome);
        Assert.True(DeploymentReadiness.Validate(DeploymentIntents.Encode(intent), DeploymentIntents.Encode(receipt), recovered));
        Assert.False(DeploymentReadiness.Validate(DeploymentIntents.Encode(intent), DeploymentIntents.Encode(receipt with { Uid = "foreign" }), recovered));
    }
    [Theory]
    [InlineData("uid")]
    [InlineData("owner")]
    [InlineData("operation")]
    [InlineData("image")]
    [InlineData("bootstrap")]
    [InlineData("audience")]
    [InlineData("mount")]
    public void Readiness_reuses_full_owned_projection_and_saved_uid(string drift)
    {
        var intent = ConfiguredDeploymentTests.Intent(); var obj = ReadyObject(intent); var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        var spec = obj["spec"]!["template"]!["spec"]!;
        switch (drift)
        {
            case "uid": obj["metadata"]!["uid"] = "replacement"; break;
            case "owner": obj["metadata"]!["labels"]![DeploymentIntents.OwnerKey] = Guid.NewGuid().ToString("N"); break;
            case "operation": obj["metadata"]!["annotations"]![DeploymentIntents.OperationKey] = Guid.NewGuid().ToString("N"); break;
            case "image": spec["containers"]![0]!["image"] = "different:v2"; break;
            case "bootstrap": spec["containers"]![0]!["env"]![0]!["value"] = "{}"; break;
            case "audience": spec["volumes"]![0]!["projected"]!["sources"]![0]!["serviceAccountToken"]!["audience"] = "different"; break;
            case "mount": spec["containers"]![0]!["volumeMounts"]![0]!["subPath"] = "token"; break;
        }
        Assert.Equal(RolloutOutcome.Conflict, DeploymentReadiness.Evaluate(obj, intent, receipt).Outcome);
    }
    [Fact]
    public void Available_count_honors_min_ready_seconds_and_unrelated_metadata_does_not_conflict()
    {
        var intent = KernelTests.Intent(); intent = intent with { Target = intent.Target with { MinReadySeconds = 10 } };
        var obj = ReadyObject(intent); var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        obj["status"]!["availableReplicas"] = 0;
        Assert.Equal(RolloutOutcome.Pending, DeploymentReadiness.Evaluate(obj, intent, receipt).Outcome);
        obj["status"]!["availableReplicas"] = 1;
        obj["metadata"]!["annotations"]!["external.example/annotation"] = "unmanaged";
        obj["metadata"]!["resourceVersion"] = "later";
        Assert.Equal(RolloutOutcome.Ready, DeploymentReadiness.Evaluate(obj, intent, receipt).Outcome);
    }
    [Theory]
    [InlineData(200, "Ready", false)]
    [InlineData(404, "Conflict", false)]
    [InlineData(401, "Unavailable", false)]
    [InlineData(403, "Unavailable", false)]
    [InlineData(429, "Unavailable", true)]
    [InlineData(503, "Unavailable", true)]
    public async Task Readiness_sends_one_exact_Deployment_GET_and_no_identity_or_write_calls(int status, string outcome, bool temporary)
    {
        var intent = ConfiguredDeploymentTests.Intent(); var obj = ReadyObject(intent); var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        var api = new ReadOnlyApi(status, obj);
        var kernel = new DeploymentKernel(intent.Binding, _ => ValueTask.FromResult(new KernelClient(new k8s.Kubernetes(
            new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api))));
        var observed = await kernel.ReadinessAsync(intent, receipt, default);
        Assert.Equal(outcome, observed.Outcome.ToString()); Assert.Equal(temporary, observed.Temporary); Assert.Equal(1, api.Reads);
        Assert.DoesNotContain("PRIVATE", observed.Evidence ?? observed.Code);
    }
    [Fact]
    public async Task Batch_admission_is_after_connection_acquisition_and_immediately_before_the_actual_GET()
    {
        var intent = ConfiguredDeploymentTests.Intent(); var obj = ReadyObject(intent); var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        var api = new ReadOnlyApi(200, obj);
        var connected = new TaskCompletionSource<KernelClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admissions = 0;
        var kernel = new DeploymentKernel(intent.Binding, async ct => await connected.Task.WaitAsync(ct));
        var result = kernel.ReadinessAsync(intent, receipt, default, new ProbeAdmission(async (read, ct) =>
        {
            Assert.True(connected.Task.IsCompleted); Assert.Equal(0, api.Reads); admissions++;
            return await read(ct);
        }));
        Assert.Equal(0, admissions); Assert.Equal(0, api.Reads);
        connected.SetResult(new KernelClient(new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api)));
        Assert.Equal(RolloutOutcome.Ready, (await result).Outcome); Assert.Equal(1, admissions); Assert.Equal(1, api.Reads);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_admission_budget_or_revocation_is_not_hidden_by_adapter_error_mapping(bool revoked)
    {
        var intent = ConfiguredDeploymentTests.Intent(); var obj = ReadyObject(intent); var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        var api = new ReadOnlyApi(200, obj);
        var kernel = new DeploymentKernel(intent.Binding, _ => ValueTask.FromResult(new KernelClient(new k8s.Kubernetes(
            new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api))));
        var admission = new ProbeAdmission((_, _) => throw (revoked ? new RolloutReadRefusedException() : new RolloutBatchExhaustedException()));
        if (revoked) await Assert.ThrowsAsync<RolloutReadRefusedException>(() => kernel.ReadinessAsync(intent, receipt, default, admission));
        else await Assert.ThrowsAsync<RolloutBatchExhaustedException>(() => kernel.ReadinessAsync(intent, receipt, default, admission));
        Assert.Equal(0, api.Reads);
    }
    private sealed class ProbeAdmission(Func<Func<CancellationToken, ValueTask<RolloutObservation>>, CancellationToken, ValueTask<RolloutObservation>> invoke) : IReadinessReadAdmission
    {
        public ValueTask<RolloutObservation> RunAsync(Func<CancellationToken, ValueTask<RolloutObservation>> read, CancellationToken token) => invoke(read, token);
    }
    private sealed class ReadOnlyApi(int status, JsonObject obj) : DelegatingHandler
    {
        internal int Reads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.EndsWith("/deployments/orders-api-main", request.RequestUri!.AbsolutePath);
            Reads++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                RequestMessage = request,
                Content = new StringContent(status == 200 ? obj.ToJsonString() : $$"""{"apiVersion":"v1","kind":"Status","status":"Failure","code":{{status}},"message":"PRIVATE"}""", Encoding.UTF8, "application/json")
            });
        }
    }
}
