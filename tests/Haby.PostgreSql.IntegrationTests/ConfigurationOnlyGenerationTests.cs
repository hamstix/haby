using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Google.Protobuf;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using AssignmentTarget = Haby.Operator.Contracts.ProfileAssignmentTarget;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    private static RevisionReference SameRevision(Scenario scenario) => new()
    {
        ApplicationRecordId = scenario.Plan.Inputs.Revision.ApplicationRecordId.ToString(),
        RevisionNumber = scenario.Plan.Inputs.Revision.RevisionNumber.Value
    };

    private static async Task<RevisionReference> SelectConfigurationRevision(ManifestOperator.ManifestOperatorClient client,
        Scenario scenario, int revision, Action<JsonNode>? configure = null)
    {
        var profile = JsonNode.Parse(RenderingProfile)!;
        profile["metadata"]!["revision"] = revision;
        profile["spec"]!["staticContributions"]!["logging"]!["value"]!["Level"] = "Debug";
        profile["spec"]!["bindingRules"]!["connection"]!["options"]!["commandTimeout"] = 40;
        configure?.Invoke(profile);
        Assert.Equal(ProfileStatus.Created, (await client.ImportProfileAsync(new()
        { Payload = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(profile.AsObject())) })).Status);
        var target = new AssignmentTarget { Kind = "environment" };
        var prior = await client.GetProfileAssignmentAsync(new() { Environment = "pilot", Target = target });
        var assignment = JsonNode.Parse("""
            {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfileAssignment","spec":{
            "target":{"kind":"environment"},"decisions":[{"kind":"use","profileRef":{"profileId":"connection-layout","revision":2}}]}}
            """u8)!;
        assignment["spec"]!["decisions"]![0]!["profileRef"]!["revision"] = revision;
        Assert.Equal(ProfileAssignmentStatus.Updated, (await client.ApplyProfileAssignmentAsync(new()
        {
            Environment = "pilot",
            ExpectedConcurrencyToken = prior.Assignment.ConcurrencyToken,
            Payload = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(assignment.AsObject()))
        })).Status);
        return SameRevision(scenario);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("application")]
    [InlineData("component")]
    public async Task Configuration_only_exact_scopes_stale_on_first_insertion_and_change(string scope)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var history = await ReuseHistory(scenario.Connection);
        var revision = await SelectConfigurationRevision(client, scenario, 2);
        var target = new JsonObject { ["kind"] = scope };
        if (scope != "environment") target["applicationId"] = DocumentRequest(scenario).ApplicationId;
        if (scope == "component") target["componentRef"] = "api";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var plan = await UpdatePlan(client, revision, 1);
            var current = await client.GetProfileAssignmentAsync(new()
            {
                Environment = "pilot",
                Target = new()
                {
                    Kind = scope,
                    ApplicationId = scope == "environment" ? "" : DocumentRequest(scenario).ApplicationId,
                    ComponentRef = scope == "component" ? "api" : ""
                }
            });
            var decisions = scope == "environment" ? new JsonArray(new JsonObject
            {
                ["kind"] = "use",
                ["profileRef"] = new JsonObject { ["profileId"] = "connection-layout", ["revision"] = 2 }
            }) : new JsonArray();
            if (attempt == 1 || scope == "environment") decisions.Add(new JsonObject { ["kind"] = "disable", ["profileId"] = "unused-" + attempt });
            var assignment = new JsonObject
            {
                ["apiVersion"] = "haby.dev/v1alpha1",
                ["kind"] = "ConfigurationProfileAssignment",
                ["spec"] = new JsonObject { ["target"] = target.DeepClone(), ["decisions"] = decisions }
            };
            var apply = new ApplyProfileAssignmentRequest
            {
                Environment = "pilot",
                Payload = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(assignment))
            };
            if (scope == "component") apply.ApplicationVersion = "v1";
            if (current.Assignment is null) apply.ExpectedAbsence = true; else apply.ExpectedConcurrencyToken = current.Assignment.ConcurrencyToken;
            Assert.Contains((await client.ApplyProfileAssignmentAsync(apply)).Status, new[] { ProfileAssignmentStatus.Created, ProfileAssignmentStatus.Updated });
            Assert.Equal(PlanningStatus.PlanStale, (await client.CheckPlanAsync(new() { PlanId = plan.PlanId })).Status);
            Assert.Equal(CommitStatus.PlanStale, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
        }
        Assert.Equal(history, await ReuseHistory(scenario.Connection));
        await using (var verify = Context(scenario.Connection)) Assert.Equal(1, await verify.DeploymentGenerations.CountAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task Configuration_only_competing_commits_have_one_lock_observed_atomic_winner()
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var revision = await SelectConfigurationRevision(client, scenario, 2);
        var first = await UpdatePlan(client, revision, 1); var second = await UpdatePlan(client, revision, 1);
        var history = await ReuseHistory(scenario.Connection);
        var lockKey = $"haby-deployment:{Guid.Parse(scenario.Plan.Inputs.Environment.ToString()):N}:{Guid.Parse(revision.ApplicationRecordId):N}";
        await using var blocker = new NpgsqlConnection(scenario.Connection); await blocker.OpenAsync(); await using var tx = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", blocker, tx))
        { hold.Parameters.AddWithValue(lockKey); await hold.ExecuteNonQueryAsync(); }
        var one = client.CommitDesiredStateAsync(new() { ChangeSetId = first.ChangeSetId, PlanId = first.PlanId }).ResponseAsync;
        var two = client.CommitDesiredStateAsync(new() { ChangeSetId = second.ChangeSetId, PlanId = second.PlanId }).ResponseAsync;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var observer = new NpgsqlConnection(scenario.Connection))
        {
            await observer.OpenAsync(deadline.Token);
            while (true)
            {
                await using var waiting = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())", observer);
                if ((long)(await waiting.ExecuteScalarAsync(deadline.Token))! >= 2) break;
                Assert.False(one.IsCompleted); Assert.False(two.IsCompleted); await Task.Delay(10, deadline.Token);
            }
        }
        await tx.CommitAsync();
        var results = await Task.WhenAll(one, two);
        Assert.Single(results, x => x.Status == CommitStatus.Committed); Assert.Single(results, x => x.Status == CommitStatus.ChangeSetStale);
        await using (var verify = Context(scenario.Connection))
        {
            Assert.Equal(2, await verify.DeploymentGenerations.CountAsync());
            var owner = await verify.ApplicationDeployments.AsNoTracking().SingleAsync(); Assert.Equal(2, owner.DesiredGeneration); Assert.Equal(1, owner.AppliedGeneration);
        }
        Assert.Equal(history, await ReuseHistory(scenario.Connection));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("options")]
    [InlineData("renderer")]
    [InlineData("collision")]
    [InlineData("secret")]
    public async Task Configuration_only_invalid_target_inputs_refuse_before_acceptance(string fault)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var history = await ReuseHistory(scenario.Connection);
        var revision = await SelectConfigurationRevision(client, scenario, 2, profile =>
        {
            var rule = profile["spec"]!["bindingRules"]!["connection"]!;
            switch (fault)
            {
                case "options": rule["options"]!["password"] = "must-not-appear"; break;
                case "renderer": rule["rendererRef"] = "missing.renderer/v1"; break;
                case "collision": rule["documentProjections"]![0]!["path"] = "/Logging"; break;
                case "secret": profile["spec"]!["staticContributions"]!["logging"]!["secretProjections"] = JsonNode.Parse("""
                    [{"path":"/Secret","format":"string","secretRef":{"name":"must-not-appear","version":1}}]
                    """u8); break;
            }
        });
        var change = await client.CreateChangeSetAsync(new()
        {
            EnvironmentId = "pilot",
            Reference = revision,
            Precondition = PlanningPrecondition.ExpectedGeneration,
            ExpectedGeneration = 1
        });
        var result = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
        Assert.Equal(PlanningStatus.ConfigurationRefused, result.Status); Assert.Null(result.Plan);
        Assert.DoesNotContain("must-not-appear", result.ToString());
        Assert.Equal(history, await ReuseHistory(scenario.Connection));
        await using (var verify = Context(scenario.Connection)) Assert.Equal(1, await verify.DeploymentGenerations.CountAsync());
        await host.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configuration_only_after_mixed_creation_and_retention_preserves_direct_sources_and_reintroduction(bool retained)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var name = "configuration_retained_" + Guid.NewGuid().ToString("N"); var baseline = retained ? 3 : 2;
        await EstablishRetained(scenario, client, name, baseline);
        var identity = await ExternalIdentity(scenario); var oid = await DatabaseOid(name); var history = await ReuseHistory(scenario.Connection);
        var revision = await SelectConfigurationRevision(client, scenario, 2); revision.RevisionNumber = baseline;
        var plan = await UpdatePlan(client, revision, baseline, added: !retained);
        Assert.Equal(retained ? 1 : 0, plan.RetainedResources.Count);
        if (retained) Assert.Equal(2, Assert.Single(plan.RetainedResources).SourceGeneration);
        var receipt = await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId });
        Assert.Equal(baseline + 1, receipt.Commit.Generation);
        Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new()
        { DeploymentId = receipt.Commit.DeploymentId, Generation = baseline + 1 })).Status);
        var batch = await client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario));
        Assert.Equal(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, batch.Status); Assert.Equal(2, batch.Items.Count);
        Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.Equal(identity, await ExternalIdentity(scenario)); Assert.Equal(oid, await DatabaseOid(name));
        if (retained)
        {
            var reintroduced = await UpdatePlan(client, await ImportUpdate(client, MixedEnvelope(scenario, 5, name)), 4, added: true);
            Assert.True(Assert.Single(reintroduced.ReusedResources, x => x.ResourceId == "extra").Reintroduced);
            Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = reintroduced.ChangeSetId, PlanId = reintroduced.PlanId })).Status);
            Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = receipt.Commit.DeploymentId, Generation = 5 })).Status);
            Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.Equal(oid, await DatabaseOid(name));
        }
        await host.StopAsync();
    }

    [Fact]
    public async Task Configuration_only_scope_override_then_inheritance_accepts_exact_inputs_and_repeats_as_noop()
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var revision = await SelectConfigurationRevision(client, scenario, 2);
        var environment = (await host.Services.GetRequiredService<IEnvironmentCatalog>().GetAsync(EnvironmentId.Create("pilot"), default))!;
        var assignments = host.Services.GetRequiredService<IProfileAssignmentStore>();
        var target = new Haby.Domain.ProfileAssignmentTarget.Component(Haby.Domain.ApplicationId.Create(DocumentRequest(scenario).ApplicationId), ManifestLocalId.Create("api"));
        var history = await ReuseHistory(scenario.Connection);
        for (var generation = 2; generation <= 3; generation++)
        {
            var prior = await assignments.FindAsync(environment, target, default);
            var decisions = generation == 2 ? new ProfileDecision[] { new ProfileDecision.Use(new ProfileRevisionRef(ProfileId.Create("connection-layout"), 1)) } : [];
            var result = await assignments.ApplyAsync(environment, new(target, decisions),
                prior is null ? new AssignmentPrecondition.ExpectedAbsence() : new AssignmentPrecondition.ExpectedConcurrencyToken(prior.Token),
                new("tester", DateTimeOffset.UtcNow, scenario.Plan.Inputs.Revision), default);
            Assert.Contains(result.Outcome, new[] { AssignmentOutcome.Created, AssignmentOutcome.Updated });
            var plan = await UpdatePlan(client, revision, generation - 1);
            var committed = await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId });
            Assert.Equal(generation, committed.Commit.Generation);
            Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = committed.Commit.DeploymentId, Generation = generation })).Status);
            var selection = DocumentRequest(scenario); selection.Generation = generation;
            var document = await client.MaterializeConfigurationAsync(selection);
            var grant = await client.CreateConfigurationAccessAsync(new() { Selector = selection });
            var delivered = await new Haby.Configuration.Contracts.ConfigurationDelivery.ConfigurationDeliveryClient(channel)
                .DeliverAsync(new() { RevisionId = document.Artifact.RevisionId }, Bearer(grant.Credential));
            using var json = JsonDocument.Parse(delivered.Json.Memory);
            Assert.Equal(generation == 2 ? "Information" : "Debug", json.RootElement.GetProperty("Logging"u8).GetProperty("Level"u8).GetString());
        }
        var noop = await UpdatePlan(client, revision, 3);
        Assert.Equal(3, (await client.CommitDesiredStateAsync(new() { ChangeSetId = noop.ChangeSetId, PlanId = noop.PlanId })).Commit.Generation);
        Assert.Equal(history, await ReuseHistory(scenario.Connection));
        await using (var verify = Context(scenario.Connection)) Assert.Equal(3, await verify.DeploymentGenerations.CountAsync());
        await host.StopAsync();
    }
}
