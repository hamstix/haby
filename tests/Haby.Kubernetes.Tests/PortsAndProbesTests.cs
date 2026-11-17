using System.Text.Json.Nodes;
using Haby.Kubernetes.Contracts;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class PortsAndProbesTests
{
    internal const string Properties = """
        {"image":{"reference":"registry.example/app:v1"},"ports":{"web":{"containerPort":8080},"dns":{"containerPort":5353,"protocol":"UDP"}},
        "probes":{"startup":{"httpGet":{"port":"web","path":"/health"}},"liveness":{"tcpSocket":{"port":"web"}},
        "readiness":{"httpGet":{"port":"web","path":"/ready","scheme":"HTTPS"},"successThreshold":2,"failureThreshold":5}}}
        """;

    internal static DeploymentIntent Intent(string properties = Properties) => DeploymentIntents.Prepare(
        PreparationTests.Resolve(properties), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid());

    [Fact]
    public void Accepted_ports_and_all_probe_slots_survive_frozen_intent_create_and_receipt()
    {
        var intent = DeploymentIntents.Decode(DeploymentIntents.Encode(Intent()));
        var obj = KernelTests.Object(intent); var main = MainContainer(obj);
        Assert.Equal(2, main["ports"]!.AsArray().Count);
        Assert.Equal("UDP", main["ports"]![0]!["protocol"]!.GetValue<string>());
        Assert.Equal("web", main["startupProbe"]!["httpGet"]!["port"]!.GetValue<string>());
        Assert.Equal("web", main["livenessProbe"]!["tcpSocket"]!["port"]!.GetValue<string>());
        Assert.Equal("HTTPS", main["readinessProbe"]!["httpGet"]!["scheme"]!.GetValue<string>());
        var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        Assert.Equal(DeploymentIntents.Encode(receipt), DeploymentIntents.Encode(DeploymentIntents.DecodeReceipt(DeploymentIntents.Encode(receipt))));
    }

    [Theory]
    [InlineData("startupProbe", "periodSeconds")]
    [InlineData("startupProbe", "initialDelaySeconds")]
    [InlineData("livenessProbe", "timeoutSeconds")]
    [InlineData("livenessProbe", "successThreshold")]
    [InlineData("livenessProbe", "failureThreshold")]
    public void Omitted_API_defaults_compare_but_changed_explicit_values_conflict(string slot, string field)
    {
        var intent = Intent(); var obj = KernelTests.Object(intent); var probe = MainContainer(obj)[slot]!;
        var original = probe[field]!.DeepClone(); probe.AsObject().Remove(field);
        Assert.NotNull(DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response));
        probe[field] = original.GetValue<int>() + 1;
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response));
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("grpc")]
    [InlineData("httpHeaders")]
    [InlineData("host")]
    [InlineData("terminationGracePeriodSeconds")]
    public void Unsupported_owned_probe_fields_cannot_confirm(string extra)
    {
        var intent = Intent(); var obj = KernelTests.Object(intent); var probe = MainContainer(obj)["startupProbe"]!;
        if (extra is "host") probe["httpGet"]![extra] = "other";
        else if (extra is "httpHeaders") probe["httpGet"]![extra] = new JsonArray();
        else probe[extra] = extra == "terminationGracePeriodSeconds" ? JsonValue.Create(5) : new JsonObject();
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response));
    }

    [Theory]
    [InlineData("number")]
    [InlineData("protocol")]
    [InlineData("reference")]
    [InlineData("range")]
    [InlineData("action")]
    [InlineData("null")]
    public void Corrupt_saved_ports_and_probes_refuse_for_intents_and_receipts(string fault)
    {
        var intent = Intent(); var receipt = DeploymentObjects.Receipt(KernelTests.Object(intent), intent, ConfirmationSource.Response);
        foreach (var isReceipt in new[] { false, true })
        {
            var json = JsonNode.Parse(isReceipt ? DeploymentIntents.Encode(receipt) : DeploymentIntents.Encode(intent))!;
            var p = json[isReceipt ? "projection" : "target"]!;
            switch (fault)
            {
                case "number": p["ports"]!["web"]!["number"] = 65536; break;
                case "protocol": p["ports"]!["web"]!["protocol"] = "SCTP"; break;
                case "reference": p["probes"]!["startup"]!["port"] = "dns"; break;
                case "range": p["probes"]!["startup"]!["successThreshold"] = 2; break;
                case "action": p["probes"]!["startup"]!["scheme"] = null; break;
                case "null": p["ports"] = null; break;
            }
            Assert.Throws<WorkloadContractException>(() => isReceipt ? DeploymentIntents.DecodeReceipt(json.ToJsonString()).Format : DeploymentIntents.Decode(json.ToJsonString()).Format);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Patch_changes_action_or_removes_owned_entries_preserving_foreign_container_and_ports(bool remove)
    {
        var old = Intent(); var obj = KernelTests.Object(old); var main = MainContainer(obj);
        var foreign = new JsonObject { ["name"] = "injected", ["containerPort"] = 9000, ["protocol"] = "TCP" };
        main["ports"]!.AsArray().Insert(0, foreign);
        obj["spec"]!["template"]!["spec"]!["containers"]!.AsArray().Insert(0, new JsonObject { ["name"] = "sidecar", ["image"] = "side:v1" });
        var next = Intent(remove ? """{"image":{"reference":"registry.example/app:v1"}}""" : Properties.Replace("\"httpGet\":{\"port\":\"web\",\"path\":\"/health\"}", "\"tcpSocket\":{\"port\":\"web\"}", StringComparison.Ordinal));
        var update = old with { ExpectedUid = "saved-uid", LastConfirmed = old.Target, Target = next.Target, Operation = Guid.NewGuid() };
        DeploymentObjects.Matches(obj, old.Target);
        ResourceRequirementsTests.Apply(obj, DeploymentObjects.Patch(obj, update));
        Assert.Equal("injected", main["ports"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("sidecar", obj["spec"]!["template"]!["spec"]!["containers"]![0]!["name"]!.GetValue<string>());
        Assert.NotNull(DeploymentObjects.Receipt(obj, update, ConfirmationSource.Response));
        if (remove)
        {
            Assert.Single(main["ports"]!.AsArray()); Assert.Null(main["startupProbe"]);
            main["ports"]!.AsArray().Add(new JsonObject { ["name"] = "web", ["containerPort"] = 8080 });
            Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, update, ConfirmationSource.Observation));
        }
        else { Assert.Null(main["startupProbe"]!["httpGet"]); Assert.NotNull(main["startupProbe"]!["tcpSocket"]); }
    }

    [Theory]
    [InlineData("port")]
    [InlineData("probe")]
    public void New_ownership_cannot_adopt_equal_injected_fields(string kind)
    {
        var old = KernelTests.Intent(); var obj = KernelTests.Object(old); var target = Intent().Target;
        var desired = MainContainer(KernelTests.Object(Intent()));
        MainContainer(obj)[kind == "port" ? "ports" : "startupProbe"] = desired[kind == "port" ? "ports" : "startupProbe"]!.DeepClone();
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Patch(obj, old with { LastConfirmed = old.Target, ExpectedUid = "saved-uid", Target = target }));
    }

    [Fact]
    public void Duplicate_observed_named_ports_and_manual_owned_drift_conflict()
    {
        var intent = Intent(); var obj = KernelTests.Object(intent); var ports = MainContainer(obj)["ports"]!.AsArray();
        ports.Add(ports[0]!.DeepClone());
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Matches(obj, intent.Target));
        ports.RemoveAt(ports.Count - 1); ports[0]!["containerPort"] = 9999;
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Matches(obj, intent.Target));
    }

    internal static JsonNode MainContainer(JsonObject obj) => obj["spec"]!["template"]!["spec"]!["containers"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "app")!;

    [Fact]
    public void Declaration_reordering_and_omitted_defaults_preserve_frozen_meaning()
    {
        var declaration = JsonNode.Parse(Properties)!;
        var ports = declaration["ports"]!.AsObject();
        declaration["ports"] = new JsonObject { ["dns"] = ports["dns"]!.DeepClone(), ["web"] = ports["web"]!.DeepClone() };
        var old = Intent(); var reordered = Intent(declaration.ToJsonString());
        Assert.Equal(DeploymentIntents.Encode(old), DeploymentIntents.Encode(old with { Target = reordered.Target }));
        var obj = KernelTests.Object(old);
        MainContainer(obj)["ports"]![1]!.AsObject().Remove("protocol");
        MainContainer(obj)["startupProbe"]!["httpGet"]!.AsObject().Remove("scheme");
        Assert.NotNull(DeploymentObjects.Receipt(obj, old, ConfirmationSource.Response));
    }

    [Fact]
    public void Previously_absent_slots_and_unnamed_foreign_ports_remain_unowned()
    {
        var old = KernelTests.Intent(); var obj = KernelTests.Object(old); var main = MainContainer(obj);
        main["readinessProbe"] = new JsonObject { ["exec"] = new JsonObject { ["command"] = new JsonArray("foreign") } };
        main["ports"] = new JsonArray(new JsonObject { ["containerPort"] = 9000, ["hostPort"] = 9001 });
        var original = main.DeepClone();
        var update = old with { ExpectedUid = "saved-uid", LastConfirmed = old.Target, Operation = Guid.NewGuid() };
        DeploymentObjects.Matches(obj, old.Target);
        ResourceRequirementsTests.Apply(obj, DeploymentObjects.Patch(obj, update));
        Assert.True(JsonNode.DeepEquals(original, main));
        Assert.NotNull(DeploymentObjects.Receipt(obj, update, ConfirmationSource.Response));
    }

    [Theory]
    [InlineData("startupProbe")]
    [InlineData("livenessProbe")]
    [InlineData("readinessProbe")]
    public void Restored_removed_owned_probe_does_not_confirm(string slot)
    {
        var old = Intent(); var obj = KernelTests.Object(old); var saved = MainContainer(obj)[slot]!.DeepClone();
        var update = old with { ExpectedUid = "saved-uid", LastConfirmed = old.Target, Target = KernelTests.Intent().Target, Operation = Guid.NewGuid() };
        ResourceRequirementsTests.Apply(obj, DeploymentObjects.Patch(obj, update));
        Assert.NotNull(DeploymentObjects.Receipt(obj, update, ConfirmationSource.Response));
        MainContainer(obj)[slot] = saved;
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, update, ConfirmationSource.Observation));
    }

    [Theory]
    [InlineData("port")]
    [InlineData("probe")]
    public async Task Owned_drift_blocks_readiness_and_uncertain_observation_without_writes(string field)
    {
        var intent = Intent(); var obj = KernelTests.Object(intent);
        var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        if (field == "port") MainContainer(obj)["ports"]![1]!["containerPort"] = 8081;
        else MainContainer(obj)["readinessProbe"]!["failureThreshold"] = 6;
        Assert.Equal(Haby.Application.RolloutOutcome.Conflict, DeploymentReadiness.Evaluate(obj, intent, receipt).Outcome);
        var api = new KernelTests.FakeApi((_, _) => KernelTests.Reply(200, obj));
        var kernel = new DeploymentKernel(intent.Binding, _ => ValueTask.FromResult(new KernelClient(new k8s.Kubernetes(
            new k8s.KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api))));
        var observed = await kernel.ObserveAsync(intent);
        Assert.Equal(KernelOutcome.NotConfirmed, observed.Outcome); Assert.Null(observed.Receipt); Assert.Equal(0, observed.WriteDispatches);
        Assert.Equal(new[] { "GET" }, api.Methods);
    }

    [Fact]
    public void Frozen_named_port_order_remains_ordinal_across_codec_reload()
    {
        var properties = JsonNode.Parse(Properties)!;
        properties["ports"]!["a-a"] = new JsonObject { ["containerPort"] = 9000 };
        properties["ports"]!["aa"] = new JsonObject { ["containerPort"] = 9001 };
        var intent = Intent(properties.ToJsonString());
        var saved = DeploymentIntents.Encode(intent);
        Assert.Equal(saved, DeploymentIntents.Encode(DeploymentIntents.Decode(saved)));
    }

    [Fact]
    public void Omitted_default_HTTP_root_path_matches_only_an_explicit_root_target()
    {
        var intent = Intent(Properties.Replace("/health", "/", StringComparison.Ordinal)); var obj = KernelTests.Object(intent);
        MainContainer(obj)["startupProbe"]!["httpGet"]!.AsObject().Remove("path");
        Assert.NotNull(DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response));
        var nonRoot = Intent();
        Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, nonRoot with { Owner = intent.Owner, ManagedWorkload = intent.ManagedWorkload, Operation = intent.Operation }, ConfirmationSource.Response));
    }
}
