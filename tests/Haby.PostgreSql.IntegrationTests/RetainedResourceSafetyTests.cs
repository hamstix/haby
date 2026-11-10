using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    private sealed class WithoutRetentionPolicy(IDurableActionAdmissionHandler? handler = null) : IDurableActionAdmissionHandler, IDurableResourcePlanComposer, IDurableResourceReusePolicy
    {
        private readonly IDurableActionAdmissionHandler inner = handler ?? new PostgreSqlDatabaseCreateAdmission();
        public DurableActionExecutionKey Key => inner.Key;
        public bool Validate(NormalizedResource resource, DurablePlanningInputs inputs) => inner.Validate(resource, inputs);
        public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resource, NormalizedResource value, DurablePlanningInputs inputs) => inner.Plan(resource, value, inputs);
        public bool Validate(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => ((IDurableResourcePlanComposer)inner).Validate(spec, resource, inputs, configuration);
        public IReadOnlyList<DurableOperationNode> Plan(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => ((IDurableResourcePlanComposer)inner).Plan(spec, resource, inputs, configuration);
        public bool ValidateGraph(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration, DurableOperationGraph graph) => ((IDurableResourcePlanComposer)inner).ValidateGraph(spec, resource, inputs, configuration, graph);
        public bool CanReuse(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => ((IDurableResourceReusePolicy)inner).CanReuse(accepted, target, resource, inputs, configuration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_execute_requires_registered_retention_policy_and_original_creation_support(bool removeRegistration)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var name = "retain_policy_" + Guid.NewGuid().ToString("N");
        await using (var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            await EstablishRetained(scenario, client, name, 2);
            var plan = await UpdatePlan(client, await ImportUpdate(client, UpdateEnvelope(scenario, 3)), 2);
            Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
            await host.StopAsync();
        }
        var history = await ReuseHistory(scenario.Connection);
        await using var restarted = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
        {
            foreach (var registration in services.Where(x => x.ServiceType == typeof(IDurableActionAdmissionHandler) && x.ImplementationType == typeof(PostgreSqlDatabaseCreateAdmission)).ToArray()) services.Remove(registration);
            if (!removeRegistration) services.AddSingleton<IDurableActionAdmissionHandler>(new WithoutRetentionPolicy());
        });
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(restarted);
        using var restartedChannel = GrpcChannel.ForAddress(restarted.Urls.Single());
        var result = await OperatorTestCredentials.Client(restartedChannel).ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 3 });
        Assert.Equal(GenerationExecutionStatus.InputUnavailable, result.Status); Assert.Equal(2, result.Progress.AppliedGeneration);
        Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.NotNull(await DatabaseOid(name));
        await restarted.StopAsync();
    }

    private async Task EstablishRetained(Scenario scenario, ManifestOperator.ManifestOperatorClient client, string name, int through = 3, bool pair = false)
    {
        for (var generation = 2; generation <= through; generation++)
        {
            var plan = await RetentionPlan(scenario, client, name, generation, generation - 1, pair, generation == 2);
            Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
            Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = generation })).Status);
        }
    }

    [Theory]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("properties")]
    [InlineData("provider")]
    [InlineData("provider-revision")]
    [InlineData("delete")]
    [InlineData("different-key")]
    [InlineData("pair-removal")]
    [InlineData("all-removal")]
    [InlineData("dangling-reference")]
    public async Task Retained_incompatible_reintroductions_and_name_claims_refuse_without_acceptance(string mutation)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var name = "retain_refuse_" + Guid.NewGuid().ToString("N");
        await EstablishRetained(scenario, client, name);
        if (mutation == "provider-revision")
        {
            var inputs = scenario.Plan.Inputs; var providers = host.Services.GetRequiredService<IProviderConfigurationStore>();
            var original = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(inputs.Environment, inputs.ProviderInstance, inputs.ConfigurationRevision, default))!;
            await providers.AddRevisionAsync(inputs.Environment, inputs.ProviderInstance, ProviderConfigurationRevisionNumber.Create(2), original.Configuration, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await host.Services.GetRequiredService<IProviderAliasStore>().ChangeAsync(inputs.Environment, inputs.Alias,
                new(inputs.ProviderInstance, inputs.ConfigurationRevision), new(inputs.ProviderInstance, ProviderConfigurationRevisionNumber.Create(2)), "tester", default)).Outcome);
        }
        var envelope = MixedEnvelope(scenario, 4, name); var spec = envelope["manifest"]!["spec"]!;
        switch (mutation)
        {
            case "name": spec["resources"]!["extra"]!["properties"]!["name"] = name + "_new"; break;
            case "type": spec["resources"]!["extra"] = spec["resources"]!["app-login"]!.DeepClone(); break;
            case "properties": spec["resources"]!["extra"]!["properties"]!["unknown"] = true; break;
            case "provider": spec["resources"]!["extra"]!["providerRef"] = "another.provider"; break;
            case "delete": spec["resources"]!["extra"]!["lifecycle"] = JsonNode.Parse("""{"deletionPolicy":"delete"}"""u8); break;
            case "different-key": spec["resources"]!["other"] = spec["resources"]!["extra"]!.DeepClone(); spec["resources"]!.AsObject().Remove("extra"); break;
            case "pair-removal": spec["resources"]!.AsObject().Remove("app-login"); spec["components"]!.AsObject().Clear(); break;
            case "all-removal": spec["resources"]!.AsObject().Clear(); spec["components"]!.AsObject().Clear(); break;
            case "dangling-reference": spec["resources"]!.AsObject().Remove("db"); break;
        }
        var history = await ReuseHistory(scenario.Connection); var oid = await DatabaseOid(name);
        var import = await client.ImportAsync(new() { OriginalEnvelope = Google.Protobuf.ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(envelope)) });
        var revision = import.Created?.Revision.Reference ?? import.ExistingEquivalent?.Revision.Reference;
        if (mutation is "properties" or "dangling-reference")
        {
            Assert.Equal(ImportReply.OutcomeOneofCase.InvalidManifest, import.OutcomeCase);
            Assert.Null(revision);
        }
        else
        {
            Assert.NotNull(revision);
            var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = 3 });
            var plan = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
            Assert.Null(plan.Plan); Assert.Equal(PlanningStatus.UnsupportedShape, plan.Status);
        }
        Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.Equal(oid, await DatabaseOid(name));
        await using var verify = Context(scenario.Connection);
        Assert.Equal(3, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
        Assert.Equal(3, await verify.DeploymentGenerations.CountAsync());
        await host.StopAsync();
    }

    [Theory]
    [InlineData("reservation", false)]
    [InlineData("reservation", true)]
    [InlineData("missing-source", false)]
    [InlineData("missing-source", true)]
    [InlineData("missing-management", false)]
    [InlineData("foreign-management", false)]
    [InlineData("missing-output", false)]
    [InlineData("corrupt-output", false)]
    [InlineData("missing-management", true)]
    [InlineData("foreign-management", true)]
    [InlineData("missing-output", true)]
    [InlineData("corrupt-output", true)]
    public async Task Retained_original_evidence_refuses_planning_and_execute_without_fallback(string damage, bool accepted)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var name = "retain_evidence_" + Guid.NewGuid().ToString("N");
        await EstablishRetained(scenario, client, name);
        var revision = await ImportUpdate(client, MixedEnvelope(scenario, 4, name));
        if (accepted)
        {
            var plan = await UpdatePlan(client, revision, 3, added: true);
            Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
        }
        await Sql(scenario.Connection, "SET session_replication_role = replica; " + (damage switch
        {
            "reservation" => "DELETE FROM provider_resource_reservations",
            "missing-source" => "UPDATE resource_management SET source_generation = 999 WHERE resource_local_id = 'extra'",
            "missing-management" => "DELETE FROM resource_management WHERE resource_local_id = 'extra'",
            "foreign-management" => "UPDATE resource_management SET resource_local_id = 'foreign' WHERE resource_local_id = 'extra'",
            "missing-output" => "DELETE FROM operation_outputs WHERE generation = 2",
            _ => "UPDATE operation_outputs SET payload = '{}' WHERE generation = 2"
        }));
        var history = await ReuseHistory(scenario.Connection); var oid = await DatabaseOid(name);
        if (accepted)
        {
            var result = await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 4 });
            Assert.Equal(GenerationExecutionStatus.InputUnavailable, result.Status); Assert.Equal(3, result.Progress.AppliedGeneration);
        }
        else
        {
            var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = 3 });
            Assert.Equal(PlanningStatus.PlanInputUnavailable, (await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId })).Status);
        }
        Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.Equal(oid, await DatabaseOid(name));
        await host.StopAsync();
    }
}
