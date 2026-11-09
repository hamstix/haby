using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Npgsql;
using AssignmentTarget = Haby.Operator.Contracts.ProfileAssignmentTarget;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    private static JsonObject ComponentEnvelope(Scenario scenario, int version)
    {
        var envelope = UpdateEnvelope(scenario, version, version == 2 ? 210 : 220);
        var components = envelope["manifest"]!["spec"]!["components"]!;
        var bindings = components["api"]!["bindings"]!.AsObject();
        bindings["primary"] = bindings["storage"]!.DeepClone(); bindings.Remove("storage");
        if (version == 2)
        {
            components["scheduler"] = components["worker"]!.DeepClone();
            components["scheduler"]!["bindings"]!["storage"]!["options"] = new JsonObject { ["timeout"] = 240 };
            components.AsObject().Remove("worker");
        }
        return envelope;
    }

    private static async Task AssignComponent(ManifestOperator.ManifestOperatorClient client, Scenario scenario,
        string component, string version, bool disable)
    {
        var target = new AssignmentTarget { Kind = "component", ApplicationId = DocumentRequest(scenario).ApplicationId, ComponentRef = component };
        var prior = await client.GetProfileAssignmentAsync(new() { Environment = "pilot", Target = target });
        var declaration = new JsonObject
        {
            ["apiVersion"] = "haby.dev/v1alpha1",
            ["kind"] = "ConfigurationProfileAssignment",
            ["spec"] = new JsonObject
            {
                ["target"] = new JsonObject { ["kind"] = "component", ["applicationId"] = target.ApplicationId, ["componentRef"] = component },
                ["decisions"] = disable ? new JsonArray(new JsonObject { ["kind"] = "disable", ["profileId"] = "connection-layout" }) : new JsonArray()
            }
        };
        var request = new ApplyProfileAssignmentRequest
        {
            Environment = "pilot",
            ApplicationVersion = version,
            Payload = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(declaration))
        };
        if (prior.Assignment is null) request.ExpectedAbsence = true; else request.ExpectedConcurrencyToken = prior.Assignment.ConcurrencyToken;
        Assert.Contains((await client.ApplyProfileAssignmentAsync(request)).Status,
            new[] { ProfileAssignmentStatus.Created, ProfileAssignmentStatus.Updated });
    }

    [Fact]
    public async Task Component_update_target_scopes_include_absence_exclude_removals_and_resume_logical_same_id_assignment()
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var history = await ReuseHistory(scenario.Connection);
        var appProfile = JsonNode.Parse(RenderingProfile)!; appProfile["metadata"]!["revision"] = 2;
        appProfile["spec"]!["staticContributions"]!["logging"]!["value"]!["Level"] = "Debug";
        Assert.Equal(ProfileStatus.Created, (await client.ImportProfileAsync(new() { Payload = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(appProfile.AsObject())) })).Status);
        var appSelection = JsonNode.Parse("""{"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfileAssignment","spec":{"target":{"kind":"application","applicationId":"placeholder"},"decisions":[{"kind":"use","profileRef":{"profileId":"connection-layout","revision":2}}]}}"""u8)!;
        appSelection["spec"]!["target"]!["applicationId"] = DocumentRequest(scenario).ApplicationId;
        Assert.Equal(ProfileAssignmentStatus.Created, (await client.ApplyProfileAssignmentAsync(new()
        {
            Environment = "pilot",
            ExpectedAbsence = true,
            Payload = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(appSelection.AsObject()))
        })).Status);
        var second = ComponentEnvelope(scenario, 2);
        second["manifest"]!["spec"]!["components"]!["scheduler"]!["bindings"]!["storage"]!.AsObject().Remove("configurationSlot");
        var revision = await ImportUpdate(client, second);
        var plan = await UpdatePlan(client, revision, 1);
        var planner = host.Services.GetRequiredService<IDurableDeploymentPlanner>();
        var originalPlanId = plan.PlanId;
        var frozen = (await planner.FindPlanAsync(DeploymentPlanId.Parse(plan.PlanId), default))!.Inputs.Configuration!;
        Assert.Equal(2, Assert.Single(frozen.Layouts.Single(x => x.Component.Value == "scheduler").Documents).Origin.Profile!.Revision);
        Assert.Contains(frozen.Scopes, x => x.Target is Haby.Domain.ProfileAssignmentTarget.Component c && c.ComponentRef.Value == "scheduler" && x.Token is null);
        Assert.DoesNotContain(frozen.Scopes, x => x.Target is Haby.Domain.ProfileAssignmentTarget.Component c && c.ComponentRef.Value == "worker");
        // Even first insertion of an empty local assignment changes an exact relevant input.
        await AssignComponent(client, scenario, "scheduler", "v2", false);
        await Stale(plan);
        plan = await UpdatePlan(client, revision, 1);
        await AssignComponent(client, scenario, "scheduler", "v2", true);
        await Stale(plan);
        plan = await UpdatePlan(client, revision, 1);
        // A stored removed scope is irrelevant, including insertion after Plan.
        await AssignComponent(client, scenario, "worker", "v1", false);
        await AssignComponent(client, scenario, "worker", "v1", true);
        Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(new() { PlanId = plan.PlanId })).Status);
        // A removed scope's exclusive writer lock must not enter the target Commit lock set.
        await using (var removedLock = new NpgsqlConnection(scenario.Connection))
        {
            await removedLock.OpenAsync(); await using var transaction = await removedLock.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", removedLock, transaction);
            command.Parameters.AddWithValue($"haby-configuration:{Guid.Parse(scenario.Owner.EnvironmentRecordId.ToString()):N}:component:{DocumentRequest(scenario).ApplicationId}:worker");
            await command.ExecuteNonQueryAsync();
            Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId }).ResponseAsync.WaitAsync(TimeSpan.FromSeconds(10))).Status);
            await transaction.CommitAsync();
        }
        Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 })).Status);
        var batch = await client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario));
        Assert.Equal(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, batch.Status);
        Assert.Equal("api", Assert.Single(batch.Items).Target.ComponentId); // scheduler's explicit disable wins.
        // Same ID resumes its logical assignment, a new name inherits Environment defaults.
        var third = ComponentEnvelope(scenario, 3);
        third["manifest"]!["spec"]!["components"]!["renamed"] = third["manifest"]!["spec"]!["components"]!["worker"]!.DeepClone();
        third["manifest"]!["spec"]!["components"]!["worker"]!["bindings"]!["storage"]!.AsObject().Remove("configurationSlot");
        var next = await UpdatePlan(client, await ImportUpdate(client, third), 2);
        var snapshot = (await planner.FindPlanAsync(DeploymentPlanId.Parse(next.PlanId), default))!.Inputs.Configuration!;
        Assert.Empty(snapshot.Layouts.Single(x => x.Component.Value == "worker").Documents);
        Assert.Equal("settings", Assert.Single(snapshot.Layouts.Single(x => x.Component.Value == "renamed").Documents).Definition.Id.Value);
        Assert.Equal(2, Assert.Single(snapshot.Layouts.Single(x => x.Component.Value == "renamed").Documents).Origin.Profile!.Revision);
        Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = next.ChangeSetId, PlanId = next.PlanId })).Status);
        Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 3 })).Status);
        Assert.Equal(new[] { "api", "renamed" }, (await client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario))).Items.Select(x => x.Target.ComponentId));
        Assert.Equal(history, await ReuseHistory(scenario.Connection));
        Assert.Equal(frozen, (await planner.FindPlanAsync(DeploymentPlanId.Parse(originalPlanId), default))!.Inputs.Configuration);
        await host.StopAsync();

        async Task Stale(DurablePlanSummary stale)
        {
            Assert.Equal(PlanningStatus.PlanStale, (await client.CheckPlanAsync(new() { PlanId = stale.PlanId })).Status);
            Assert.Equal(CommitStatus.PlanStale, (await client.CommitDesiredStateAsync(new() { ChangeSetId = stale.ChangeSetId, PlanId = stale.PlanId })).Status);
            await using var verify = Context(scenario.Connection); Assert.Equal(1, await verify.DeploymentGenerations.CountAsync());
        }
    }

    [Fact]
    public async Task Component_update_zero_components_produces_empty_batch_without_deleting_old_documents_or_grants()
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var old = DocumentRequest(scenario, "worker", exact: true);
        var document = await client.MaterializeConfigurationAsync(old);
        var grant = await client.CreateConfigurationAccessAsync(new() { Selector = old });
        var history = await ReuseHistory(scenario.Connection);
        var envelope = UpdateEnvelope(scenario, 2); envelope["manifest"]!["spec"]!["components"]!.AsObject().Clear();
        var plan = await UpdatePlan(client, await ImportUpdate(client, envelope), 1);
        Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
        Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 2 })).Status);
        var batch = await client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario));
        Assert.Equal(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, batch.Status); Assert.Empty(batch.Items); Assert.Equal(0, batch.SelectedCount);
        Assert.Equal(Haby.Operator.Contracts.ConfigurationDocumentStatus.NotFound, (await client.GetConfigurationAsync(DocumentRequest(scenario, "worker"))).Status);
        Assert.Equal(document.Artifact, (await client.GetConfigurationAsync(old)).Artifact);
        _ = await new Haby.Configuration.Contracts.ConfigurationDelivery.ConfigurationDeliveryClient(channel).DeliverAsync(new() { RevisionId = document.Artifact.RevisionId }, Bearer(grant.Credential));
        Assert.Equal(history, await ReuseHistory(scenario.Connection));
        await using var verify = Context(scenario.Connection); Assert.Single(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync()); Assert.Single(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
        await host.StopAsync();
    }

    [Theory]
    [InlineData("missing-target")]
    [InlineData("missing-slot")]
    [InlineData("renderer")]
    [InlineData("duplicate-slot")]
    [InlineData("path-collision")]
    [InlineData("external")]
    [InlineData("unsupported-new-target")]
    public async Task Component_update_invalid_target_layout_refuses_without_desired_state_or_effects(string fault)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var history = await ReuseHistory(scenario.Connection);
        var envelope = ComponentEnvelope(scenario, 2);
        var spec = envelope["manifest"]!["spec"]!;
        var binding = spec["components"]!["scheduler"]!["bindings"]!["storage"]!;
        switch (fault)
        {
            case "missing-target": binding["target"]!["resourceRef"] = "absent"; break;
            case "missing-slot": binding["configurationSlot"] = "absent"; break;
            case "renderer": binding["target"]!["resourceRef"] = "db"; binding.AsObject().Remove("options"); break;
            case "duplicate-slot": spec["components"]!["scheduler"]!["bindings"]!["duplicate"] = binding.DeepClone(); break;
            case "path-collision":
                var profile = JsonNode.Parse(RenderingProfile)!; profile["metadata"]!["revision"] = 2;
                profile["spec"]!["bindingRules"]!["connection"]!["documentProjections"]![0]!["path"] = "/Logging";
                Assert.Equal(ProfileStatus.Created, (await client.ImportProfileAsync(new() { Payload = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(profile.AsObject())) })).Status);
                var prior = await client.GetProfileAssignmentAsync(new() { Environment = "pilot", Target = new() { Kind = "environment" } });
                Assert.Equal(ProfileAssignmentStatus.Updated, (await client.ApplyProfileAssignmentAsync(new()
                {
                    Environment = "pilot",
                    ExpectedConcurrencyToken = prior.Assignment.ConcurrencyToken,
                    Payload = ByteString.CopyFromUtf8("""{"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfileAssignment","spec":{"target":{"kind":"environment"},"decisions":[{"kind":"use","profileRef":{"profileId":"connection-layout","revision":2}}]}}""")
                })).Status); break;
            case "external":
                binding["target"] = JsonNode.Parse("""{"kind":"application-export","applicationId":"other","exportRef":"login","contract":"postgresql.application-login-output/v1alpha1"}"""u8); break;
            case "unsupported-new-target":
                spec["resources"]!["other-login"] = spec["resources"]!["app-login"]!.DeepClone();
                binding["target"]!["resourceRef"] = "other-login"; break;
        }
        var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(envelope)) });
        if (fault is "missing-target" or "duplicate-slot")
        {
            Assert.Null(imported.Created); Assert.Null(imported.ExistingEquivalent);
        }
        else
        {
            Assert.NotNull(imported.Created);
            var change = await client.CreateChangeSetAsync(new()
            {
                EnvironmentId = "pilot",
                Reference = imported.Created.Revision.Reference,
                Precondition = PlanningPrecondition.ExpectedGeneration,
                ExpectedGeneration = 1
            });
            var result = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
            Assert.Contains(result.Status, new[] { PlanningStatus.ConfigurationRefused, PlanningStatus.UnsupportedShape }); Assert.Null(result.Plan);
        }
        Assert.Equal(history, await ReuseHistory(scenario.Connection));
        await using var verify = Context(scenario.Connection);
        var deployment = await verify.ApplicationDeployments.AsNoTracking().SingleAsync();
        Assert.Equal(1, deployment.DesiredGeneration); Assert.Equal(1, deployment.AppliedGeneration);
        Assert.Single(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
        await host.StopAsync();
    }

}
