using Haby.Application;
using Haby.Kubernetes;
using Haby.Domain;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using Haby.Manifest;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class DeploymentImageUpdateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepted_deep_Profile_compares_image_updates_and_refuses_changed_nested_values(bool changed)
    {
        static ConfigurationProfileRevision Profile(string leaf)
        {
            var payload = Encoding.UTF8.GetBytes("""{"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"deep","revision":1},"spec":{"documents":{"deep-settings":{"fileName":"deep.json","format":"json","template":""" + new string('[', 60) + leaf + new string(']', 60) + "}}}}");
            var accepted = ProfilePayloadParser.ParseProfile(payload);
            Assert.True(accepted.IsSuccess, string.Join(",", accepted.Diagnostics.Select(x => x.Code)));
            return accepted.Declaration!;
        }
        var token = AssignmentToken.New();
        DurablePlanningInputs Selected(DurablePlanningInputs inputs, ConfigurationProfileRevision profile)
        {
            var scopes = ConfigurationPlanningSnapshot.Targets(inputs.Providers.Declaration).Select(t =>
                new ConfigurationPlanningScope(t, t is ProfileAssignmentTarget.Environment ? token : null,
                    new(t, t is ProfileAssignmentTarget.Environment ? [new ProfileDecision.Use(profile.Reference)] : [])));
            var prepared = ConfigurationPlanningSnapshot.Prepare(inputs.Revision, inputs.Providers.Declaration, scopes, [profile]);
            Assert.True(prepared.Expansion.IsSuccess);
            return inputs with { Configuration = prepared.Snapshot! };
        }
        var context = InternalDeploymentPlacementTests.Contexts(false)[0];
        var baseline = Selected(context.Inputs, Profile("0"));
        var target = Selected(Target(context, root => Properties(root)["image"]!["members"]!["reference"] = Value("registry.example/app:v2")), Profile(changed ? "1" : "0"));
        var snapshot = ConfigurationPlanningSnapshotCodec.Encode(baseline.Configuration!);
        using var document = JsonDocument.Parse(snapshot, new JsonDocumentOptions { MaxDepth = 160 });
        static int Depth(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Object => 1 + value.EnumerateObject().Select(x => Depth(x.Value)).DefaultIfEmpty().Max(),
            JsonValueKind.Array => 1 + value.EnumerateArray().Select(Depth).DefaultIfEmpty().Max(),
            _ => 0
        };
        Assert.InRange(Depth(document.RootElement), 65, 160);
        Assert.Equal(baseline.Configuration, ConfigurationPlanningSnapshotCodec.Decode(snapshot, baseline.Revision));
        Assert.Equal(!changed, WorkloadUpdateInputs.Same(baseline, target));
    }
    [Fact]
    public void Reordered_maps_and_a_new_source_revision_preserve_semantic_inputs()
    {
        var context = InternalDeploymentPlacementTests.Contexts(false)[0];
        var target = Target(context, root =>
        {
            var components = root["spec"]!["components"]!.AsObject();
            var reversed = components.Reverse().Select(x => (x.Key, Value: x.Value!.DeepClone())).ToArray();
            components.Clear();
            foreach (var item in reversed) components.Add(item.Key, item.Value);
        });
        Assert.NotEqual(context.Inputs.Revision, target.Revision);
        Assert.True(WorkloadUpdateInputs.Same(context.Inputs, target));
    }
    [Theory]
    [InlineData("reference", "registry.example/app:v2", "registry.example/app:v2")]
    [InlineData("tag", "release-2", "registry.example/app:release-2")]
    [InlineData("tagFrom", "applicationVersion", "registry.example/app:exact-v2")]
    public void Image_forms_resolve_the_exact_target_version(string form, string value, string expected)
    {
        var context = InternalDeploymentPlacementTests.Contexts(false)[0];
        // Complete references do not need a registry; composed forms select one exact Placement.
        context = WithRegistry(context);
        var target = Target(context, root =>
        {
            var image = form == "reference" ? new JsonObject { ["reference"] = Value(value) } :
                new JsonObject { ["name"] = Value("app"), [form] = Value(value) };
            Properties(root)["image"] = new JsonObject { ["kind"] = "object", ["members"] = image };
        });
        var intent = DeploymentIntents.Prepare(InternalDeploymentAction.Resolve(context), Guid.Parse(context.Key.DeploymentId.ToString()),
            context.ManagedIdentity, context.Key.Generation.Value, context.Marker);
        var receipt = Receipt(intent);
        var result = DeploymentImageUpdate.Capture(context, target, "exact-v2", DeploymentIntents.Encode(intent), DeploymentIntents.Encode(receipt));
        Assert.Equal("ImageChanged", result.Classification);
        Assert.Equal(expected, result.TargetImage);
        Assert.Equal("registry.example/app:v1", result.PreviousImage);
        Assert.Equal(receipt.Uid, result.Uid);
        Assert.True(WorkloadUpdateInputs.Same(context.Inputs, target));
    }

    [Theory]
    [InlineData("container")]
    [InlineData("replicas")]
    [InlineData("component-added")]
    [InlineData("workload-removed")]
    [InlineData("document")]
    [InlineData("provider")]
    public void Nonimage_or_membership_changes_are_not_silently_absorbed(string fault)
    {
        var context = InternalDeploymentPlacementTests.Contexts(false, configured: true)[0];
        if (fault == "provider")
        {
            var entries = context.Inputs.Providers.Entries.Values.Select(x => x with { Configuration = ProviderConfigurationRevisionNumber.Create(2) });
            Assert.False(WorkloadUpdateInputs.Same(context.Inputs, context.Inputs with { Providers = new(context.Inputs.Providers.Declaration, context.Inputs.Environment, entries) }));
            return;
        }
        var target = Target(context, root =>
        {
            var components = root["spec"]!["components"]!.AsObject();
            switch (fault)
            {
                case "container": Properties(root)["containerName"] = Value("different"); break;
                case "replicas": Properties(root)["overrides"] = new JsonObject { ["kind"] = "object", ["members"] = new JsonObject { ["replicas"] = new JsonObject { ["kind"] = "number", ["value"] = "2" } } }; break;
                case "component-added": components["extra"] = components["worker"]!.DeepClone(); break;
                case "workload-removed": components["worker"]!["workloads"]!.AsObject().Remove("main"); break;
                case "document": components["api"]!["documents"]!["settings"]!["file"] = "changed.json"; break;
            }
        }, rebuildProviders: true);
        Assert.False(WorkloadUpdateInputs.Same(context.Inputs, target));
    }

    private static JsonObject Value(string text) => new() { ["kind"] = "string", ["value"] = text };
    private static JsonObject Properties(JsonNode root) => root["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["members"]!.AsObject();
    private static DurablePlanningInputs Target(InternalActionContext context, Action<JsonNode> change, bool rebuildProviders = false)
    {
        var root = JsonNode.Parse(NormalizedSnapshotCodec.Encode(context.Inputs.Providers.Declaration))!;
        change(root);
        var manifest = NormalizedSnapshotCodec.Decode(root.ToJsonString(), context.Inputs.Providers.Declaration.InputApiVersion,
            context.Inputs.Providers.Declaration.NormalizedModelVersion, context.Inputs.Providers.Declaration.ApplicationId.Value, NormalizedSnapshotCodec.FormatVersion);
        var reference = ManifestRevisionRef.Create(context.Inputs.Revision.ApplicationRecordId, ManifestRevisionNumber.Create(2));
        var scopes = ConfigurationPlanningSnapshot.Targets(manifest).Select(t => new ConfigurationPlanningScope(t, null, new(t, [])));
        var configuration = ConfigurationPlanningSnapshot.Prepare(reference, manifest, scopes, []).Snapshot!;
        var entries = context.Inputs.Providers.Entries.Values.ToArray();
        if (rebuildProviders)
            entries = manifest.Spec.Components.SelectMany(c => c.Value.Workloads.Select(w =>
                entries[0] with { Input = new ProviderInput.Workload(c.Key, w.Key) })).ToArray();
        return context.Inputs with { Revision = reference, Configuration = configuration, Providers = new(manifest, context.Inputs.Environment, entries) };
    }
    private static InternalActionContext WithRegistry(InternalActionContext context)
    {
        var entries = context.Inputs.Providers.Entries.Values.Select(x =>
        {
            var pin = InternalPlacementInput.Decode(x.Placement!);
            var portable = JsonNode.Parse(pin.Declaration.Payload)!;
            portable["spec"]!["registry"] = "registry.example";
            var declaration = new Haby.Kubernetes.Contracts.KubernetesPlacementCodec().Decode(System.Text.Encoding.UTF8.GetBytes(portable.ToJsonString()));
            return x with { Placement = InternalPlacementInput.Encode(pin.Id, declaration) };
        }).ToArray();
        var inputs = context.Inputs with { Providers = new(context.Inputs.Providers.Declaration, context.Inputs.Environment, entries) };
        return context with { Inputs = inputs, Provider = entries.Single(x => x.Input == context.Subject) };
    }
    private static DeploymentReceipt Receipt(DeploymentIntent intent) => new(4, intent.Binding, intent.Namespace, intent.Name, intent.Owner,
        intent.ManagedWorkload, intent.TargetGeneration, intent.Operation, "original-uid", 1, intent.Target, ConfirmationSource.Response);
    [Fact]
    public void Frozen_update_preserves_original_receipt_and_classifies_equal_image_without_a_transition()
    {
        var context = InternalDeploymentPlacementTests.Contexts(false)[0];
        var prior = DeploymentIntents.Prepare(InternalDeploymentAction.Resolve(context),
            Guid.Parse(context.Key.DeploymentId.ToString()), context.ManagedIdentity, context.Key.Generation.Value, context.Marker);
        var receipt = new DeploymentReceipt(4, prior.Binding, prior.Namespace, prior.Name, prior.Owner,
            prior.ManagedWorkload, prior.TargetGeneration, prior.Operation, "original-uid", 1, prior.Target, ConfirmationSource.Response);
        var frozen = DeploymentImageUpdate.Capture(context, context.Inputs, "v2",
            DeploymentIntents.Encode(prior), DeploymentIntents.Encode(receipt));
        Assert.Equal("UnchangedImage", frozen.Classification);
        Assert.Equal("original-uid", frozen.Uid);
        Assert.Equal(DeploymentIntents.Encode(receipt), frozen.PreviousReceipt);
        Assert.Equal(receipt.TargetGeneration, DeploymentIntents.DecodeReceipt(frozen.PreviousReceipt).TargetGeneration);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("managed")]
    [InlineData("image")]
    [InlineData("generation")]
    public void Foreign_or_inconsistent_original_receipt_refuses(string fault)
    {
        var context = InternalDeploymentPlacementTests.Contexts(false)[0];
        var prior = DeploymentIntents.Prepare(InternalDeploymentAction.Resolve(context), Guid.Parse(context.Key.DeploymentId.ToString()),
            context.ManagedIdentity, context.Key.Generation.Value, context.Marker);
        var receipt = new DeploymentReceipt(4, prior.Binding, prior.Namespace, prior.Name,
            fault == "owner" ? Guid.NewGuid() : prior.Owner, fault == "managed" ? Guid.NewGuid() : prior.ManagedWorkload,
            fault == "generation" ? prior.TargetGeneration + 1 : prior.TargetGeneration, prior.Operation,
            "original-uid", 1, fault == "image" ? prior.Target with { Image = "registry.example/foreign:v1" } : prior.Target, ConfirmationSource.Response);
        Assert.Throws<InvalidDataException>(() => DeploymentImageUpdate.Capture(context, context.Inputs, "v2", DeploymentIntents.Encode(prior), DeploymentIntents.Encode(receipt)));
    }
}
