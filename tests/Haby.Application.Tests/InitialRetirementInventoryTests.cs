using Haby.Application;
using Xunit;

namespace Haby.Application.Tests;

public sealed class InitialRetirementInventoryTests
{
    private static InitialRetirementDocument Valid() => new(1, "Initial", null, [],
        [new("Workload/api/main", "sample.workload/v1", Guid.NewGuid(), 1, new string('A', 64), [], false)], [], []);

    [Fact]
    public void Inventory_is_defensively_frozen_and_part_of_the_graph_fingerprint_bytes()
    {
        var document = Valid(); var inventory = new InitialRetirementInventory(document);
        var graph = new DurableOperationGraph([]).WithRetirement(inventory);
        var encoded = DurableOperationGraphCodec.Encode(graph); var hash = inventory.Fingerprint;
        document.Targets[0] = document.Targets[0] with { Documents = ["other"], RuntimeIdentity = true };
        inventory.Document.Targets[0] = document.Targets[0];
        Assert.Equal(hash, inventory.Fingerprint); Assert.Equal(encoded, DurableOperationGraphCodec.Encode(graph));
        Assert.Equal(hash, DurableOperationGraphCodec.Decode(encoded).Retirement!.Fingerprint);
        Assert.NotEqual(encoded, DurableOperationGraphCodec.Encode(new([])));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("shape")]
    [InlineData("missing-targets")]
    [InlineData("duplicate-target")]
    [InlineData("missing-obsolete")]
    [InlineData("baseline-without-history")]
    public void Incomplete_or_unknown_inventory_cannot_be_accepted(string fault)
    {
        var valid = Valid();
        var invalid = fault switch
        {
            "version" => valid with { Version = 2 },
            "shape" => valid with { Shape = "Update" },
            "missing-targets" => valid with { Targets = [] },
            "duplicate-target" => valid with { Targets = [valid.Targets[0], valid.Targets[0]] },
            "missing-obsolete" => valid with { ObsoleteObjects = null! },
            "baseline-without-history" => valid with { Shape = "ResourceBaseline", PriorApplied = 1 },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<InvalidDataException>(() => new InitialRetirementInventory(invalid));
    }
}
