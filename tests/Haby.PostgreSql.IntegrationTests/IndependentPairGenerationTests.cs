using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    private static JsonObject IndependentEnvelope(Scenario scenario, int version, string database)
    {
        var envelope = UpdateEnvelope(scenario, version);
        var spec = envelope["manifest"]!["spec"]!;
        spec["resources"]!["db-b"] = spec["resources"]!["db"]!.DeepClone();
        spec["resources"]!["db-b"]!["properties"]!["name"] = database;
        spec["resources"]!["login-b"] = spec["resources"]!["app-login"]!.DeepClone();
        spec["resources"]!["login-b"]!["properties"]!["databaseRef"] = "db-b";
        spec["components"]!["consumer-b"] = spec["components"]!["worker"]!.DeepClone();
        spec["components"]!["consumer-b"]!["bindings"]!["storage"]!["target"]!["resourceRef"] = "login-b";
        spec["components"]!["consumer-b"]!["bindings"]!["storage"]!["options"] = new JsonObject { ["timeout"] = 73 };
        return envelope;
    }

    private static async Task<DurablePlanSummary> IndependentPlan(ManifestOperator.ManifestOperatorClient client,
        RevisionReference revision, int expected, string endpoint, int created)
    {
        var native = await NativeReuseCommand(endpoint, "changeset", "create", revision.ApplicationRecordId,
            revision.RevisionNumber.ToString(), "--expected-generation", expected.ToString());
        var change = native is { } n ? await client.GetChangeSetAsync(new() { ChangeSetId = n.GetProperty("data"u8).GetProperty("changeSet"u8).GetProperty("changeSetId"u8).GetString()! }) :
            await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = expected });
        var planned = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
        Assert.Equal(PlanningStatus.Planned, planned.Status);
        Assert.Equal(created, planned.Plan.Operations.Count);
        Assert.Equal(expected == 1 ? 2 : 4, planned.Plan.ReusedResources.Count);
        Assert.All(planned.Plan.ReusedResources, r => Assert.Equal(r.ResourceId is "db-b" or "login-b" ? 2 : 1, r.SourceGeneration));
        Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(new() { PlanId = planned.Plan.PlanId })).Status);
        _ = await NativeReuseCommand(endpoint, "plan", "get", planned.Plan.PlanId);
        return planned.Plan;
    }

    [Fact]
    public async Task Independent_pair_native_runtime_direct_sources_configuration_only_restart_restore_and_old_exact_grants()
    {
        const string sentinelA = "pair-a-before-generation-2";
        const string sentinelB = "pair-b-created-in-generation-2";
        var scenario = await Prepare(full: true, render: true);
        var databaseB = "pair_b_" + Guid.NewGuid().ToString("N");
        var roleB = PostgreSqlPairResourceAdmission.RoleName(scenario.Owner with { ResourceLocalId = ManifestLocalId.Create("login-b") });
        var retained = new List<(ConfigurationDocumentReply Document, ConfigurationAccessReply Grant, bool B, int Timeout, int Command)>();
        string? identityA = null, historyA = null, completeHistory = null, identityB = null;
        RevisionReference? revision = null;
        for (var phase = 1; phase <= 2; phase++)
        {
            await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            if (phase == 1)
            {
                Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 1 })).Status);
                await Save(1, false, 200, 30, seed: true);
                identityA = await ExternalIdentity(scenario); historyA = await OriginalPairHistory();
                revision = await ImportUpdate(client, IndependentEnvelope(scenario, 2, databaseB));
                var plan = await IndependentPlan(client, revision, 1, host.Urls.Single(), 5);
                Assert.Equal(new[] { "db-b-create", "db-b-restrict", "login-b-access", "login-b-create", "login-b-material" }, plan.Operations.Select(o => o.OperationId));
                await Commit(plan);
                Assert.Null(await DatabaseOid(databaseB));
                await using var verify = Context(scenario.Connection);
                Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
                Assert.Equal(2, await verify.ResourceManagement.CountAsync(r => r.SourceGeneration == 2));
                Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().Where(x => x.Generation == 2).ToArrayAsync());
            }
            else
            {
                for (var generation = 2; generation <= 4; generation++)
                {
                    if (generation > 2)
                    {
                        if (generation == 3) revision = await ImportUpdate(client, IndependentEnvelope(scenario, 3, databaseB));
                        else _ = await SelectConfigurationRevision(client, scenario, 2);
                        await Commit(await IndependentPlan(client, revision!, generation - 1, host.Urls.Single(), 0));
                    }
                    var native = await NativeReuseCommand(host.Urls.Single(), "deployment", "generation", "execute", scenario.Commit.DeploymentId.ToString(), generation.ToString());
                    if (native is { } n) Assert.Equal(generation == 2 ? 5 : 0, n.GetProperty("data"u8).GetProperty("progress"u8).GetProperty("total"u8).GetInt32());
                    var executed = await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = generation });
                    Assert.Equal(GenerationExecutionStatus.Succeeded, executed.Status); Assert.Equal(generation, executed.Progress.AppliedGeneration);
                    Assert.Equal(identityA, await ExternalIdentity(scenario)); Assert.Equal(historyA, await OriginalPairHistory());
                    _ = await NativeReuseCommand(host.Urls.Single(), "configuration", "materialize", DocumentRequest(scenario).ApplicationId, "--all", "--generation", generation.ToString());
                    var batch = await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = DocumentRequest(scenario).ApplicationId, Generation = generation });
                    Assert.Equal(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, batch.Status); Assert.Equal(3, batch.Items.Count);
                    await Save(generation, false, 210, generation == 4 ? 40 : 30);
                    await Save(generation, true, 73, generation == 4 ? 40 : 30, seed: generation == 2);
                    foreach (var item in retained) await Receive(channel, item);
                    completeHistory ??= await ReuseHistory(scenario.Connection);
                    Assert.Equal(completeHistory, await ReuseHistory(scenario.Connection));
                    identityB ??= await PairBIdentity(); Assert.Equal(identityB, await PairBIdentity());
                    await using (var verify = Context(scenario.Connection))
                    {
                        var slots = await verify.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync();
                        Assert.Equal(2, slots.Length); Assert.Equal(2, slots.Select(s => s.SecretId).Distinct().Count());
                        Assert.Equal(2, slots.Select(s => s.VersionId).Distinct().Count());
                        Assert.Equal(10, await verify.DurableOperations.CountAsync());
                        var saved = await verify.DeploymentGenerations.AsNoTracking().SingleAsync(g => g.Generation == generation);
                        var frozen = ConfigurationPlanningSnapshotCodec.Decode(saved.ConfigurationJson,
                            ManifestRevisionRef.Create(scenario.Plan.Inputs.Revision.ApplicationRecordId, ManifestRevisionNumber.Create(saved.RevisionNumber)));
                        Assert.Equal("app-login-access", frozen.RenderPins.Single(p => p.Component.Value == "api").Producer.Value);
                        Assert.Equal("login-b-access", frozen.RenderPins.Single(p => p.Component.Value == "consumer-b").Producer.Value);
                        Assert.Single(frozen.RenderPins.Select(p => p.Renderer).Distinct());
                        if (generation == 4) Assert.Equal(revision!.RevisionNumber, saved.RevisionNumber);
                    }
                    var denial = await Assert.ThrowsAsync<RpcException>(async () => await new Haby.Configuration.Contracts.ConfigurationDelivery.ConfigurationDeliveryClient(channel)
                        .DeliverAsync(new() { RevisionId = retained.Last(x => x.B).Document.Artifact.RevisionId }, Bearer(retained[0].Grant.Credential)));
                    Assert.Equal(StatusCode.PermissionDenied, denial.StatusCode);
                }
            }
            if (phase == 2)
            {
                var noop = await IndependentPlan(client, revision!, 4, host.Urls.Single(), 0);
                var receipt = await client.CommitDesiredStateAsync(new() { ChangeSetId = noop.ChangeSetId, PlanId = noop.PlanId });
                Assert.Equal(CommitStatus.Committed, receipt.Status); Assert.Equal(4, receipt.Commit.Generation);
                Assert.Equal(completeHistory, await ReuseHistory(scenario.Connection));
            }
            await host.StopAsync();

            async Task Commit(DurablePlanSummary plan)
            {
                _ = await NativeReuseCommand(host.Urls.Single(), "changeset", "commit", plan.ChangeSetId, "--plan", plan.PlanId);
                Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
            }
            async Task Save(int generation, bool b, int timeout, int command, bool seed = false)
            {
                var selector = DocumentRequest(scenario, b ? "consumer-b" : "api"); selector.Generation = generation;
                var doc = await client.MaterializeConfigurationAsync(selector);
                Assert.Contains(doc.Status, new[] { Haby.Operator.Contracts.ConfigurationDocumentStatus.Created, Haby.Operator.Contracts.ConfigurationDocumentStatus.Existing });
                var grant = await client.CreateConfigurationAccessAsync(new() { Selector = selector });
                var item = (doc, grant, b, timeout, command);
                if (seed) await Seed(channel, item);
                retained.Add(item); await Receive(channel, item);
            }
        }
        var restored = _catalogPools.Register(new NpgsqlConnectionStringBuilder(await Database("pair_restore_")));
        var directory = "/tmp/haby-pairs-" + Guid.NewGuid().ToString("N"); var dump = directory + "/catalog.dump";
        await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
        try
        {
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + new NpgsqlConnectionStringBuilder(scenario.Connection).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
            await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + restored, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            foreach (var item in retained) await Receive(channel, item);
            for (var g = 2; g <= 4; g++) Assert.Equal(GenerationExecutionStatus.Succeeded,
                (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = g })).Status);
            Assert.Equal(completeHistory, await ReuseHistory(restored));
            await host.StopAsync();
        }
        finally { await fixture.RunClientUtilityAsync("rm", "-f", dump); await fixture.RunClientUtilityAsync("rmdir", directory); }

        async Task<NpgsqlConnectionStringBuilder> DeliveredSettings(GrpcChannel channel, (ConfigurationDocumentReply Document, ConfigurationAccessReply Grant, bool B, int Timeout, int Command) item)
        {
            var result = await new Haby.Configuration.Contracts.ConfigurationDelivery.ConfigurationDeliveryClient(channel)
                .DeliverAsync(new() { RevisionId = item.Document.Artifact.RevisionId }, Bearer(item.Grant.Credential));
            using var json = JsonDocument.Parse(result.Json.Memory);
            var settings = new NpgsqlConnectionStringBuilder(json.RootElement.GetProperty("PostgreSQL"u8).GetProperty("DefaultConnection"u8).GetProperty("ConnectionString"u8).GetString());
            var database = item.B ? databaseB : scenario.Database; var role = item.B ? roleB : scenario.Role;
            Assert.Equal(database, settings.Database); Assert.Equal(role, settings.Username);
            Assert.Equal(item.Timeout, settings.Timeout); Assert.Equal(item.Command, settings.CommandTimeout); Assert.Equal(SslMode.Disable, settings.SslMode);
            return settings;
        }
        async Task Seed(GrpcChannel channel, (ConfigurationDocumentReply Document, ConfigurationAccessReply Grant, bool B, int Timeout, int Command) item)
        {
            var settings = await DeliveredSettings(channel, item);
            await using var runtime = new NpgsqlConnection(settings.ConnectionString); await runtime.OpenAsync();
            // Each pair is seeded only at its creation generation; repeat checks never repair data.
            await new NpgsqlCommand("CREATE TABLE app.pair_probe(id integer PRIMARY KEY, value text NOT NULL); CREATE TABLE app.pair_runtime_writes(id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, value text NOT NULL)", runtime).ExecuteNonQueryAsync();
            await using var insert = new NpgsqlCommand("INSERT INTO app.pair_probe VALUES (168, $1)", runtime);
            insert.Parameters.AddWithValue(item.B ? sentinelB : sentinelA);
            await insert.ExecuteNonQueryAsync();
        }
        async Task Receive(GrpcChannel channel, (ConfigurationDocumentReply Document, ConfigurationAccessReply Grant, bool B, int Timeout, int Command) item)
        {
            var settings = await DeliveredSettings(channel, item);
            var role = item.B ? roleB : scenario.Role;
            await using var runtime = new NpgsqlConnection(settings.ConnectionString); await runtime.OpenAsync();
            Assert.Equal(role, await new NpgsqlCommand("SELECT current_user", runtime).ExecuteScalarAsync());
            Assert.Equal(role, await new NpgsqlCommand("SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'app'", runtime).ExecuteScalarAsync());
            // Read the original sentinel before any write, including old-grant and restore checks.
            Assert.Equal(item.B ? sentinelB : sentinelA, await new NpgsqlCommand("SELECT value FROM app.pair_probe WHERE id = 168", runtime).ExecuteScalarAsync());
            // Runtime write capability is a separate probe and cannot replace the preserved row.
            Assert.Equal(role, await new NpgsqlCommand("INSERT INTO app.pair_runtime_writes(value) VALUES (current_user) RETURNING value", runtime).ExecuteScalarAsync());
            if (await DatabaseOid(databaseB) is not null)
            {
                await using var query = new NpgsqlCommand("SELECT has_database_privilege(current_user, $1, 'CONNECT')", runtime);
                query.Parameters.AddWithValue(item.B ? scenario.Database : databaseB);
                Assert.Equal(false, await query.ExecuteScalarAsync());
                settings.Database = item.B ? scenario.Database : databaseB; settings.Pooling = false;
                var refused = false;
                try { await using var cross = new NpgsqlConnection(settings.ConnectionString); await cross.OpenAsync(); }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { refused = true; }
                Assert.True(refused, "Cross-pair database access was admitted.");
            }
        }
        async Task<string> PairBIdentity()
        {
            await using var db = new NpgsqlConnection(fixture.ConnectionString); await db.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT json_build_array(d.oid, d.datdba, d.datacl, r.oid, r.rolname)::text FROM pg_database d JOIN pg_roles r ON r.oid = d.datdba WHERE d.datname = $1 AND r.rolcanlogin AND NOT (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls) AND r.rolname = $2 AND NOT EXISTS (SELECT 1 FROM pg_auth_members WHERE member = r.oid)", db);
            query.Parameters.AddWithValue(databaseB); query.Parameters.AddWithValue(roleB); var value = await query.ExecuteScalarAsync(); Assert.NotNull(value); return (string)value;
        }
        async Task<string> OriginalPairHistory()
        {
            await using var db = Context(scenario.Connection);
            var nodes = await db.DurableOperations.AsNoTracking().Where(x => x.Generation == 1).OrderBy(x => x.OperationId).Select(x => x.OperationId + ":" + x.State + ":" + x.CurrentAttemptId).ToArrayAsync();
            var attempts = await db.DurableOperationAttempts.AsNoTracking().Where(x => x.Generation == 1).OrderBy(x => x.OperationId).Select(x => x.AttemptId + ":" + x.Outcome).ToArrayAsync();
            var outputs = await db.OperationOutputs.AsNoTracking().Where(x => x.Generation == 1).OrderBy(x => x.OperationId).Select(x => x.OperationId + ":" + x.Payload).ToArrayAsync();
            return string.Join("\n", nodes.Concat(attempts).Concat(outputs));
        }
    }
    [Fact]
    public async Task Independent_pairs_coexist_with_standalone_addition_retention_and_reintroduction()
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var b = "pair_coexist_" + Guid.NewGuid().ToString("N"); var extra = "pair_extra_" + Guid.NewGuid().ToString("N");
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        long? oid = null; string? identity = null;
        for (var generation = 2; generation <= 5; generation++)
        {
            var envelope = IndependentEnvelope(scenario, generation, b);
            if (generation is 3 or 5) envelope["manifest"]!["spec"]!["resources"]!["extra"] = new JsonObject
            {
                ["type"] = "postgresql.database/v1alpha1", ["providerRef"] = "postgresql.primary",
                ["properties"] = new JsonObject { ["name"] = extra }
            };
            var reference = await ImportUpdate(client, envelope);
            var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = reference, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = generation - 1 });
            var planned = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
            Assert.Equal(PlanningStatus.Planned, planned.Status); var plan = planned.Plan;
            Assert.Equal(generation == 2 ? 5 : generation == 3 ? 1 : 0, plan.Operations.Count);
            Assert.All(plan.ReusedResources, r => Assert.Equal(r.ResourceId == "extra" ? 3 : r.ResourceId is "db-b" or "login-b" ? 2 : 1, r.SourceGeneration));
            Assert.Equal(generation == 4 ? 1 : 0, plan.RetainedResources.Count);
            Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
            Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = generation })).Status);
            var batch = await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = DocumentRequest(scenario).ApplicationId, Generation = generation });
            Assert.Equal(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, batch.Status);
            identity ??= await ExternalIdentity(scenario); Assert.Equal(identity, await ExternalIdentity(scenario));
            if (generation >= 3) { oid ??= await DatabaseOid(extra); Assert.NotNull(oid); Assert.Equal(oid, await DatabaseOid(extra)); }
            if (generation == 5) Assert.True(plan.ReusedResources.Single(r => r.ResourceId == "extra").Reintroduced);
        }
        await using var verify = Context(scenario.Connection);
        Assert.Equal(11, await verify.DurableOperationAttempts.CountAsync());
        Assert.Equal(5, await verify.ResourceManagement.CountAsync());
        await host.StopAsync();
    }

}
