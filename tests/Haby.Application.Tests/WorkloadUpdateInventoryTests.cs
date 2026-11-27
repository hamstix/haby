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
    [Fact]
    public void Corrective_inventory_cannot_promote_unconfirmed_members_or_mutate_saved_authority()
    {
        var deployment = Guid.CreateVersion7(); var checkpoint = Guid.CreateVersion7(); var fingerprint = "v6:" + new string('A', 64);
        var proof = ResourcesReadyCheckpointCodec.Encode(new(checkpoint, deployment, 1, fingerprint, DateTimeOffset.UtcNow, []));
        var member = new CorrectiveMember("api", "main", "NeverConfirmedInitialCreate", null, null, null, null, null, null, null,
            new("registry.example/app:bad", "registry.example/app:fixed"), ["settings"]);
        var document = new CorrectiveDocument(1, deployment, 1, null, Guid.CreateVersion7(), Guid.CreateVersion7(), fingerprint,
            checkpoint, proof, [member], [], [], [], [], []);
        var inventory = new CorrectiveInventory(document);
        var graph = new DurableOperationGraph([], baseline: new(ApplicationDeploymentId.Parse(deployment.ToString()), 1, fingerprint)).WithCorrection(inventory);
        var encoded = DurableOperationGraphCodec.Encode(graph);
        member.Documents[0] = "changed"; inventory.Document.Members[0].Documents[0] = "changed-again";
        var read = DurableOperationGraphCodec.Decode(encoded);
        Assert.Null(read.WorkloadUpdate); Assert.Null(read.Retirement);
        Assert.Equal("settings", Assert.Single(read.Correction!.Document.Members).Documents[0]);
        Assert.Equal(inventory.Fingerprint, read.Correction.Fingerprint);
        Assert.Throws<InvalidDataException>(() => new CorrectiveInventory(document with { Members = [member with { State = "ConfirmedOwned" }] }));
        Assert.Throws<InvalidDataException>(() => new CorrectiveInventory(document with { Members = [member with { Confirmation = new("foreign-uid", "registry.example/app:bad") }] }));
        Assert.Throws<InvalidDataException>(() => new CorrectiveInventory(document with { ExpectedDesired = 2 }));
        Assert.Throws<InvalidDataException>(() => new CorrectiveInventory(document with { LastApplied = 1 }));
    }

}
