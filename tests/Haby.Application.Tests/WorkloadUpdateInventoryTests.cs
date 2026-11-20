using Haby.Application;
using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class WorkloadUpdateInventoryTests
{
    private static WorkloadUpdateDocument Valid() => new(1, Guid.CreateVersion7(), 1, 2, Guid.CreateVersion7(), Guid.CreateVersion7(),
        "v6:" + new string('A', 64), Guid.CreateVersion7(), new string('B', 64),
        [new("api", "main", Guid.CreateVersion7(), "sample.target/v1", 1, "{}", "original", Guid.CreateVersion7(), null,
            new("sample.delta/v1", "{}"), ["settings"])], [], [], [], []);

    [Fact]
    public void Update_inventory_roundtrip_is_defensive_and_distinct_from_initial_retirement_authority()
    {
        var document = Valid(); var inventory = new WorkloadUpdateInventory(document);
        var baseline = new ResourceReuseBaseline(ApplicationDeploymentId.Parse(document.Deployment.ToString()), 1, document.Fingerprint);
        var graph = new DurableOperationGraph([], baseline: baseline).WithWorkloadUpdate(inventory);
        var encoded = DurableOperationGraphCodec.Encode(graph);
        document.Members[0].PrepareDocuments[0] = "changed";
        inventory.Document.Members[0].PrepareDocuments[0] = "changed-again";
        var loaded = DurableOperationGraphCodec.Decode(encoded);
        Assert.Null(loaded.Retirement);
        Assert.Equal(inventory.Fingerprint, loaded.WorkloadUpdate!.Fingerprint);
        Assert.Equal("settings", Assert.Single(Assert.Single(loaded.WorkloadUpdate.Document.Members).PrepareDocuments));
        Assert.Equal(encoded, DurableOperationGraphCodec.Encode(graph));
        Assert.NotEqual(encoded, DurableOperationGraphCodec.Encode(new([], baseline: baseline)));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("baseline")]
    [InlineData("target")]
    [InlineData("completion")]
    [InlineData("members")]
    [InlineData("duplicate")]
    [InlineData("publications")]
    public void Incomplete_or_unsupported_update_inventory_is_refused(string fault)
    {
        var document = Valid();
        document = fault switch
        {
            "version" => document with { Version = 2 },
            "baseline" => document with { BaselineGeneration = 0 },
            "target" => document with { TargetGeneration = 3 },
            "completion" => document with { CompletionHash = "unproven" },
            "members" => document with { Members = [] },
            "duplicate" => document with { Members = [document.Members[0], document.Members[0]] },
            "publications" => document with { ObsoletePublications = null! },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<InvalidDataException>(() => new WorkloadUpdateInventory(document));
    }
}
