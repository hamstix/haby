using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.PostgreSql;
using Xunit;
using static Haby.PostgreSql.IntegrationTests.PostgreSqlPairPlanningTests;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class RetainedPairPlanningTests
{
    [Fact]
    public void Pair_retention_does_not_require_an_unrelated_original_standalone_to_remain_declared()
    {
        var envelope = IndependentPairPlanningTests.TwoPairs();
        envelope["manifest"]!["spec"]!["resources"]!["extra"] = new JsonObject
        {
            ["type"] = "postgresql.database/v1alpha1",
            ["providerRef"] = "postgresql.primary",
            ["properties"] = new JsonObject { ["name"] = "independent_standalone" }
        };
        var original = Spec(envelope); var target = Spec(Example()); var admission = PairAdmission();
        foreach (var id in new[] { "db-b", "login-b", "extra" })
            Assert.True(admission.CanRetainResource(original, target, ManifestLocalId.Create(id), Provider, Inputs(), PostgreSqlPairPlanningTests.Configuration()));
    }

    [Fact]
    public void Pair_retention_still_refuses_removal_of_another_original_pair()
    {
        var envelope = IndependentPairPlanningTests.TwoPairs(); var resources = envelope["manifest"]!["spec"]!["resources"]!;
        resources["db-c"] = resources["db-b"]!.DeepClone(); resources["db-c"]!["properties"]!["name"] = "sample_c";
        resources["login-c"] = resources["login-b"]!.DeepClone(); resources["login-c"]!["properties"]!["databaseRef"] = "db-c";
        var original = Spec(envelope); var target = Spec(Example()); var admission = PairAdmission();
        foreach (var id in new[] { "db-b", "login-b", "db-c", "login-c" })
            Assert.False(admission.CanRetainResource(original, target, ManifestLocalId.Create(id), Provider, Inputs(), PostgreSqlPairPlanningTests.Configuration()));
    }

    [Fact]
    public void Complete_independent_pair_retains_and_returns_through_exact_adapter_policies()
    {
        var original = Spec(IndependentPairPlanningTests.TwoPairs()); var target = Spec(Example());
        var admission = PairAdmission(); var inputs = Inputs(); var configuration = PostgreSqlPairPlanningTests.Configuration();
        var retained = original.Resources.Keys.Where(k => !target.Resources.ContainsKey(k)).Select(k =>
            new ResourceReuse(k, Guid.NewGuid(), 2, new string('B', 64))).ToArray();
        Assert.Equal(2, retained.Length);
        foreach (var r in retained)
        {
            Assert.True(admission.CanRetainResource(original, target, r.Resource, Provider, inputs, configuration));
            Assert.True(admission.CanReuseResource(original, original, r.Resource, Provider, inputs, configuration));
        }
        var active = target.Resources.Keys.Select(k => new ResourceReuse(k, Guid.NewGuid(), 1, new string('A', 64))).ToArray();
        var graph = admission.PlanUpdate(target, Provider, inputs, configuration, active,
            new(ApplicationDeploymentId.New(), 2, "v2:" + new string('A', 64)), retained);
        Assert.Empty(graph.Nodes); Assert.Equal(2, graph.RetainedResources.Count);
        Assert.Equal(graph, DurableOperationGraphCodec.Decode(DurableOperationGraphCodec.Encode(graph)));
    }

    [Theory]
    [InlineData("login-only")]
    [InlineData("renamed-login")]
    [InlineData("renamed-database")]
    [InlineData("foreign-owner")]
    [InlineData("changed-peer")]
    [InlineData("delete-peer")]
    [InlineData("all-removed")]
    public void Pair_closure_refuses_partial_removal_return_changed_peers_and_name_claims(string fault)
    {
        var envelope = IndependentPairPlanningTests.TwoPairs(); var original = Spec(envelope);
        var spec = envelope["manifest"]!["spec"]!; var resources = spec["resources"]!.AsObject();
        spec["components"]!.AsObject().Remove("consumer-b");
        switch (fault)
        {
            case "login-only": resources.Remove("login-b"); break;
            case "database-only": resources.Remove("db-b"); break;
            case "renamed-login": resources["other-login"] = resources["login-b"]!.DeepClone(); resources.Remove("login-b"); break;
            case "renamed-database": resources["other-db"] = resources["db-b"]!.DeepClone(); resources.Remove("db-b"); resources["login-b"]!["properties"]!["databaseRef"] = "other-db"; break;
            case "foreign-owner": resources["login-b"]!["properties"]!["databaseRef"] = "db"; break;
            case "changed-peer": resources["login-b"]!["properties"]!["schema"] = "changed"; break;
            case "delete-peer": resources["login-b"]!["lifecycle"] = new JsonObject { ["deletionPolicy"] = "delete" }; break;
            case "all-removed": resources.Clear(); spec["components"]!.AsObject().Clear(); break;
        }
        var target = Spec(envelope); var admission = PairAdmission(); var inputs = Inputs(); var configuration = PostgreSqlPairPlanningTests.Configuration();
        Assert.False(new[] { "db-b", "login-b" }.All(id => target.Resources.ContainsKey(ManifestLocalId.Create(id))
            ? admission.CanReuseResource(original, target, ManifestLocalId.Create(id), Provider, inputs, configuration)
            : admission.CanRetainResource(original, target, ManifestLocalId.Create(id), Provider, inputs, configuration)));
        // The unchanged database cannot authorize changed ownership or a changed peer.
        if (fault is "changed-peer" or "delete-peer" or "renamed-login")
            Assert.False(admission.CanReuseResource(original, target, ManifestLocalId.Create("db-b"), Provider, inputs, configuration));
    }

    private static DurableActionAdmission PairAdmission() => new(new(new IDurableActionAdmissionHandler[]
        { new PostgreSqlDatabaseCreateAdmission(), new PostgreSqlPairResourceAdmission(PostgreSqlApplicationLoginContract.Id) }, []));
}
