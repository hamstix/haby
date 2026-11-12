using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class DurableOperationGraphTests
{
    [Fact]
    public void Retained_membership_and_reintroduction_are_immutable_distinct_direct_source_requirements()
    {
        var baseline = new ResourceReuseBaseline(ApplicationDeploymentId.New(), 4, "v2:" + new string('A', 64));
        var original = new ResourceReuse(ManifestLocalId.Create("extra"), Guid.CreateVersion7(), 2, new string('B', 64));
        var retained = new DurableOperationGraph([], baseline: baseline, retainedResources: [original]);
        Assert.Equal(retained, DurableOperationGraphCodec.Decode(DurableOperationGraphCodec.Encode(retained)));
        Assert.Empty(retained.Nodes); Assert.Empty(retained.ReusedResources);
        Assert.Equal(original, Assert.Single(retained.RetainedResources));
        var reintroduced = new DurableOperationGraph([], [original with { Reintroduced = true }], baseline);
        Assert.Equal(reintroduced, DurableOperationGraphCodec.Decode(DurableOperationGraphCodec.Encode(reintroduced)));
        var empty = Haby.Tests.DurableInputSamples.Create(Inputs.Environment, Inputs.EnvironmentKey, Inputs.Revision,
            Inputs.Providers.RequireUniformResourceProvider().Alias, Inputs.Providers.RequireUniformResourceProvider().Instance,
            Inputs.Providers.RequireUniformResourceProvider().Configuration, Inputs.Providers.RequireUniformResourceProvider().Implementation.Value);
        var active = Haby.Tests.DurableInputSamples.Create(Inputs.Environment, Inputs.EnvironmentKey, Inputs.Revision,
            Inputs.Providers.RequireUniformResourceProvider().Alias, Inputs.Providers.RequireUniformResourceProvider().Instance,
            Inputs.Providers.RequireUniformResourceProvider().Configuration, Inputs.Providers.RequireUniformResourceProvider().Implementation.Value, "extra");
        Assert.NotEqual(DurableOperationGraphCodec.Fingerprint(empty, retained), DurableOperationGraphCodec.Fingerprint(active, reintroduced));
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([], [original], baseline, [original]));
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([], baseline: baseline, retainedResources: [original with { Reintroduced = true }]));
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([], retainedResources: [original]));
    }

    [Fact]
    public void Mixed_graph_roundtrips_new_work_and_direct_original_reuse_without_overlapping_owners()
    {
        var reused = new ResourceReuse(ManifestLocalId.Create("old-resource"), Guid.CreateVersion7(), 2, new string('B', 64));
        var baseline = new ResourceReuseBaseline(ApplicationDeploymentId.New(), 3, "v2:" + new string('A', 64));
        var graph = new DurableOperationGraph([Node("new-create")], [reused], baseline);
        Assert.Equal(graph, DurableOperationGraphCodec.Decode(DurableOperationGraphCodec.Encode(graph)));
        Assert.Single(graph.Nodes); Assert.Equal(2, Assert.Single(graph.ReusedResources).SourceGeneration);
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([Node("fake-create") with { ResourceId = reused.Resource }], [reused], baseline));
    }

    [Fact]
    public void Reuse_is_separate_from_external_work_and_canonically_pins_original_source_and_baseline()
    {
        var baseline = new ResourceReuseBaseline(ApplicationDeploymentId.New(), 2, "v2:" + new string('A', 64));
        var reused = new ResourceReuse(ManifestLocalId.Create("one-resource"), Guid.CreateVersion7(), 1, new string('B', 64));
        var graph = new DurableOperationGraph([], [reused], baseline);
        Assert.Empty(graph.Nodes); Assert.Equal(reused, Assert.Single(graph.ReusedResources));
        var bytes = DurableOperationGraphCodec.Encode(graph);
        Assert.Equal(graph, DurableOperationGraphCodec.Decode(bytes));
        Assert.NotEqual(DurableOperationGraphCodec.Fingerprint(Inputs, graph), DurableOperationGraphCodec.Fingerprint(Inputs,
            new([], [reused with { ManagementId = Guid.CreateVersion7() }], baseline)));
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([Node("a")], [reused], baseline));
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([], [reused], null));
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([], [reused, reused], baseline));
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([], [reused with { SourceGeneration = 3 }], baseline));
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph([], [reused with { EvidenceFingerprint = "corrupt" }], baseline));
    }

    private static readonly DurablePlanningInputs Inputs = Haby.Tests.DurableInputSamples.Create(EnvironmentRecordId.New(), EnvironmentId.Create("pilot"),
        ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)), ProviderAlias.Create("sample.primary"),
        ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1), "sample.impl/v1", "one-resource");
    private static DurableOperationNode Node(string id, params string[] dependencies) => new(OperationId.Create(id),
        ManifestLocalId.Create("one-resource"), new(new(ResourceTypeId.Create("sample.resource/v1"),
            new("sample/v1", "sample.configuration/v1", "sample.impl/v1")), OperationContractId.Create("sample.action/v1")),
        new(Inputs.Environment, Inputs.Providers.RequireUniformResourceProvider().Instance, Inputs.Providers.RequireUniformResourceProvider().Configuration, Inputs.Providers.RequireUniformResourceProvider().Implementation.Value),
        new("sample.input/v1", "{\"count\":3}"), dependencies.Select(OperationId.Create).ToArray());

    [Fact]
    public void Canonical_graph_and_fingerprint_ignore_node_and_dependency_declaration_order()
    {
        var a = new DurableOperationGraph([Node("c", "b", "a"), Node("b"), Node("a")]);
        var b = new DurableOperationGraph([Node("a"), Node("c", "a", "b"), Node("b")]);
        Assert.Equal(DurableOperationGraphCodec.Encode(a), DurableOperationGraphCodec.Encode(b));
        Assert.Equal(DurableOperationGraphCodec.Fingerprint(Inputs, a), DurableOperationGraphCodec.Fingerprint(Inputs, b));
        Assert.Equal(DurableOperationGraphCodec.Encode(a), DurableOperationGraphCodec.Encode(DurableOperationGraphCodec.Decode(DurableOperationGraphCodec.Encode(a))));
        var changed = new DurableOperationGraph([Node("a") with { Input = new("sample.input/v1", "{\"count\":4}") }, Node("b"), Node("c", "a", "b")]);
        Assert.NotEqual(DurableOperationGraphCodec.Fingerprint(Inputs, a), DurableOperationGraphCodec.Fingerprint(Inputs, changed));
    }

    [Fact]
    public void Graph_copies_collections_and_separates_step_identity_from_resource_ownership()
    {
        var dependencies = new[] { OperationId.Create("a") };
        var nodes = new[] { Node("a"), Node("b") with { Dependencies = dependencies } };
        var graph = new DurableOperationGraph(nodes);
        var encoded = DurableOperationGraphCodec.Encode(graph);
        dependencies[0] = OperationId.Create("other"); nodes[0] = Node("other");
        Assert.Equal(encoded, DurableOperationGraphCodec.Encode(graph));
        Assert.Single(graph.Nodes.Select(n => n.ResourceId).Distinct());
        Assert.Equal(2, graph.Nodes.Select(n => n.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("dependency-duplicate")]
    [InlineData("missing")]
    [InlineData("cycle")]
    [InlineData("self")]
    [InlineData("empty")]
    public void Invalid_graphs_are_rejected_before_persistence(string invalid)
    {
        var nodes = invalid switch
        {
            "duplicate" => new[] { Node("a"), Node("a") },
            "dependency-duplicate" => [Node("a"), Node("b", "a", "a")],
            "missing" => [Node("a", "missing")],
            "cycle" => [Node("a", "b"), Node("b", "a")],
            "self" => [Node("a", "a")],
            _ => []
        };
        Assert.Throws<InvalidDataException>(() => new DurableOperationGraph(nodes));
    }

    [Fact]
    public void Unknown_current_format_and_inconsistent_exact_references_fail_closed()
    {
        Assert.Throws<InvalidDataException>(() => DurableOperationGraphCodec.Decode("{\"Version\":99,\"Nodes\":[]}"));
        Assert.Throws<InvalidDataException>(() => DurableOperationGraphCodec.Decode("{\"Version\":1,\"Nodes\":null}"));
        var graph = new DurableOperationGraph([Node("a")]);
        Assert.Throws<InvalidDataException>(() => graph.ValidateReferences(Inputs with { Providers = new(Inputs.Providers.Declaration, Inputs.Environment, Inputs.Providers.Entries.Values.Select(x => x with { Instance = ProviderInstanceId.New() })) }));
    }
}
