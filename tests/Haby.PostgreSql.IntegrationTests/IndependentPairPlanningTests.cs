using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.PostgreSql;
using Xunit;
using static Haby.PostgreSql.IntegrationTests.PostgreSqlPairPlanningTests;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class IndependentPairPlanningTests
{
    internal static JsonObject TwoPairs()
    {
        var envelope = Example();
        var spec = envelope["manifest"]!["spec"]!;
        spec["resources"]!["db-b"] = spec["resources"]!["db"]!.DeepClone();
        spec["resources"]!["db-b"]!["properties"]!["name"] = "sample_b";
        spec["resources"]!["login-b"] = spec["resources"]!["app-login"]!.DeepClone();
        spec["resources"]!["login-b"]!["properties"]!["databaseRef"] = "db-b";
        spec["components"]!["consumer-b"] = spec["components"]!["worker"]!.DeepClone();
        spec["components"]!["consumer-b"]!["bindings"]!["storage"]!["target"]!["resourceRef"] = "login-b";
        return envelope;
    }

    [Theory]
    [InlineData("existing-db")]
    [InlineData("two-owners")]
    [InlineData("duplicate-name")]
    [InlineData("two-pairs")]
    public void Only_one_new_independent_pair_is_admitted(string fault)
    {
        var accepted = Spec(Example()); var envelope = TwoPairs(); var resources = envelope["manifest"]!["spec"]!["resources"]!;
        if (fault == "existing-db") { resources.AsObject().Remove("db-b"); resources["login-b"]!["properties"]!["databaseRef"] = "db"; }
        if (fault == "two-owners") { resources["third-login"] = resources["login-b"]!.DeepClone(); }
        if (fault == "duplicate-name") resources["db-b"]!["properties"]!["name"] = resources["db"]!["properties"]!["name"]!.DeepClone();
        if (fault == "two-pairs")
        {
            resources["db-c"] = resources["db-b"]!.DeepClone(); resources["db-c"]!["properties"]!["name"] = "sample_c";
            resources["login-c"] = resources["login-b"]!.DeepClone(); resources["login-c"]!["properties"]!["databaseRef"] = "db-c";
        }
        var admission = new DurableActionAdmission(new(new IDurableActionAdmissionHandler[]
            { new PostgreSqlDatabaseCreateAdmission(), new PostgreSqlPairResourceAdmission(PostgreSqlApplicationLoginContract.Id) }, []));
        Assert.False(admission.CanReuse(accepted, Spec(envelope), Provider, Inputs(), PostgreSqlPairPlanningTests.Configuration()));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("database")]
    [InlineData("extra-node")]
    [InlineData("missing-node")]
    [InlineData("action")]
    public void Complete_new_pair_graph_rejects_crossed_missing_foreign_and_extra_nodes(string fault)
    {
        var inputs = Inputs(TwoPairs()); var config = PostgreSqlPairPlanningTests.Configuration(); var target = Spec(TwoPairs());
        var admission = new DurableActionAdmission(new(new IDurableActionAdmissionHandler[]
            { new PostgreSqlDatabaseCreateAdmission(), new PostgreSqlPairResourceAdmission(PostgreSqlApplicationLoginContract.Id) }, []));
        var reuse = Spec(Example()).Resources.Keys.Select(r => new ResourceReuse(r, Guid.NewGuid(), 1, new string('B', 64))).ToArray();
        var graph = admission.PlanUpdate(target, Provider, inputs, config, reuse, new(ApplicationDeploymentId.New(), 1, "v2:" + new string('A', 64)));
        var nodes = graph.Nodes.ToList(); var access = nodes.Single(n => n.Id.Value == "login-b-access");
        if (fault == "missing-node") nodes.Remove(access);
        else if (fault == "extra-node") nodes.Add(access with { Id = OperationId.Create("unexpected") });
        else nodes[nodes.IndexOf(access)] = fault == "action" ? access with { Action = access.Action with { ActionContract = OperationContractId.Create("foreign/v1") } } :
            access with { Input = access.Input with { Json = access.Input.Json.Replace(fault == "password" ? "login-b-material" : "db-b-create", fault == "password" ? "app-login-material" : "db-create", StringComparison.Ordinal) } };
        var corrupt = new DurableOperationGraph(nodes, graph.ReusedResources, graph.Baseline);
        Assert.False(new PostgreSqlPairResourceAdmission(PostgreSqlApplicationLoginContract.Id).ValidateGraph(target, ManifestLocalId.Create("login-b"), inputs, config, corrupt));
    }

    [Fact]
    public void Ambiguous_description_producer_refuses_without_throwing_or_selecting_first()
    {
        var inputs = Inputs(); var spec = Spec(Example()); var config = PostgreSqlPairPlanningTests.Configuration();
        var graph = Admission().Plan(spec, Provider, inputs, config);
        graph = new DurableOperationGraph(graph.Nodes.Select(n => n.Id.Value == "db-create" ? n with { Action = PostgreSqlDatabaseRestrictAction.RegistrationKey } : n));
        Assert.Equal(PlanDescriptionStatus.UnsupportedShape, PostgreSqlPairResourceAdmission.DescribeGraph(spec, inputs, graph));
    }

    [Fact]
    public void Independent_pair_update_has_only_resource_scoped_five_step_chain_and_original_reuse()
    {
        var accepted = Spec(Example()); var target = Spec(TwoPairs());
        var inputs = Inputs(TwoPairs()); var configuration = PostgreSqlPairPlanningTests.Configuration();
        var admission = new DurableActionAdmission(new(new IDurableActionAdmissionHandler[]
            { new PostgreSqlDatabaseCreateAdmission(), new PostgreSqlPairResourceAdmission(PostgreSqlApplicationLoginContract.Id) }, []));
        Assert.True(admission.CanReuse(accepted, target, Provider, inputs, configuration));
        var reuse = accepted.Resources.Keys.Select(r => new ResourceReuse(r, Guid.NewGuid(), 1, new string('B', 64))).ToArray();
        var graph = admission.PlanUpdate(target, Provider, inputs, configuration, reuse,
            new(ApplicationDeploymentId.New(), 1, "v2:" + new string('A', 64)));
        Assert.Equal(new[] { "db-b-create", "db-b-restrict", "login-b-access", "login-b-create", "login-b-material" }, graph.Nodes.Select(n => n.Id.Value));
        Assert.Equal(2, graph.ReusedResources.Count);
        Assert.True(admission.ValidateCreationGraph(target, Provider, inputs, configuration, graph));
        foreach (var n in graph.Nodes)
        {
            Assert.Contains(n.ResourceId.Value, new[] { "db-b", "login-b" });
            Assert.All(n.Dependencies, d => Assert.Contains(d, graph.Nodes.Select(x => x.Id)));
            Assert.All(n.OutputReferences, o => Assert.Contains(o.ProducerOperationId, graph.Nodes.Select(x => x.Id)));
        }
        var material = GeneratedCredentialInputCodec.Decode(graph.Nodes.Single(n => n.Id.Value == "login-b-material").Input)!;
        Assert.Equal(new ManagedResourceKey(inputs.Environment, inputs.Revision.ApplicationRecordId, ManifestLocalId.Create("login-b")), material.Owner);
        Assert.Equal(graph, DurableOperationGraphCodec.Decode(DurableOperationGraphCodec.Encode(graph)));
    }
}
