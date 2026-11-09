using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    private static JsonObject MixedEnvelope(Scenario scenario, int version, string name)
    {
        var envelope = UpdateEnvelope(scenario, version);
        envelope["manifest"]!["spec"]!["resources"]!["extra"] = new JsonObject
        {
            ["type"] = "postgresql.database/v1alpha1",
            ["providerRef"] = "postgres.primary",
            ["properties"] = new JsonObject { ["name"] = name }
        };
        // Use the exact declared alias, independently of the test fixture's spelling.
        envelope["manifest"]!["spec"]!["resources"]!["extra"]!["providerRef"] =
            envelope["manifest"]!["spec"]!["resources"]!["db"]!["providerRef"]!.DeepClone();
        return envelope;
    }

    private async Task<long?> DatabaseOid(string name)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT oid::bigint FROM pg_database WHERE datname = $1", connection);
        command.Parameters.AddWithValue(name);
        return await command.ExecuteScalarAsync() is long oid ? oid : null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mixed_v1_pair_v2_create_v3_v4_direct_sources_native_delivery_restart_and_restore(bool retain)
    {
        var scenario = await Prepare(full: true, render: true);
        var last = retain ? 5 : 4;
        string? completeHistory = null;
        string? addedIdentity = null;
        var added = "mixed_" + Guid.NewGuid().ToString("N");
        var retained = new List<(ConfigurationDocumentReply Document, ConfigurationAccessReply Grant, int Timeout)>();
        string originalHistory = "", originalIdentity = "", originalLogin = "";
        long? addedOid = null;
        for (var phase = 1; phase <= 2; phase++)
        {
            await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            if (phase == 1)
            {
                Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 1 })).Status);
                originalLogin = await SaveAndReceive(1, 200);
                await Sql(originalLogin, "CREATE TABLE app.mixed_probe(id integer PRIMARY KEY, value text NOT NULL); INSERT INTO app.mixed_probe VALUES (160, 'original-v1')");
                originalHistory = await OriginalHistory(); originalIdentity = await ExternalIdentity(scenario);
                var revision = await ImportUpdate(client, MixedEnvelope(scenario, 2, added));
                var plan = await UpdatePlan(client, revision, 1, host.Urls.Single(), created: 1);
                Assert.Equal("extra", Assert.Single(plan.Operations).ResourceId);
                Assert.Null(await DatabaseOid(added));
                _ = await NativeReuseCommand(host.Urls.Single(), "changeset", "commit", plan.ChangeSetId, "--plan", plan.PlanId);
                Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
                Assert.Null(await DatabaseOid(added));
                await using (var verify = Context(scenario.Connection))
                {
                    var owner = await verify.ApplicationDeployments.AsNoTracking().SingleAsync();
                    Assert.Equal(2, owner.DesiredGeneration); Assert.Equal(1, owner.AppliedGeneration);
                    Assert.Equal("Pending", (await verify.DurableOperations.AsNoTracking().SingleAsync(x => x.Generation == 2)).State);
                    Assert.Equal(2, (await verify.ResourceManagement.AsNoTracking().SingleAsync(x => x.Resource == "extra")).SourceGeneration);
                    Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().Where(x => x.Generation == 2).ToArrayAsync());
                }
            }
            else
            {
                for (var generation = 2; generation <= last; generation++)
                {
                    if (generation > 2)
                    {
                        var absent = retain && generation < 5;
                        var changed = absent ? UpdateEnvelope(scenario, generation) : MixedEnvelope(scenario, generation, added);
                        changed["manifest"]!["spec"]!["components"] = ComponentEnvelope(scenario, generation < last ? 2 : 3)["manifest"]!["spec"]!["components"]!.DeepClone();
                        var revision = await ImportUpdate(client, changed);
                        var plan = await UpdatePlan(client, revision, generation - 1, host.Urls.Single(), added: !absent);
                        Assert.Equal(absent ? 1 : 0, plan.RetainedResources.Count);
                        if (absent) Assert.Equal(2, Assert.Single(plan.RetainedResources).SourceGeneration);
                        Assert.All(plan.ReusedResources, r => Assert.Equal(retain && generation == 5 && r.ResourceId == "extra", r.Reintroduced));
                        _ = await NativeReuseCommand(host.Urls.Single(), "changeset", "commit", plan.ChangeSetId, "--plan", plan.PlanId);
                        Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
                    }
                    var native = await NativeReuseCommand(host.Urls.Single(), "deployment", "generation", "execute", scenario.Commit.DeploymentId.ToString(), generation.ToString());
                    if (native is { } json)
                    {
                        Assert.Equal("succeeded", json.GetProperty("outcome"u8).GetString());
                        Assert.Equal(retain && generation is 3 or 4 ? 1 : 0, json.GetProperty("data"u8).GetProperty("progress"u8).GetProperty("retainedResources"u8).GetInt32());
                        Assert.Equal(retain && generation == 5 ? 1 : 0, json.GetProperty("data"u8).GetProperty("progress"u8).GetProperty("reintroducedResources"u8).GetInt32());
                        Assert.Equal(generation == 2 ? 1 : 0, json.GetProperty("data"u8).GetProperty("progress"u8).GetProperty("total"u8).GetInt32());
                        Assert.Equal(generation == 2 || retain && generation < 5 ? 2 : 3, json.GetProperty("data"u8).GetProperty("progress"u8).GetProperty("reusedResources"u8).GetInt32());
                    }
                    var executed = await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = generation });
                    Assert.Equal(GenerationExecutionStatus.Succeeded, executed.Status); Assert.Equal(generation, executed.Progress.AppliedGeneration);
                    Assert.Equal(generation == 2 ? 1 : 0, executed.Progress.Total);
                    var oid = await DatabaseOid(added); Assert.NotNull(oid); addedOid ??= oid; Assert.Equal(addedOid, oid);
                    if (generation == 2)
                    {
                        var addedConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = added }.ConnectionString;
                        await Sql(addedConnection, "CREATE TABLE retained_probe(id integer PRIMARY KEY, value text NOT NULL); INSERT INTO retained_probe VALUES (164, 'standalone-v2')");
                        completeHistory = await ReuseHistory(scenario.Connection);
                        addedIdentity = await AddedIdentity();
                    }
                    Assert.Equal(completeHistory, await ReuseHistory(scenario.Connection)); Assert.Equal(addedIdentity, await AddedIdentity());
                    await using (var addedData = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = added }.ConnectionString))
                    {
                        await addedData.OpenAsync();
                        Assert.Equal("standalone-v2", await new NpgsqlCommand("SELECT value FROM retained_probe WHERE id = 164", addedData).ExecuteScalarAsync());
                    }
                    Assert.Equal(originalHistory, await OriginalHistory()); Assert.Equal(originalIdentity, await ExternalIdentity(scenario));
                    Assert.True(originalLogin == await SaveAndReceive(generation, generation == last ? 220 : 210), "Existing credential changed.");
                    foreach (var item in retained) Assert.True(originalLogin == await Receive(item.Document, item.Grant, item.Timeout), "Retained grant changed.");
                    await using var data = new NpgsqlConnection(originalLogin); await data.OpenAsync();
                    Assert.Equal("original-v1", await new NpgsqlCommand("SELECT value FROM app.mixed_probe WHERE id = 160", data).ExecuteScalarAsync());
                }
                var same = MixedEnvelope(scenario, last, added);
                same["manifest"]!["spec"]!["components"] = ComponentEnvelope(scenario, 3)["manifest"]!["spec"]!["components"]!.DeepClone();
                var noop = await UpdatePlan(client, await ImportUpdate(client, same), last, added: true);
                var receipt = await client.CommitDesiredStateAsync(new() { ChangeSetId = noop.ChangeSetId, PlanId = noop.PlanId });
                Assert.Equal(CommitStatus.Committed, receipt.Status); Assert.Equal(last, receipt.Commit.Generation);
                await using var verify = Context(scenario.Connection);
                Assert.Equal(last, await verify.DeploymentGenerations.CountAsync());
                Assert.Equal(6, await verify.DurableOperations.CountAsync()); Assert.Equal(6, await verify.DurableOperationAttempts.CountAsync());
                Assert.Equal(5, await verify.OperationOutputs.CountAsync());
                Assert.All(await verify.ResourceManagement.AsNoTracking().ToArrayAsync(), r => Assert.Equal(r.Resource == "extra" ? 2 : 1, r.SourceGeneration));
            }
            await host.StopAsync();

            async Task<string> SaveAndReceive(int generation, int timeout)
            {
                var selector = DocumentRequest(scenario); selector.Generation = generation;
                var batch = await client.MaterializeApplicationConfigurationAsync(new() { Environment = "pilot", ApplicationId = selector.ApplicationId, Generation = generation });
                Assert.Equal(Haby.Operator.Contracts.ConfigurationApplicationStatus.Succeeded, batch.Status);
                var document = await client.GetConfigurationAsync(selector);
                var grant = await client.CreateConfigurationAccessAsync(new() { Selector = selector });
                retained.Add((document, grant, timeout));
                return await Receive(document, grant, timeout);
            }
            async Task<string> Receive(ConfigurationDocumentReply document, ConfigurationAccessReply grant, int timeout)
            {
                var result = await new Haby.Configuration.Contracts.ConfigurationDelivery.ConfigurationDeliveryClient(channel)
                    .DeliverAsync(new() { RevisionId = document.Artifact.RevisionId }, Bearer(grant.Credential));
                using var json = JsonDocument.Parse(result.Json.Memory);
                var settings = new NpgsqlConnectionStringBuilder(json.RootElement.GetProperty("PostgreSQL"u8).GetProperty("DefaultConnection"u8).GetProperty("ConnectionString"u8).GetString());
                Assert.Equal(timeout, settings.Timeout); Assert.Equal(scenario.Database, settings.Database); Assert.Equal(scenario.Role, settings.Username);
                settings.Timeout = 200; return settings.ConnectionString;
            }
        }
        var restored = _catalogPools.Register(new NpgsqlConnectionStringBuilder(await Database("mixed_restore_")));
        var dump = "/tmp/haby-mixed-" + Guid.NewGuid().ToString("N") + ".dump";
        try
        {
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + new NpgsqlConnectionStringBuilder(scenario.Connection).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
            await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + restored, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            for (var generation = 2; generation <= last; generation++) Assert.Equal(GenerationExecutionStatus.Succeeded,
                (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = generation })).Status);
            foreach (var item in retained)
            {
                var delivered = await new Haby.Configuration.Contracts.ConfigurationDelivery.ConfigurationDeliveryClient(channel)
                    .DeliverAsync(new() { RevisionId = item.Document.Artifact.RevisionId }, Bearer(item.Grant.Credential));
                using var json = JsonDocument.Parse(delivered.Json.Memory);
                var settings = new NpgsqlConnectionStringBuilder(json.RootElement.GetProperty("PostgreSQL"u8).GetProperty("DefaultConnection"u8).GetProperty("ConnectionString"u8).GetString());
                Assert.Equal(item.Timeout, settings.Timeout); settings.Timeout = 200;
                Assert.True(originalLogin == settings.ConnectionString, "Restored credential changed.");
            }
            Assert.Equal(completeHistory, await ReuseHistory(restored));
            Assert.Equal(addedOid, await DatabaseOid(added)); await host.StopAsync();
        }
        finally { await fixture.RunClientUtilityAsync("rm", "-f", dump); }

        async Task<string> AddedIdentity()
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT json_build_array(oid, datdba, datacl)::text FROM pg_database WHERE datname = $1", connection);
            command.Parameters.AddWithValue(added); return (string)(await command.ExecuteScalarAsync())!;
        }

        async Task<string> OriginalHistory()
        {
            await using var db = Context(scenario.Connection);
            var operations = await db.DurableOperations.AsNoTracking().Where(x => x.Generation == 1).OrderBy(x => x.OperationId).Select(x => x.OperationId + ":" + x.State + ":" + x.CurrentAttemptId).ToArrayAsync();
            var attempts = await db.DurableOperationAttempts.AsNoTracking().Where(x => x.Generation == 1).OrderBy(x => x.OperationId).Select(x => x.AttemptId + ":" + x.Outcome).ToArrayAsync();
            var outputs = await db.OperationOutputs.AsNoTracking().Where(x => x.Generation == 1).OrderBy(x => x.OperationId).Select(x => x.OperationId + ":" + x.Payload).ToArrayAsync();
            var slots = await db.GeneratedCredentialSlots.AsNoTracking().Select(x => x.SecretId + ":" + x.VersionId).ToArrayAsync();
            return string.Join("\n", operations.Concat(attempts).Concat(outputs).Concat(slots)) + ":" + await db.SecretVersions.CountAsync() + ":" + await db.GeneratedCredentialCreationAudits.CountAsync();
        }
    }
}
