using System.Text.Json.Nodes;
using Haby.Kubernetes.Contracts;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class ResourceRequirementsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Effective_resources_survive_frozen_intent_and_create(bool wholeOverride)
    {
        var workload = PreparationTests.Resolve(wholeOverride
            ? """{"image":{"reference":"registry.example/app:v1"},"overrides":{"resources":{"limits":{"memory":"1Gi"}}}}"""
            : """{"image":{"reference":"registry.example/app:v1"}}""",
            """{"namespace":"test","resources":{"requests":{"cpu":"250m","memory":"128Mi"},"limits":{"cpu":"1","memory":"512Mi"}}}""");
        var intent = DeploymentIntents.Prepare(workload, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid());
        var saved = JsonNode.Parse(DeploymentIntents.Encode(intent))!;
        Assert.Equal(wholeOverride ? null : "250m", saved["target"]!["resources"]!["requests"]?["cpu"]?.GetValue<string>());
        var reloaded = DeploymentIntents.Decode(saved.ToJsonString());
        var obj = KernelTests.Object(reloaded);
        Assert.Equal(wholeOverride ? "1Gi" : "512Mi", obj["spec"]!["template"]!["spec"]!["containers"]![0]!["resources"]!["limits"]!["memory"]!.GetValue<string>());
        Assert.Equal(DeploymentIntents.Encode(reloaded), DeploymentIntents.Encode(DeploymentIntents.Decode(DeploymentIntents.Encode(reloaded))));
    }

    [Theory]
    [InlineData("250m", "0.25", "128Mi", "134217728", true)]
    [InlineData("250m", "251m", "128Mi", "134217728", false)]
    [InlineData("250m", "0.25", "128Mi", "134217729", false)]
    [InlineData(null, "1", null, "1073741824", true)]
    [InlineData(null, "999m", null, "1073741824", false)]
    public void Readback_compares_exact_quantities_and_limit_only_defaults(string? requestCpu, string observedCpu, string? requestMemory, string observedMemory, bool equivalent)
    {
        var requests = new JsonObject();
        if (requestCpu is not null) requests["cpu"] = requestCpu;
        if (requestMemory is not null) requests["memory"] = requestMemory;
        var resources = new JsonObject { ["limits"] = new JsonObject { ["cpu"] = "1", ["memory"] = "1Gi" } };
        if (requests.Count > 0) resources["requests"] = requests;
        var workload = PreparationTests.Resolve(placementSpec: "{\"namespace\":\"test\",\"resources\":" + resources.ToJsonString() + "}");
        var intent = DeploymentIntents.Prepare(workload, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid());
        var obj = KernelTests.Object(intent);
        obj["spec"]!["template"]!["spec"]!["containers"]![0]!["resources"]!["requests"] = new JsonObject { ["cpu"] = observedCpu, ["memory"] = observedMemory };
        if (equivalent) Assert.NotNull(DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response));
        else Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response));
    }

    [Theory]
    [InlineData("cpu", "0")]
    [InlineData("cpu", "10000.001")]
    [InlineData("cpu", "0.0001")]
    [InlineData("cpu", "1e0")]
    [InlineData("cpu", "2")]
    [InlineData("memory", "0")]
    [InlineData("memory", "1125899906842625")]
    [InlineData("memory", "1.5Gi")]
    [InlineData("memory", "2Gi")]
    public void Invalid_reloaded_intent_and_receipt_quantities_refuse(string dimension, string invalid)
    {
        var intent = WithResources(new(new("250m", "128Mi"), new("1", "1Gi")));
        Assert.Throws<WorkloadContractException>(() => DeploymentIntents.Encode(intent with { Format = 2 }));
        var json = JsonNode.Parse(DeploymentIntents.Encode(intent))!;
        json["target"]!["resources"]!["requests"]![dimension] = invalid;
        Assert.Throws<WorkloadContractException>(() => DeploymentIntents.Decode(json.ToJsonString()));
        var receipt = DeploymentObjects.Receipt(KernelTests.Object(intent), intent, ConfirmationSource.Response);
        Assert.Throws<WorkloadContractException>(() => DeploymentIntents.Encode(receipt with { Format = 2 }));
        var saved = JsonNode.Parse(DeploymentIntents.Encode(receipt))!;
        saved["projection"]!["resources"]!["requests"]![dimension] = invalid;
        Assert.Throws<WorkloadContractException>(() => DeploymentIntents.DecodeReceipt(saved.ToJsonString()));
    }

    [Theory]
    [InlineData("0.001", "1m", true)]
    [InlineData("10000", "10k", true)]
    [InlineData("1Gi", "1024Mi", true)]
    [InlineData("1Gi", "1.073741824G", true)]
    [InlineData("1000", "1e3", true)]
    [InlineData("1Ti", "1099511627777", false)]
    [InlineData("1", "1e1000", false)]
    [InlineData("1", "NaN", false)]
    [InlineData("1", "", false)]
    public void Observed_quantity_equivalence_uses_exact_rationals(string expected, string observed, bool equivalent) =>
        Assert.Equal(equivalent, DeploymentResources.Equivalent(expected, observed));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Patch_add_change_remove_preserves_foreign_dimensions_and_keyed_container(bool remove)
    {
        var old = WithResources(new(new("250m", "128Mi"), new("1", "1Gi")));
        var obj = KernelTests.Object(old); var spec = obj["spec"]!["template"]!["spec"]!;
        spec["containers"]!.AsArray().Insert(0, new JsonObject { ["name"] = "injected", ["image"] = "sidecar:v1" });
        var main = spec["containers"]![1]!;
        main["resources"]!["limits"]!["ephemeral-storage"] = "1Gi";
        main["resources"]!["requests"]!["example.test/device"] = "1";
        main["env"] = new JsonArray(new JsonObject { ["name"] = "FOREIGN", ["value"] = "preserve" });
        var update = old with
        {
            ExpectedUid = "saved-uid",
            LastConfirmed = old.Target,
            Operation = Guid.NewGuid(),
            TargetGeneration = 2,
            Target = old.Target with { Resources = remove ? null : new(new("500m", "256Mi"), new("2", "2Gi")) }
        };
        DeploymentObjects.Matches(obj, old.Target);
        Apply(obj, DeploymentObjects.Patch(obj, update));
        Assert.Equal("sidecar:v1", spec["containers"]![0]!["image"]!.GetValue<string>());
        Assert.Equal("1Gi", main["resources"]!["limits"]!["ephemeral-storage"]!.GetValue<string>());
        Assert.Equal("1", main["resources"]!["requests"]!["example.test/device"]!.GetValue<string>());
        Assert.Equal("preserve", main["env"]![0]!["value"]!.GetValue<string>());
        Assert.Equal(update.Operation, DeploymentObjects.Receipt(obj, update, ConfirmationSource.Response).Operation);
        if (remove)
        {
            main["resources"]!["requests"]!["cpu"] = "250m";
            Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, update, ConfirmationSource.Observation));
        }
        else
        {
            var empty = KernelTests.Intent(); var initial = KernelTests.Object(empty);
            var add = empty with { ExpectedUid = "saved-uid", LastConfirmed = empty.Target, Target = update.Target };
            Apply(initial, DeploymentObjects.Patch(initial, add));
            Assert.NotNull(DeploymentObjects.Receipt(initial, add, ConfirmationSource.Response));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mismatch")]
    [InlineData("shape")]
    public async Task Resource_drift_blocks_readiness_and_observation_without_another_create(string fault)
    {
        var intent = WithResources(new(new("250m", "128Mi"), new("1", "1Gi")));
        var obj = KernelTests.Object(intent);
        var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        var main = obj["spec"]!["template"]!["spec"]!["containers"]![0]!;
        if (fault == "missing") main["resources"]!["requests"]!.AsObject().Remove("cpu");
        if (fault == "mismatch") main["resources"]!["requests"]!["cpu"] = "251m";
        if (fault == "shape") main["resources"]!["requests"] = new JsonArray();
        Assert.Equal(Haby.Application.RolloutOutcome.Conflict, DeploymentReadiness.Evaluate(obj, intent, receipt).Outcome);
        var api = new KernelTests.FakeApi((_, _) => KernelTests.Reply(200, obj));
        var kernel = new DeploymentKernel(intent.Binding, _ => ValueTask.FromResult(new KernelClient(new k8s.Kubernetes(
            new k8s.KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api))));
        var result = await kernel.ObserveAsync(intent);
        Assert.Equal(fault == "shape" ? KernelOutcome.ObservationUnavailable : KernelOutcome.NotConfirmed, result.Outcome); Assert.Null(result.Receipt); Assert.Equal(0, result.WriteDispatches);
        Assert.Equal(new[] { "GET" }, api.Methods);
    }

    [Fact]
    public void Manual_owned_drift_and_new_leaf_collision_refuse_before_patch()
    {
        var old = WithResources(new(new("250m", null), null)); var obj = KernelTests.Object(old);
        obj["spec"]!["template"]!["spec"]!["containers"]![0]!["resources"]!["requests"]!["cpu"] = "251m";
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Matches(obj, old.Target));
        obj = KernelTests.Object(KernelTests.Intent());
        obj["spec"]!["template"]!["spec"]!["containers"]![0]!["resources"] = DeploymentResources.Json(old.Target.Resources!);
        var plain = KernelTests.Intent();
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Patch(obj, plain with { LastConfirmed = plain.Target, ExpectedUid = "saved-uid", Target = old.Target }));
    }

    [Fact]
    public void Removing_explicit_request_with_retained_limit_allows_absent_template_request()
    {
        var old = WithResources(new(new("250m", null), new("1", null)));
        var update = old with
        {
            LastConfirmed = old.Target,
            ExpectedUid = "saved-uid",
            Operation = Guid.NewGuid(),
            Target = old.Target with { Resources = new(null, new("1", null)) }
        };
        var obj = KernelTests.Object(old);
        Apply(obj, DeploymentObjects.Patch(obj, update));
        var requests = obj["spec"]!["template"]!["spec"]!["containers"]![0]!["resources"]!["requests"]!;
        Assert.Null(requests["cpu"]);
        Assert.NotNull(DeploymentObjects.Receipt(obj, update, ConfirmationSource.Response));
        requests["cpu"] = "1";
        Assert.NotNull(DeploymentObjects.Receipt(obj, update, ConfirmationSource.Response));
        requests["cpu"] = "250m";
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, update, ConfirmationSource.Observation));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("1", true)]
    [InlineData("1000m", true)]
    [InlineData("999m", false)]
    public void Limit_only_Deployment_template_preserves_absence_and_checks_any_present_default(string? observedRequest, bool matches)
    {
        var intent = WithResources(new(null, new("1", "1Gi")));
        var obj = KernelTests.Object(intent);
        if (observedRequest is not null)
            obj["spec"]!["template"]!["spec"]!["containers"]![0]!["resources"]!["requests"] = new JsonObject { ["cpu"] = observedRequest };
        if (matches)
        {
            var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
            Assert.Null(receipt.Projection.Resources!.Requests);
            Assert.Null(DeploymentIntents.DecodeReceipt(DeploymentIntents.Encode(receipt)).Projection.Resources!.Requests);
        }
        else Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response));
    }

    [Fact]
    public void Observed_default_is_not_authority_to_acquire_or_remove_an_unowned_request()
    {
        var old = WithResources(new(null, new("1", null))); var obj = KernelTests.Object(old);
        var main = obj["spec"]!["template"]!["spec"]!["containers"]![0]!;
        main["resources"]!["requests"] = new JsonObject { ["cpu"] = "1", ["example.test/device"] = "1" };
        DeploymentObjects.Matches(obj, old.Target);
        var acquire = old with
        {
            ExpectedUid = "saved-uid",
            LastConfirmed = old.Target,
            Target = old.Target with { Resources = new(new("1", null), new("1", null)) }
        };
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Patch(obj, acquire));
        var removeLimit = old with { ExpectedUid = "saved-uid", LastConfirmed = old.Target, Target = old.Target with { Resources = null } };
        Apply(obj, DeploymentObjects.Patch(obj, removeLimit));
        Assert.Equal("1", main["resources"]!["requests"]!["cpu"]!.GetValue<string>());
        Assert.Equal("1", main["resources"]!["requests"]!["example.test/device"]!.GetValue<string>());
        Assert.NotNull(DeploymentObjects.Receipt(obj, removeLimit, ConfirmationSource.Response));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Limit_change_preserves_explicit_requests_but_cannot_overwrite_an_observed_default(bool explicitRequest)
    {
        var old = WithResources(new(explicitRequest ? new("250m", null) : null, new("1", null)));
        var obj = KernelTests.Object(old); var main = obj["spec"]!["template"]!["spec"]!["containers"]![0]!;
        if (!explicitRequest) main["resources"]!["requests"] = new JsonObject { ["cpu"] = "1" };
        var update = old with
        {
            ExpectedUid = "saved-uid",
            LastConfirmed = old.Target,
            Target = old.Target with { Resources = new(old.Target.Resources!.Requests, new("2", null)) }
        };
        DeploymentObjects.Matches(obj, old.Target);
        if (explicitRequest)
        {
            Apply(obj, DeploymentObjects.Patch(obj, update));
            Assert.Equal("250m", main["resources"]!["requests"]!["cpu"]!.GetValue<string>());
            Assert.NotNull(DeploymentObjects.Receipt(obj, update, ConfirmationSource.Response));
        }
        else Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Patch(obj, update));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Limit_removal_preserves_explicit_request_and_does_not_invent_an_absent_request(bool explicitRequest)
    {
        var requirements = new ResourceRequirements(explicitRequest ? new("250m", null) : null, new("1", null));
        var old = WithResources(requirements); var obj = KernelTests.Object(old);
        var update = old with
        {
            ExpectedUid = "saved-uid",
            LastConfirmed = old.Target,
            Target = old.Target with { Resources = explicitRequest ? new(requirements.Requests, null) : null }
        };
        DeploymentObjects.Matches(obj, old.Target);
        Apply(obj, DeploymentObjects.Patch(obj, update));
        var main = obj["spec"]!["template"]!["spec"]!["containers"]![0]!;
        Assert.Null(main["resources"]!["limits"]!["cpu"]);
        Assert.Equal(explicitRequest ? "250m" : null, main["resources"]!["requests"]?["cpu"]?.GetValue<string>());
        Assert.NotNull(DeploymentObjects.Receipt(obj, update, ConfirmationSource.Response));
        main["resources"]!["limits"]!["cpu"] = "1";
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, update, ConfirmationSource.Observation));
    }

    [Fact]
    public void Present_null_leaf_is_not_absence_default_or_acquisition_authority()
    {
        foreach (var dimension in new[] { "cpu", "memory" })
        {
            var implicitRequest = WithResources(new(null, new("1", "1Gi")));
            var obj = KernelTests.Object(implicitRequest);
            var main = obj["spec"]!["template"]!["spec"]!["containers"]![0]!;
            main["resources"]!["requests"] = new JsonObject { [dimension] = null };
            Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, implicitRequest, ConfirmationSource.Response));
            var plain = KernelTests.Intent();
            var acquire = plain with { LastConfirmed = plain.Target, ExpectedUid = "saved-uid", Target = implicitRequest.Target with { Resources = new(new("1", "1Gi"), null) } };
            Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Patch(obj, acquire));
            var removed = implicitRequest with
            {
                LastConfirmed = implicitRequest.Target with { Resources = new(new("1", "1Gi"), null) },
                ExpectedUid = "saved-uid",
                Target = implicitRequest.Target with { Resources = null }
            };
            main["resources"]!.AsObject().Remove("limits");
            Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, removed, ConfirmationSource.Observation));
        }
    }

    private static DeploymentIntent WithResources(ResourceRequirements resources)
    {
        var intent = KernelTests.Intent(); return intent with { Target = intent.Target with { Resources = resources } };
    }

    internal static void Apply(JsonObject obj, JsonArray patch)
    {
        foreach (var operation in patch)
        {
            var parts = operation!["path"]!.GetValue<string>().Split('/').Skip(1).Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            JsonNode parent = obj;
            foreach (var part in parts[..^1]) parent = parent is JsonArray array ? array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)]! : parent[part]!;
            var key = parts[^1]; var op = operation["op"]!.GetValue<string>(); var value = operation["value"]?.DeepClone();
            if (parent is JsonArray list)
            {
                var index = key == "-" ? list.Count : int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
                if (op == "test") Assert.True(JsonNode.DeepEquals(list[index], value));
                else if (op == "remove") list.RemoveAt(index);
                else { Assert.Equal("add", op); list.Insert(index, value); }
            }
            else if (op == "test") Assert.True(JsonNode.DeepEquals(parent[key], value));
            else if (op == "remove") Assert.True(parent.AsObject().Remove(key));
            else { Assert.Equal("add", op); parent[key] = value; }
        }
    }
}
