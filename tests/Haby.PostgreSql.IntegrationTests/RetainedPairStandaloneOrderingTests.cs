using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_pair_and_earlier_standalone_have_independent_lifetimes_in_both_removal_orders(bool pairFirst)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var standalone = "retain_order_s_" + Guid.NewGuid().ToString("N");
        var databaseB = "retain_order_b_" + Guid.NewGuid().ToString("N");
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var delivery = new Haby.Configuration.Contracts.ConfigurationDelivery.ConfigurationDeliveryClient(channel);
        var documents = new Dictionary<string, (ConfigurationDocumentReply Document, ConfigurationAccessReply Grant)>();
        var identities = new Dictionary<string, string>(); var originalConnections = new Dictionary<string, string>();
        string? history = null; RevisionReference? lastRevision = null;
        await SaveGrant("api", 1); await Seed(scenario.Database, "A-created-at-1");
        identities.Add(scenario.Database, await Identity(scenario.Database));
        for (var generation = 2; generation <= 8; generation++)
        {
            // S is created BEFORE B. Each removal order converges to A active,
            // S retained from 2 and BOTH B peers retained from 3, then returns.
            var hasPair = generation == 3 || generation == 4 && !pairFirst || generation >= 7;
            var hasStandalone = generation <= 3 || generation == 4 && pairFirst || generation == 8;
            var envelope = hasPair ? IndependentEnvelope(scenario, generation, databaseB) : UpdateEnvelope(scenario, generation);
            if (hasStandalone) envelope["manifest"]!["spec"]!["resources"]!["extra"] =
                MixedEnvelope(scenario, generation, standalone)["manifest"]!["spec"]!["resources"]!["extra"]!.DeepClone();
            var revision = await ImportUpdate(client, envelope); lastRevision = revision;
            var change = await client.CreateChangeSetAsync(new()
            {
                EnvironmentId = "pilot",
                Reference = revision,
                Precondition = PlanningPrecondition.ExpectedGeneration,
                ExpectedGeneration = generation - 1
            });
            var planned = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
            Assert.Equal(PlanningStatus.Planned, planned.Status); var plan = planned.Plan;
            Assert.Equal(generation == 2 ? 1 : generation == 3 ? 5 : 0, plan.Operations.Count);
            Assert.Equal(generation == 2 ? 2 : generation == 3 ? 3 : 2 + (hasPair ? 2 : 0) + (hasStandalone ? 1 : 0), plan.ReusedResources.Count);
            Assert.Equal((generation >= 3 && !hasPair ? 2 : 0) + (!hasStandalone ? 1 : 0), plan.RetainedResources.Count);
            Assert.All(plan.ReusedResources.Concat(plan.RetainedResources), r =>
                Assert.Equal(r.ResourceId == "extra" ? 2 : r.ResourceId is "db-b" or "login-b" ? 3 : 1, r.SourceGeneration));
            if (generation == 7) Assert.All(plan.ReusedResources.Where(r => r.SourceGeneration == 3), r => Assert.True(r.Reintroduced));
            if (generation == 8) Assert.True(plan.ReusedResources.Single(r => r.ResourceId == "extra").Reintroduced);
            Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(new() { PlanId = plan.PlanId })).Status);
            var request = new CommitDesiredStateRequest { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId };
            var accepted = await client.CommitDesiredStateAsync(request); Assert.Equal(CommitStatus.Committed, accepted.Status);
            Assert.Equal(accepted.Commit, (await client.CommitDesiredStateAsync(request)).Commit);
            await using (var verify = Context(scenario.Connection))
            {
                var deployment = await verify.ApplicationDeployments.AsNoTracking().SingleAsync();
                Assert.Equal(generation, deployment.DesiredGeneration); Assert.Equal(generation - 1, deployment.AppliedGeneration);
                var saved = DurableOperationGraphCodec.Decode((await verify.DeploymentGenerations.AsNoTracking().SingleAsync(g => g.Generation == generation)).GraphJson);
                Assert.Equal(plan.RetainedResources.Select(r => r.ResourceId), saved.RetainedResources.Select(r => r.Resource.Value));
                Assert.Equal(plan.ReusedResources.Select(r => r.ManagementId), saved.ReusedResources.Select(r => r.ManagementId.ToString()));
                if (generation >= 4) Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().Where(a => a.Generation == generation).ToArrayAsync());
            }
            // All pre-existing preservation rows are read before any later write.
            await ProbeExisting();
            Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = generation })).Status);
            if (generation == 2) { await Seed(standalone, "S-created-at-2"); identities.Add(standalone, await Identity(standalone)); }
            if (generation == 3) { await SaveGrant("consumer-b", 3); await Seed(databaseB, "B-created-at-3"); identities.Add(databaseB, await Identity(databaseB)); history = await ReuseHistory(scenario.Connection); }
            await ProbeExisting();
            if (generation >= 3) Assert.Equal(history, await ReuseHistory(scenario.Connection));
            await using (var verify = Context(scenario.Connection)) Assert.Equal(generation, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        var noopChange = await client.CreateChangeSetAsync(new()
        {
            EnvironmentId = "pilot",
            Reference = lastRevision,
            Precondition = PlanningPrecondition.ExpectedGeneration,
            ExpectedGeneration = 8
        });
        var noop = (await client.CreatePlanAsync(new() { ChangeSetId = noopChange.ChangeSet.ChangeSetId })).Plan;
        Assert.Empty(noop.Operations);
        Assert.Equal(8, (await client.CommitDesiredStateAsync(new() { ChangeSetId = noop.ChangeSetId, PlanId = noop.PlanId })).Commit.Generation);
        Assert.Equal(history, await ReuseHistory(scenario.Connection)); await ProbeExisting();
        await using (var verify = Context(scenario.Connection))
        { Assert.Equal(8, await verify.DeploymentGenerations.CountAsync()); Assert.Equal(11, await verify.DurableOperationAttempts.CountAsync()); Assert.Equal(5, await verify.ResourceManagement.CountAsync()); }
        await host.StopAsync();

        async Task SaveGrant(string component, int generation)
        {
            var selector = DocumentRequest(scenario, component); selector.Generation = generation;
            var document = await client.MaterializeConfigurationAsync(selector);
            Assert.Equal(Haby.Operator.Contracts.ConfigurationDocumentStatus.Created, document.Status);
            var grant = await client.CreateConfigurationAccessAsync(new() { Selector = selector });
            documents.Add(component == "api" ? scenario.Database : databaseB, (document, grant));
        }
        async Task<string> Connection(string database)
        {
            if (!documents.TryGetValue(database, out var item)) return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
            var result = await delivery.DeliverAsync(new() { RevisionId = item.Document.Artifact.RevisionId }, Bearer(item.Grant.Credential));
            using var json = JsonDocument.Parse(result.Json.Memory);
            var settings = new NpgsqlConnectionStringBuilder(json.RootElement.GetProperty("PostgreSQL"u8).GetProperty("DefaultConnection"u8).GetProperty("ConnectionString"u8).GetString());
            Assert.Equal(database, settings.Database); return settings.ConnectionString;
        }
        async Task Seed(string database, string sentinel)
        {
            var connection = await Connection(database); originalConnections.Add(database, connection);
            await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
            // Called exactly once per database at its creation; no repair/upsert.
            await new NpgsqlCommand("CREATE TABLE public.retention_order_probe(id integer PRIMARY KEY, value text NOT NULL); CREATE TABLE public.retention_order_runtime(id bigint GENERATED ALWAYS AS IDENTITY, value text NOT NULL)", db).ExecuteNonQueryAsync();
            await using var insert = new NpgsqlCommand("INSERT INTO public.retention_order_probe VALUES (170, $1)", db);
            insert.Parameters.AddWithValue(sentinel); await insert.ExecuteNonQueryAsync();
        }
        async Task ProbeExisting()
        {
            foreach (var (database, identity) in identities)
            {
                Assert.Equal(identity, await Identity(database));
                var delivered = new NpgsqlConnectionStringBuilder(await Connection(database));
                var original = new NpgsqlConnectionStringBuilder(originalConnections[database]);
                Assert.True(original.Password == delivered.Password, "Original credential changed.");
                Assert.Equal(original.Username, delivered.Username);
                await using var db = new NpgsqlConnection(originalConnections[database]); await db.OpenAsync();
                Assert.Equal(database == scenario.Database ? "A-created-at-1" : database == standalone ? "S-created-at-2" : "B-created-at-3",
                    await new NpgsqlCommand("SELECT value FROM public.retention_order_probe WHERE id = 170", db).ExecuteScalarAsync());
                await new NpgsqlCommand("INSERT INTO public.retention_order_runtime(value) VALUES (current_user)", db).ExecuteNonQueryAsync();
            }
        }
        async Task<string> Identity(string database)
        {
            await using var db = new NpgsqlConnection(fixture.ConnectionString); await db.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT json_build_array(d.oid, d.datdba, d.datacl, r.oid, r.rolname, r.rolcanlogin)::text FROM pg_database d JOIN pg_roles r ON r.oid = d.datdba WHERE d.datname = $1", db);
            command.Parameters.AddWithValue(database); var value = await command.ExecuteScalarAsync(); Assert.NotNull(value); return (string)value;
        }
    }
}
