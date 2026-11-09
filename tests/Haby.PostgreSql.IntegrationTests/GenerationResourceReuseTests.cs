using System.Diagnostics;
using System.Text.Json;
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
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    private static JsonObject UpdateEnvelope(Scenario scenario, int version, int timeout = 210)
    {
        var envelope = PostgreSqlPairPlanningTests.Example();
        envelope["applicationVersion"] = "v" + version;
        envelope["manifest"]!["spec"]!["resources"]!["db"]!["properties"]!["name"] = scenario.Database;
        foreach (var component in new[] { "api", "worker" })
            envelope["manifest"]!["spec"]!["components"]![component]!["bindings"]!["storage"]!["configurationSlot"] = "main-database";
        envelope["manifest"]!["spec"]!["components"]!["api"]!["bindings"]!["storage"]!["options"] = new JsonObject { ["timeout"] = timeout };
        return envelope;
    }

    private static async Task<RevisionReference> ImportUpdate(ManifestOperator.ManifestOperatorClient client, JsonObject envelope)
    {
        var result = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(envelope)) });
        Assert.NotNull(result.Created ?? (object?)result.ExistingEquivalent);
        return result.Created?.Revision.Reference ?? result.ExistingEquivalent.Revision.Reference;
    }

    private static async Task<DurablePlanSummary> UpdatePlan(ManifestOperator.ManifestOperatorClient client, RevisionReference revision, long expected, string? endpoint = null, int created = 0, bool added = false)
    {
        var native = endpoint is null ? null : await NativeReuseCommand(endpoint, "changeset", "create", revision.ApplicationRecordId, revision.RevisionNumber.ToString(), "--expected-generation", expected.ToString());
        var change = native is { } nativeChange ? await client.GetChangeSetAsync(new() { ChangeSetId = nativeChange.GetProperty("data"u8).GetProperty("changeSet"u8).GetProperty("changeSetId"u8).GetString()! }) : await client.CreateChangeSetAsync(new()
        { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = expected });
        Assert.Equal(native is null ? PlanningStatus.Created : PlanningStatus.Found, change.Status);
        Assert.Equal(expected, change.ChangeSet.ExpectedGeneration);
        var nativePlan = endpoint is null ? null : await NativeReuseCommand(endpoint, "plan", "create", change.ChangeSet.ChangeSetId);
        var planned = nativePlan is { } p ? await client.GetPlanAsync(new() { PlanId = p.GetProperty("data"u8).GetProperty("plan"u8).GetProperty("planId"u8).GetString()! }) : await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
        Assert.NotNull(planned.Plan);
        Assert.Equal(created, planned.Plan.Operations.Count); Assert.Equal(added ? 3 : 2, planned.Plan.ReusedResources.Count);
        Assert.All(planned.Plan.ReusedResources, x => Assert.Equal(x.ResourceId == "extra" ? 2 : 1, x.SourceGeneration));
        Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(new() { PlanId = planned.Plan.PlanId })).Status);
        return planned.Plan;
    }

    private static async Task<string> ReuseHistory(string connection)
    {
        await using var db = Context(connection);
        // Independent complete immutable history oracle, excluding desired/applied pointers and new configuration artifacts.
        var operations = await db.DurableOperations.AsNoTracking().OrderBy(x => x.OperationId).Select(x => x.OperationId + ":" + x.State + ":" + x.CurrentAttemptId).ToArrayAsync();
        var attempts = await db.DurableOperationAttempts.AsNoTracking().OrderBy(x => x.OperationId).Select(x => x.AttemptId + ":" + x.Outcome).ToArrayAsync();
        var outputs = await db.OperationOutputs.AsNoTracking().OrderBy(x => x.OperationId).Select(x => x.OperationId + ":" + x.Contract + ":" + x.Payload).ToArrayAsync();
        var slots = await db.GeneratedCredentialSlots.AsNoTracking().Select(x => x.SecretId + ":" + x.VersionId).ToArrayAsync();
        var management = await db.ResourceManagement.AsNoTracking().OrderBy(x => x.Resource)
            .Select(x => x.Id + ":" + x.Environment + ":" + x.Application + ":" + x.Resource + ":" + x.Deployment + ":" + x.SourceGeneration).ToArrayAsync();
        var reservations = await db.ProviderResourceReservations.AsNoTracking().OrderBy(x => x.Namespace).ThenBy(x => x.Value)
            .Select(x => x.ProviderInstanceId + ":" + x.EnvironmentRecordId + ":" + x.ApplicationRecordId + ":" + x.ResourceLocalId + ":" + x.Namespace + ":" + x.Value).ToArrayAsync();
        return string.Join("\n", operations.Concat(attempts).Concat(outputs).Concat(slots).Concat(management).Concat(reservations)) + ":" + await db.SecretVersions.CountAsync() + ":" + await db.GeneratedCredentialCreationAudits.CountAsync();
    }

    private async Task<string> ExternalIdentity(Scenario scenario)
    {
        await using var db = new NpgsqlConnection(fixture.ConnectionString); await db.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT json_build_array(d.oid, d.datdba, d.datacl, r.oid, r.rolname, r.rolcanlogin)::text FROM pg_database d JOIN pg_roles r ON r.oid = d.datdba WHERE d.datname = $1", db);
        command.Parameters.AddWithValue(scenario.Database);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resource_reuse_v1_v2_v3_original_evidence_data_delivery_noop_native_restart_and_restore(bool changedComponents)
    {
        var scenario = await Prepare(full: true, render: true, configureManifest: changedComponents ? envelope =>
        {
            var api = envelope["manifest"]!["spec"]!["components"]!["api"]!;
            api["documents"] = JsonNode.Parse("""{"legacy":{"fileName":"legacy.json","format":"json","template":{}}}"""u8);
            api["bindings"]!["legacy"] = JsonNode.Parse("""{"target":{"kind":"local-resource","resourceRef":"app-login"},"options":{"sslMode":"disable","timeout":200},"documentProjections":[{"documentRef":"legacy","path":"/PostgreSQL/DefaultConnection/ConnectionString","rendererRef":"postgresql.npgsql-connection/v1alpha1"}]}"""u8);
        }
        : null);
        var grants = new List<(ConfigurationDocumentRequest Selector, ConfigurationDocumentReply Document, ConfigurationAccessReply Grant, int Timeout)>();
        string history;
        string external;
        string login;
        CommitReply? secondReceipt = null;
        CommitDesiredStateRequest? secondCommit = null;
        await using (var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 1 })).Status);
            var selection = DocumentRequest(scenario, exact: true);
            var document = await client.MaterializeConfigurationAsync(selection);
            var grant = await client.CreateConfigurationAccessAsync(new() { Selector = selection });
            grants.Add((selection, document, grant, 200));
            var workerSelection = DocumentRequest(scenario, "worker", exact: true);
            grants.Add((workerSelection, await client.MaterializeConfigurationAsync(workerSelection),
                await client.CreateConfigurationAccessAsync(new() { Selector = workerSelection }), 15));
            if (changedComponents)
            {
                var legacy = DocumentRequest(scenario, exact: true); legacy.DocumentId = "legacy";
                grants.Add((legacy, await client.MaterializeConfigurationAsync(legacy), await client.CreateConfigurationAccessAsync(new() { Selector = legacy }), 200));
            }
            login = await ReceivedConnection(channel, document, grant, 200);
            await Sql(login, "CREATE TABLE app.reuse_probe(id integer PRIMARY KEY, value text NOT NULL); INSERT INTO app.reuse_probe VALUES (158, 'retained-v1')");
            history = await ReuseHistory(scenario.Connection); external = await ExternalIdentity(scenario);
            // Independent no-Provider-work oracle: the restarted Host has no administrator
            // material with which to contact the external server. Application material remains exact.
            await Sql(scenario.Connection, "SET session_replication_role = replica; DELETE FROM secret_payloads WHERE secret_version_id NOT IN (SELECT version_id FROM generated_credential_slots)");
            var revision = await ImportUpdate(client, changedComponents ? ComponentEnvelope(scenario, 2) : UpdateEnvelope(scenario, 2));
            var plan = await UpdatePlan(client, revision, 1, host.Urls.Single());
            _ = await NativeReuseCommand(host.Urls.Single(), "changeset", "commit", plan.ChangeSetId, "--plan", plan.PlanId);
            var committed = await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId });
            secondReceipt = committed;
            secondCommit = new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId };
            Assert.Equal(CommitStatus.Committed, committed.Status); Assert.Equal(2, committed.Commit.Generation);
            var progress = await client.GetGenerationAsync(new() { DeploymentId = committed.Commit.DeploymentId, Generation = 2 });
            Assert.Equal(2, progress.Progress.DesiredGeneration); Assert.Equal(1, progress.Progress.AppliedGeneration);
            Assert.Empty(progress.Progress.Nodes); Assert.Equal(2, progress.Progress.ReusedResources);
            Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.Equal(external, await ExternalIdentity(scenario));
            Assert.Equal(Haby.Operator.Contracts.ConfigurationDocumentStatus.NotApplied,
                (await client.MaterializeConfigurationAsync(DocumentRequest(scenario))).Status);
            await host.StopAsync(); // Committed desired generation survives a process restart before Execute.
        }
        await using (var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            for (var generation = 2; generation <= 3; generation++)
            {
                if (generation == 3)
                {
                    var revision = await ImportUpdate(client, changedComponents ? ComponentEnvelope(scenario, 3) : UpdateEnvelope(scenario, 3, 220));
                    var plan = await UpdatePlan(client, revision, 2, host.Urls.Single());
                    _ = await NativeReuseCommand(host.Urls.Single(), "changeset", "commit", plan.ChangeSetId, "--plan", plan.PlanId);
                    Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
                }
                var request = new GenerationRequest { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = generation };
                var nativeExecute = await NativeReuseCommand(host.Urls.Single(), "deployment", "generation", "execute", request.DeploymentId, generation.ToString());
                if (nativeExecute is { } n)
                {
                    Assert.Equal("succeeded", n.GetProperty("outcome"u8).GetString());
                    Assert.Equal(0, n.GetProperty("data"u8).GetProperty("progress"u8).GetProperty("total"u8).GetInt32());
                    Assert.Equal(2, n.GetProperty("data"u8).GetProperty("progress"u8).GetProperty("reusedResources"u8).GetInt32());
                }
                var executed = await client.ExecuteGenerationAsync(request);
                Assert.Equal(GenerationExecutionStatus.Succeeded, executed.Status); Assert.Equal(0, executed.Progress.Total);
                Assert.Equal(generation, executed.Progress.AppliedGeneration); Assert.Equal(2, executed.Progress.ReusedResources);
                Assert.Equal(executed, await client.ExecuteGenerationAsync(request));
                var selection = DocumentRequest(scenario); selection.Generation = generation;
                var batch = await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = selection.ApplicationId, Generation = generation });
                Assert.Equal(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, batch.Status);
                Assert.Equal(new[] { "api", changedComponents && generation == 2 ? "scheduler" : "worker" }, batch.Items.Select(x => x.Target.ComponentId));
                Assert.All(batch.Items, x => Assert.Equal("settings", x.Target.DocumentId));
                if (changedComponents)
                {
                    var removed = DocumentRequest(scenario, generation == 2 ? "worker" : "scheduler");
                    Assert.Equal(Haby.Operator.Contracts.ConfigurationDocumentStatus.NotFound, (await client.MaterializeConfigurationAsync(removed)).Status);
                    var newSelection = DocumentRequest(scenario, generation == 2 ? "scheduler" : "worker"); newSelection.Generation = generation;
                    grants.Add((newSelection, await client.GetConfigurationAsync(newSelection),
                        await client.CreateConfigurationAccessAsync(new() { Selector = newSelection }), generation == 2 ? 240 : 15));
                    await using var frozenContext = Context(scenario.Connection);
                    var frozenRow = await frozenContext.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == generation);
                    var saved = ConfigurationPlanningSnapshotCodec.Decode(frozenRow.ConfigurationJson,
                        ManifestRevisionRef.Create(scenario.Plan.Inputs.Revision.ApplicationRecordId, ManifestRevisionNumber.Create(frozenRow.RevisionNumber)));
                    Assert.Equal(new[] { "api", generation == 2 ? "scheduler" : "worker" }, saved.Layouts.Select(x => x.Component.Value));
                    Assert.Equal("primary", Assert.Single(saved.Layouts.Single(x => x.Component.Value == "api").BindingRequests).BindingId.Value);
                    Assert.All(saved.Layouts, x => Assert.Equal(1, Assert.Single(x.Documents).Origin.Profile!.Revision));
                    var legacy = DocumentRequest(scenario); legacy.DocumentId = "legacy";
                    Assert.Equal(Haby.Operator.Contracts.ConfigurationDocumentStatus.NotFound, (await client.GetConfigurationAsync(legacy)).Status);
                    var workerGrant = grants.Single(x => x.Selector.ComponentId == "worker" && x.Selector.Generation == 1);
                    var denied = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => new Haby.Configuration.Contracts.ConfigurationDelivery.ConfigurationDeliveryClient(channel)
                        .DeliverAsync(new() { RevisionId = grants[^1].Document.Artifact.RevisionId }, Bearer(workerGrant.Grant.Credential)).ResponseAsync);
                    Assert.Equal(Grpc.Core.StatusCode.PermissionDenied, denied.StatusCode);
                }
                var document = await client.GetConfigurationAsync(selection);
                var grant = await client.CreateConfigurationAccessAsync(new() { Selector = selection });
                grants.Add((selection, document, grant, generation == 2 ? 210 : 220));
                foreach (var item in grants)
                {
                    Assert.True(login == await ReceivedConnection(channel, item.Document, item.Grant, item.Timeout), "Credential identity changed.");
                    var repeat = await client.MaterializeConfigurationAsync(item.Selector);
                    Assert.Equal(item.Document.Artifact, repeat.Artifact);
                }
                Assert.Equal(history, await ReuseHistory(scenario.Connection)); Assert.Equal(external, await ExternalIdentity(scenario));
                await using var data = new NpgsqlConnection(login); await data.OpenAsync();
                Assert.Equal("retained-v1", await new NpgsqlCommand("SELECT value FROM app.reuse_probe WHERE id = 158", data).ExecuteScalarAsync());
            }
            // A new ChangeSet with the same complete effective state is a no-op; its own receipt is immutable.
            var revision3 = await ImportUpdate(client, changedComponents ? ComponentEnvelope(scenario, 3) : UpdateEnvelope(scenario, 3, 220));
            Assert.Equal(secondReceipt, await client.CommitDesiredStateAsync(secondCommit!));
            var noop = await UpdatePlan(client, revision3, 3);
            var receipt = await client.CommitDesiredStateAsync(new() { ChangeSetId = noop.ChangeSetId, PlanId = noop.PlanId });
            Assert.Equal(3, receipt.Commit.Generation);
            Assert.Equal(receipt, await client.CommitDesiredStateAsync(new() { ChangeSetId = noop.ChangeSetId, PlanId = noop.PlanId }));
            await using (var verify = Context(scenario.Connection)) Assert.Equal(3, await verify.DeploymentGenerations.CountAsync());
            Assert.Equal(history, await ReuseHistory(scenario.Connection));
            await host.StopAsync();
        }
        var restored = _catalogPools.Register(new NpgsqlConnectionStringBuilder(await Database("reuse_restore_")));
        var directory = "/tmp/haby-reuse-" + Guid.NewGuid().ToString("N"); var dump = directory + "/catalog.dump";
        await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
        try
        {
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + new NpgsqlConnectionStringBuilder(scenario.Connection).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
            await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + restored, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            foreach (var item in grants) _ = await ReceivedConnection(channel, item.Document, item.Grant, item.Timeout);
            for (var generation = 2; generation <= 3; generation++) Assert.Equal(GenerationExecutionStatus.Succeeded,
                (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = generation })).Status);
            Assert.Equal(history, await ReuseHistory(restored));
            await host.StopAsync();
        }
        finally { await fixture.RunClientUtilityAsync("rm", "-f", dump); await fixture.RunClientUtilityAsync("rmdir", directory); }

        async Task<string> ReceivedConnection(GrpcChannel channel, ConfigurationDocumentReply document, ConfigurationAccessReply grant, int timeout)
        {
            var result = await new Haby.Configuration.Contracts.ConfigurationDelivery.ConfigurationDeliveryClient(channel)
                .DeliverAsync(new() { RevisionId = document.Artifact.RevisionId }, Bearer(grant.Credential));
            using var json = JsonDocument.Parse(result.Json.Memory);
            var settings = new NpgsqlConnectionStringBuilder(json.RootElement.GetProperty("PostgreSQL"u8).GetProperty("DefaultConnection"u8).GetProperty("ConnectionString"u8).GetString());
            Assert.Equal(timeout, settings.Timeout); Assert.Equal(30, settings.CommandTimeout); Assert.Equal(SslMode.Disable, settings.SslMode);
            Assert.Equal(scenario.Database, settings.Database); Assert.Equal(scenario.Role, settings.Username);
            await using (var runtime = new NpgsqlConnection(settings.ConnectionString))
            {
                await runtime.OpenAsync();
                Assert.Equal(scenario.Database, await new NpgsqlCommand("SELECT current_database()", runtime).ExecuteScalarAsync());
                Assert.Equal(scenario.Role, await new NpgsqlCommand("SELECT current_user", runtime).ExecuteScalarAsync());
            }
            settings.Timeout = 200; // Compare the immutable credential and endpoint after independently checking target settings.
            return settings.ConnectionString;
        }
    }

    private static async Task<JsonElement?> NativeReuseCommand(string endpoint, params string[] args)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrEmpty(binary));
        if (binary is null) return null;
        var config = Path.Combine(Path.GetTempPath(), "haby-reuse-" + Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllTextAsync(config, $"formatVersion: 1\ncurrentContext: pilot\ninstances:\n  local:\n    endpoint: {endpoint}\n    token: {OperatorTestCredentials.Credential(endpoint)}\ncontexts:\n  pilot:\n    instance: local\n    environmentId: pilot\n");
        try
        {
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args.Concat(["--config", config, "--output", "json"])) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(deadline.Token); } catch { process.Kill(true); throw; }
            Assert.Equal(0, process.ExitCode); Assert.True(string.IsNullOrEmpty(await stderr), "Native CLI wrote unexpected diagnostics.");
            using var json = JsonDocument.Parse(await stdout); return json.RootElement.Clone();
        }
        finally { File.Delete(config); }
    }
}
