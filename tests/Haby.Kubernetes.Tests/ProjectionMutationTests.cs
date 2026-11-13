using System.Text.Json.Nodes;
using Haby.Kubernetes;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class ProjectionMutationTests
{
    [Fact]
    public void K04_patch_applied_in_RFC6902_order_preserves_injected_fields_and_confirms_target()
    {
        var old = KernelTests.Intent(); var obj = KernelTests.Object(old);
        var spec = obj["spec"]!["template"]!["spec"]!;
        spec["containers"]!.AsArray().Insert(0, new JsonObject { ["name"] = "injected", ["image"] = "sidecar:v1" });
        spec["nodeSelector"]!["injected"] = "preserve";
        spec["imagePullSecrets"]!.AsArray().Insert(0, new JsonObject { ["name"] = "injected" });
        obj["metadata"]!["annotations"]!["unrelated"] = "preserve";
        var i = old with
        {
            ExpectedUid = "saved-uid",
            LastConfirmed = old.Target,
            TargetGeneration = 2,
            Operation = Guid.NewGuid(),
            Target = old.Target with { Image = "registry.example/app:v2", Replicas = 2, NodeSelector = old.Target.NodeSelector.Clear(), ImagePullSecrets = [] }
        };
        DeploymentObjects.Matches(obj, old.Target);
        var patch = DeploymentObjects.Patch(obj, i);
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
            else
            {
                if (op == "test") Assert.True(JsonNode.DeepEquals(parent[key], value));
                else if (op == "remove") Assert.True(parent.AsObject().Remove(key));
                else { Assert.Equal("add", op); parent[key] = value; }
            }
        }
        Assert.Equal("sidecar:v1", spec["containers"]![0]!["image"]!.GetValue<string>());
        Assert.Equal("app", spec["containers"]![1]!["name"]!.GetValue<string>());
        Assert.Equal("registry.example/app:v2", spec["containers"]![1]!["image"]!.GetValue<string>());
        Assert.Equal("preserve", spec["nodeSelector"]!["injected"]!.GetValue<string>());
        Assert.Single(spec["nodeSelector"]!.AsObject()); Assert.Equal("injected", Assert.Single(spec["imagePullSecrets"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.Equal("preserve", obj["metadata"]!["annotations"]!["unrelated"]!.GetValue<string>());
        Assert.Equal(2, obj["spec"]!["replicas"]!.GetValue<int>());
        Assert.Equal(i.Operation, DeploymentObjects.Receipt(obj, i, ConfirmationSource.Response).Operation);
    }
}
