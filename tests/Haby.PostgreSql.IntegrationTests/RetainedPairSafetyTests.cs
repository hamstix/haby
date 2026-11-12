using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    [Theory]
    [InlineData("database-retention")]
    [InlineData("login-retention")]
    [InlineData("creation")]
    [InlineData("output")]
    public async Task Retained_pair_restart_after_commit_requires_both_policies_and_exact_original_support(string missing)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var name = "retain_pair_support_" + Guid.NewGuid().ToString("N");
        await using (var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            await EstablishRetained(scenario, client, name, 2, pair: true);
            var plan = await RetentionPlan(scenario, client, name, 3, 2, true, false);
            Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
            await host.StopAsync();
        }
        var history = await ReuseHistory(scenario.Connection); var oid = await DatabaseOid(name);
        await using var restarted = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
        {
            foreach (var registration in services.Where(s => s.ServiceType == typeof(IDurableActionAdmissionHandler)).ToArray()) services.Remove(registration);
            var database = new PostgreSqlDatabaseCreateAdmission(); var login = new PostgreSqlPairResourceAdmission(PostgreSqlApplicationLoginContract.Id);
            services.AddSingleton<IDurableActionAdmissionHandler>(missing == "database-retention" ? new WithoutRetentionPolicy(database) : database);
            services.AddSingleton<IDurableActionAdmissionHandler>(missing == "login-retention" ? new WithoutRetentionPolicy(login) : login);
            if (missing is "creation" or "output")
                services.AddSingleton<DurableActionRegistry>(p => new(p.GetServices<IDurableActionAdmissionHandler>(),
                    p.GetServices<IDurableActionHandler>().Where(h => missing != "creation" || h.Key != PostgreSqlApplicationLoginAction.RegistrationKey),
                    p.GetServices<IExternalOperationOutputContract>().Where(o => missing != "output" || o.Producer != PostgreSqlApplicationAccessAction.RegistrationKey)));
        });
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(restarted);
        using var restartedChannel = GrpcChannel.ForAddress(restarted.Urls.Single());
        var result = await OperatorTestCredentials.Client(restartedChannel).ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 3 });
        Assert.Equal(GenerationExecutionStatus.InputUnavailable, result.Status); Assert.Equal(2, result.Progress.AppliedGeneration);
        Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.Equal(oid, await DatabaseOid(name));
        await using (var verify = Context(scenario.Connection))
        {
            var graph = DurableOperationGraphCodec.Decode((await verify.DeploymentGenerations.AsNoTracking().SingleAsync(g => g.Generation == 3)).GraphJson);
            Assert.Equal(new[] { "db-b", "login-b" }, graph.RetainedResources.Select(r => r.Resource.Value));
        }
        await restarted.StopAsync();
    }

    private static async Task<DurablePlanSummary> RetentionPlan(Scenario scenario, ManifestOperator.ManifestOperatorClient client,
        string name, int version, int expected, bool pair, bool declared, string? endpoint = null)
    {
        var envelope = declared ? pair ? IndependentEnvelope(scenario, version, name) : MixedEnvelope(scenario, version, name) : UpdateEnvelope(scenario, version);
        var revision = await ImportUpdate(client, envelope);
        if (!pair) return await UpdatePlan(client, revision, expected, endpoint, created: expected == 1 ? 1 : 0, added: declared && expected > 1);
        var change = await client.CreateChangeSetAsync(new()
        {
            EnvironmentId = "pilot",
            Reference = revision,
            Precondition = PlanningPrecondition.ExpectedGeneration,
            ExpectedGeneration = expected
        });
        var result = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
        Assert.Equal(PlanningStatus.Planned, result.Status);
        var plan = result.Plan;
        Assert.Equal(expected == 1 ? 5 : 0, plan.Operations.Count);
        Assert.Equal(expected == 1 || !declared ? 2 : 4, plan.ReusedResources.Count);
        Assert.Equal(declared ? 0 : 2, plan.RetainedResources.Count);
        Assert.All(plan.ReusedResources.Concat(plan.RetainedResources), r => Assert.Equal(r.ResourceId is "db-b" or "login-b" ? 2 : 1, r.SourceGeneration));
        Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(new() { PlanId = plan.PlanId })).Status);
        if (endpoint is not null) _ = await NativeReuseCommand(endpoint, "plan", "get", plan.PlanId);
        return plan;
    }

    [Theory]
    [InlineData("half-login", false)]
    [InlineData("half-db", false)]
    [InlineData("half-login", true)]
    [InlineData("half-db", true)]
    [InlineData("name", true)]
    [InlineData("schema", true)]
    [InlineData("policy", true)]
    [InlineData("provider", true)]
    [InlineData("provider-revision", true)]
    [InlineData("delete", true)]
    [InlineData("new-login-key", true)]
    [InlineData("new-db-key", true)]
    [InlineData("name-claim", true)]
    [InlineData("swap", true)]
    [InlineData("dangling", false)]
    [InlineData("all", false)]
    [InlineData("remove-a", false)]
    public async Task Retained_pair_invalid_closures_and_foreign_claims_refuse_before_acceptance(string fault, bool returning)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var name = "retain_pair_refuse_" + Guid.NewGuid().ToString("N"); var baseline = returning ? 3 : 2;
        await EstablishRetained(scenario, client, name, 2, pair: true);
        var selector = DocumentRequest(scenario, "consumer-b"); selector.Generation = 2;
        var role = PostgreSqlPairResourceAdmission.RoleName(scenario.Owner with { ResourceLocalId = ManifestLocalId.Create("login-b") });
        var probe = await PrepareRefusalProbe(scenario, client, channel, selector, name, role);
        if (returning)
        {
            var removal = await RetentionPlan(scenario, client, name, 3, 2, true, false);
            Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = removal.ChangeSetId, PlanId = removal.PlanId })).Status);
            Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 3 })).Status);
            // Capture the accepted retained baseline, preserving the original grant and sentinel.
            probe = probe with { Accepted = await RefusalAcceptedState(scenario.Connection) };
        }
        var history = await ReuseHistory(scenario.Connection); var identity = await ExternalIdentity(scenario); var oid = await DatabaseOid(name);
        if (fault == "provider-revision")
        {
            var inputs = scenario.Plan.Inputs; var providers = host.Services.GetRequiredService<IProviderConfigurationStore>();
            var original = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(inputs.Environment, inputs.Providers.RequireUniformResourceProvider().Instance, inputs.Providers.RequireUniformResourceProvider().Configuration, default))!;
            await providers.AddRevisionAsync(inputs.Environment, inputs.Providers.RequireUniformResourceProvider().Instance, ProviderConfigurationRevisionNumber.Create(2), original.Configuration, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await host.Services.GetRequiredService<IProviderAliasStore>().ChangeAsync(inputs.Environment, inputs.Providers.RequireUniformResourceProvider().Alias,
                new(inputs.Providers.RequireUniformResourceProvider().Instance, inputs.Providers.RequireUniformResourceProvider().Configuration), new(inputs.Providers.RequireUniformResourceProvider().Instance, ProviderConfigurationRevisionNumber.Create(2)), "tester", default)).Outcome);
        }
        var envelope = IndependentEnvelope(scenario, baseline + 1, name); var spec = envelope["manifest"]!["spec"]!;
        var resources = spec["resources"]!.AsObject();
        switch (fault)
        {
            case "half-login": resources.Remove("login-b"); spec["components"]!.AsObject().Remove("consumer-b"); break;
            case "half-db": resources.Remove("db-b"); break;
            case "name": resources["db-b"]!["properties"]!["name"] = name + "_new"; break;
            case "schema": resources["login-b"]!["properties"]!["schema"] = "changed"; break;
            case "policy": resources["login-b"]!["properties"]!["privilegePolicy"] = "other/v1"; break;
            case "provider": resources["login-b"]!["providerRef"] = "other.provider"; break;
            case "delete": resources["db-b"]!["lifecycle"] = new JsonObject { ["deletionPolicy"] = "delete" }; break;
            case "new-login-key": resources["other-login"] = resources["login-b"]!.DeepClone(); resources.Remove("login-b"); spec["components"]!["consumer-b"]!["bindings"]!["storage"]!["target"]!["resourceRef"] = "other-login"; break;
            case "new-db-key": resources["other-db"] = resources["db-b"]!.DeepClone(); resources.Remove("db-b"); resources["login-b"]!["properties"]!["databaseRef"] = "other-db"; break;
            case "name-claim": resources.Remove("db-b"); resources.Remove("login-b"); spec["components"]!.AsObject().Remove("consumer-b"); resources["claim"] = new JsonObject { ["type"] = "postgresql.database/v1alpha1", ["providerRef"] = "postgresql.primary", ["properties"] = new JsonObject { ["name"] = name } }; break;
            case "swap": resources["login-b"]!["properties"]!["databaseRef"] = "db"; resources["app-login"]!["properties"]!["databaseRef"] = "db-b"; break;
            case "dangling": resources.Remove("db-b"); resources.Remove("login-b"); break;
            case "all": resources.Clear(); spec["components"]!.AsObject().Clear(); break;
            case "remove-a": resources.Remove("db"); resources.Remove("app-login"); spec["components"]!.AsObject().Remove("api"); spec["components"]!.AsObject().Remove("worker"); break;
        }
        var imported = await client.ImportAsync(new() { OriginalEnvelope = Google.Protobuf.ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(envelope)) });
        var revision = imported.Created?.Revision.Reference ?? imported.ExistingEquivalent?.Revision.Reference;
        if (fault is "half-db" or "policy" or "dangling")
        {
            Assert.Equal(ImportReply.OutcomeOneofCase.InvalidManifest, imported.OutcomeCase);
            Assert.Null(revision);
        }
        else
        {
            Assert.NotNull(revision);
            var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = baseline });
            var plan = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
            Assert.Null(plan.Plan); Assert.Equal(PlanningStatus.UnsupportedShape, plan.Status);
            Assert.Equal(PlanningStatus.UnsupportedShape, (await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId })).Status);
        }
        await VerifyRefusalProbe(scenario, channel, probe);
        Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.Equal(identity, await ExternalIdentity(scenario)); Assert.Equal(oid, await DatabaseOid(name));
        await using (var verify = Context(scenario.Connection))
        { Assert.Equal(baseline, await verify.DeploymentGenerations.CountAsync()); Assert.Equal(baseline, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration); }
        await host.StopAsync();
        await using var restarted = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(restarted);
        using var restartedChannel = GrpcChannel.ForAddress(restarted.Urls.Single());
        var restartedClient = OperatorTestCredentials.Client(restartedChannel);
        if (revision is not null)
        {
            var change = await restartedClient.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = baseline });
            Assert.Equal(PlanningStatus.UnsupportedShape, (await restartedClient.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId })).Status);
        }
        else
            Assert.Equal(ImportReply.OutcomeOneofCase.InvalidManifest, (await restartedClient.ImportAsync(new()
            { OriginalEnvelope = Google.Protobuf.ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(envelope)) })).OutcomeCase);
        await VerifyRefusalProbe(scenario, restartedChannel, probe);
        await restarted.StopAsync();
    }

    [Theory]
    [InlineData("role-reservation", false)]
    [InlineData("role-reservation", true)]
    [InlineData("source", false)]
    [InlineData("source", true)]
    [InlineData("slot", false)]
    [InlineData("slot", true)]
    [InlineData("purpose", false)]
    [InlineData("purpose", true)]
    [InlineData("material", false)]
    [InlineData("material", true)]
    [InlineData("corrupt-material", true)]
    [InlineData("output", false)]
    [InlineData("output", true)]
    [InlineData("foreign-password", false)]
    [InlineData("foreign-password", true)]
    [InlineData("selector", false)]
    [InlineData("selector", true)]
    public async Task Retained_pair_missing_or_foreign_original_evidence_refuses_without_repair(string fault, bool accepted)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var name = "retain_pair_damage_" + Guid.NewGuid().ToString("N");
        await EstablishRetained(scenario, client, name, 2, pair: true);
        if (accepted)
        {
            var plan = await RetentionPlan(scenario, client, name, 3, 2, true, false);
            Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
        }
        await Sql(scenario.Connection, "SET session_replication_role = replica; " + (fault switch
        {
            "role-reservation" => "DELETE FROM provider_resource_reservations WHERE resource_local_id = 'login-b'",
            "source" => "UPDATE resource_management SET source_generation = 1 WHERE resource_local_id = 'login-b'",
            "slot" => "DELETE FROM generated_credential_slots WHERE resource_local_id = 'login-b'",
            "purpose" => "UPDATE secrets SET owner = 'Environment', purpose = 'ProviderAdministrator', application_record_id = NULL, resource_local_id = NULL WHERE id IN (SELECT secret_id FROM generated_credential_slots WHERE resource_local_id = 'login-b')",
            "material" => "DELETE FROM secret_payloads WHERE secret_version_id IN (SELECT version_id FROM generated_credential_slots WHERE resource_local_id = 'login-b')",
            "corrupt-material" => "UPDATE secret_payloads SET bytes = decode(repeat('00', 43), 'hex') WHERE secret_version_id IN (SELECT version_id FROM generated_credential_slots WHERE resource_local_id = 'login-b')",
            "output" => "DELETE FROM operation_outputs WHERE operation_id = 'login-b-access'",
            "selector" => "UPDATE operation_outputs SET output_key = 'foreign' WHERE operation_id = 'login-b-create'",
            _ => "UPDATE operation_outputs SET payload = jsonb_set(payload::jsonb, '{password}', (SELECT payload::jsonb->'password' FROM operation_outputs WHERE operation_id = 'app-login-access'))::text WHERE operation_id = 'login-b-access'"
        }));
        var history = await ReuseHistory(scenario.Connection); var oid = await DatabaseOid(name);
        if (accepted)
        {
            var result = await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 3 });
            Assert.Equal(GenerationExecutionStatus.InputUnavailable, result.Status); Assert.Equal(2, result.Progress.AppliedGeneration);
        }
        else
        {
            var revision = await ImportUpdate(client, UpdateEnvelope(scenario, 3));
            var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = 2 });
            Assert.Equal(PlanningStatus.PlanInputUnavailable, (await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId })).Status);
        }
        Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.Equal(oid, await DatabaseOid(name));
        await host.StopAsync();
    }
}
