using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class DurableOperationGraphTests
{
    private static readonly DurablePlanningInputs Inputs = new(EnvironmentRecordId.New(), EnvironmentId.Create("pilot"),
        ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)), ProviderAlias.Create("sample.primary"),
        ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1), "sample.impl/v1");
    private static DurableOperationNode Node(string id, params string[] dependencies) => new(OperationId.Create(id),
        ManifestLocalId.Create("one-resource"), new(new(ResourceTypeId.Create("sample.resource/v1"),
            new("sample/v1", "sample.configuration/v1", "sample.impl/v1")), OperationContractId.Create("sample.action/v1")),
        new(Inputs.Environment, Inputs.ProviderInstance, Inputs.ConfigurationRevision, Inputs.ImplementationId),
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
        Assert.Throws<InvalidDataException>(() => graph.ValidateReferences(Inputs with { ProviderInstance = ProviderInstanceId.New() }));
    }
}
