using Haby.Application;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ResourceReadinessProofTests
{
    [Fact]
    public void Empty_resource_proof_requires_zero_declarations_operations_and_reuse()
    {
        Assert.True(ResourceReadinessCoverage.IsComplete([], [], []));
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([]));
        Assert.False(ResourceReadinessCoverage.IsComplete(["database"], [], []));
        Assert.False(ResourceReadinessCoverage.IsComplete([], ["database"], []));
        Assert.False(ResourceReadinessCoverage.IsComplete([], [], ["database"]));
        Assert.False(ResourceReadinessCoverage.IsComplete(["database"], ["database"], ["database"]));
        Assert.False(ResourceReadinessCoverage.IsComplete(["database"], [], ["database", "database"]));
        Assert.True(ResourceReadinessCoverage.IsComplete(["database", "login"], ["login", "login"], ["database"]));
    }

    [Fact]
    public void Checkpoint_roundtrip_detaches_original_result_references_and_refuses_unknown_format()
    {
        var output = new ResourceOutputReference("sample.result/v1", "receipt", new string('B', 64));
        ResourceOutputReference[] outputs = [output];
        var operation = new ResourceOperationResult("create", Guid.CreateVersion7(), 1, null, outputs);
        ResourceOperationResult[] operations = [operation];
        var resource = new ResourceReadyResult("database", Guid.CreateVersion7(), 1, "v4:" + new string('A', 64), false, false, operations);
        ResourceReadyResult[] resources = [resource];
        var proof = new ResourcesReadyCheckpoint(Guid.CreateVersion7(), Guid.CreateVersion7(), 2,
            "v4:" + new string('C', 64), DateTimeOffset.UtcNow, resources);
        var encoded = ResourcesReadyCheckpointCodec.Encode(proof);
        outputs[0] = output with { Key = "changed" };
        operations[0] = new("unrelated", Guid.CreateVersion7(), 2, null, []);
        resources[0] = new("other", Guid.CreateVersion7(), 2, "v4:" + new string('D', 64), false, false, []);
        Assert.Equal(encoded, ResourcesReadyCheckpointCodec.Encode(proof));
        var decoded = ResourcesReadyCheckpointCodec.Decode(encoded);
        Assert.Equal(encoded, ResourcesReadyCheckpointCodec.Encode(decoded));
        Assert.Equal("receipt", Assert.Single(Assert.Single(Assert.Single(decoded.Resources).Operations).Outputs).Key);
        Assert.Throws<InvalidDataException>(() => ResourcesReadyCheckpointCodec.Decode(encoded.Replace("\"Version\":1", "\"Version\":99", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => ResourcesReadyCheckpointCodec.Decode(encoded.Replace("ResourcesReady", "RolloutReady", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => ResourcesReadyCheckpointCodec.Decode(encoded[..^1] + ",\"unknown\":true}"));
    }
}
