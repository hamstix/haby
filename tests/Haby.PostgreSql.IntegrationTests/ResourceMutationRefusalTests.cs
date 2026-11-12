using System.Text.Json;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    // The original grant and delivered bytes are retained privately, never included in assertions.
    private sealed record RefusalProbe(ConfigurationDocumentReply Document, ConfigurationAccessReply Grant,
        byte[] Original, string Sentinel, string History, string External, string Accepted, string Database, string Role);

    private async Task<RefusalProbe> PrepareRefusalProbe(Scenario scenario, ManifestOperator.ManifestOperatorClient client, GrpcChannel channel,
        ConfigurationDocumentRequest? selector = null, string? database = null, string? role = null)
    {
        selector ??= DocumentRequest(scenario); selector.Generation = selector.Generation == 0 ? 1 : selector.Generation;
        database ??= scenario.Database; role ??= scenario.Role;
        var document = await client.MaterializeConfigurationAsync(selector);
        var grant = await client.CreateConfigurationAccessAsync(new() { Selector = selector });
        var original = await RefusalDelivery(channel, document, grant);
        var sentinel = Guid.NewGuid().ToString("N");
        await using (var runtime = new NpgsqlConnection(RefusalConnection(original)))
        {
            await runtime.OpenAsync();
            await new NpgsqlCommand("CREATE TABLE app.refusal_sentinel(value text NOT NULL)", runtime).ExecuteNonQueryAsync();
            await using var insert = new NpgsqlCommand("INSERT INTO app.refusal_sentinel VALUES ($1)", runtime);
            insert.Parameters.AddWithValue(sentinel); await insert.ExecuteNonQueryAsync();
        }
        return new(document, grant, original, sentinel, await ReuseHistory(scenario.Connection),
            await RefusalExternalIdentity(database), await RefusalAcceptedState(scenario.Connection), database, role);
    }

    private static async Task<byte[]> RefusalDelivery(GrpcChannel channel, ConfigurationDocumentReply document, ConfigurationAccessReply grant) =>
        (await new Haby.Configuration.Contracts.ConfigurationDelivery.ConfigurationDeliveryClient(channel)
            .DeliverAsync(new() { RevisionId = document.Artifact.RevisionId }, Bearer(grant.Credential))).Json.ToByteArray();

    private static string RefusalConnection(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        return json.RootElement.GetProperty("PostgreSQL"u8).GetProperty("DefaultConnection"u8).GetProperty("ConnectionString"u8).GetString()!;
    }

    private async Task VerifyRefusalProbe(Scenario scenario, GrpcChannel channel, RefusalProbe probe)
    {
        var delivered = await RefusalDelivery(channel, probe.Document, probe.Grant);
        Assert.True(probe.Original.AsSpan().SequenceEqual(delivered), "Original exact grant configuration changed.");
        await using (var runtime = new NpgsqlConnection(RefusalConnection(probe.Original)))
        {
            await runtime.OpenAsync();
            // SELECT the original row before any writes. This probe never repairs or reseeds it.
            Assert.Equal(probe.Sentinel, await new NpgsqlCommand("SELECT value FROM app.refusal_sentinel", runtime).ExecuteScalarAsync());
            Assert.Equal(probe.Role, await new NpgsqlCommand("SELECT current_user", runtime).ExecuteScalarAsync());
            Assert.Equal(probe.Database, await new NpgsqlCommand("SELECT current_database()", runtime).ExecuteScalarAsync());
        }
        Assert.Equal(probe.External, await RefusalExternalIdentity(probe.Database));
        Assert.Equal(probe.History, await ReuseHistory(scenario.Connection));
        Assert.Equal(probe.Accepted, await RefusalAcceptedState(scenario.Connection));
    }

    private async Task<string> RefusalExternalIdentity(string database)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT json_build_array(d.oid, d.datdba, d.datacl, r.oid, r.rolname, r.rolcanlogin)::text FROM pg_database d JOIN pg_roles r ON r.oid = d.datdba WHERE d.datname = $1", connection);
        query.Parameters.AddWithValue(database);
        return Assert.IsType<string>(await query.ExecuteScalarAsync());
    }

    private static async Task<string> RefusalAcceptedState(string connection)
    {
        await using var db = Context(connection);
        var owners = await db.ApplicationDeployments.AsNoTracking().Select(x => x.Id + ":" + x.DesiredGeneration + ":" + x.AppliedGeneration).ToArrayAsync();
        var generations = await db.DeploymentGenerations.AsNoTracking().OrderBy(x => x.Generation).Select(x => x.Generation + ":" + x.Fingerprint + ":" + x.GraphJson).ToArrayAsync();
        var commits = await db.ChangeSetCommits.AsNoTracking().OrderBy(x => x.ChangeSetId).Select(x => x.ChangeSetId + ":" + x.PlanId).ToArrayAsync();
        var documents = await db.ConfigurationDocuments.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id + ":" + x.Generation + ":" + x.Payload).ToArrayAsync();
        var grants = await db.ConfigurationAccess.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id + ":" + x.RevisionId + ":" + x.Scope + ":" + x.CreatedAt + ":" + x.ExpiresAt + ":" + x.RevokedAt).ToArrayAsync();
        var slots = await db.GeneratedCredentialSlots.AsNoTracking().OrderBy(x => x.ResourceLocalId).ThenBy(x => x.SlotId)
            .Select(x => x.EnvironmentRecordId + ":" + x.ApplicationRecordId + ":" + x.ResourceLocalId + ":" + x.SlotId + ":" + x.GenerationProfile + ":" + x.SecretId + ":" + x.VersionId).ToArrayAsync();
        var secrets = await db.Secrets.AsNoTracking().Where(x => x.Owner == "Resource").OrderBy(x => x.Id)
            .Select(x => x.Id + ":" + x.EnvironmentRecordId + ":" + x.ApplicationRecordId + ":" + x.ResourceLocalId + ":" + x.Purpose + ":" + x.CurrentVersionId).ToArrayAsync();
        var versions = await db.SecretVersions.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => x.Id + ":" + x.SecretId + ":" + x.Number + ":" + x.ProtectionFormat + ":" + x.FormatVersion + ":" + x.CreatedAt).ToArrayAsync();
        var attempts = await db.DurableOperationAttempts.AsNoTracking().OrderBy(x => x.AttemptId)
            .Select(x => x.AttemptId + ":" + x.Generation + ":" + x.OperationId + ":" + x.AttemptNumber + ":" + x.StartedAt + ":" + x.DispatchedAt + ":" + x.FinishedAt + ":" + x.Outcome).ToArrayAsync();
        var outputs = await db.OperationOutputs.AsNoTracking().OrderBy(x => x.Generation).ThenBy(x => x.OperationId).ThenBy(x => x.Key)
            .Select(x => x.Generation + ":" + x.OperationId + ":" + x.Contract + ":" + x.Key + ":" + x.Payload).ToArrayAsync();
        return string.Join("\n", owners.Concat(generations).Concat(commits).Concat(documents).Concat(grants)
            .Concat(slots).Concat(secrets).Concat(versions).Concat(attempts).Concat(outputs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_switch_stales_saved_plan_and_refuses_new_reuse_without_changing_original_delivery(bool differentInstance)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        RefusalProbe probe; DurablePlanSummary plan; RevisionReference revision;
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        await using (var host = OperatorHostApplication.Build(args))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            probe = await PrepareRefusalProbe(scenario, client, channel);
            revision = await ImportUpdate(client, UpdateEnvelope(scenario, 2));
            plan = await UpdatePlan(client, revision, 1);
            var inputs = scenario.Plan.Inputs;
            var providers = host.Services.GetRequiredService<IProviderConfigurationStore>();
            var original = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(inputs.Environment, inputs.Providers.RequireUniformResourceProvider().Instance, inputs.Providers.RequireUniformResourceProvider().Configuration, default))!;
            var instance = differentInstance ? await providers.CreateInstanceAsync(inputs.Environment, original.Configuration, default) : inputs.Providers.RequireUniformResourceProvider().Instance;
            var number = ProviderConfigurationRevisionNumber.Create(differentInstance ? 1 : 2);
            await providers.AddRevisionAsync(inputs.Environment, instance, number, original.Configuration, default);
            // Real generated administration performs its own valid preflight; identical payload is still a NEW exact binding.
            var switched = await client.SetProviderAliasAsync(new()
            {
                Environment = "pilot",
                Alias = inputs.Providers.RequireUniformResourceProvider().Alias.Value,
                Expected = new() { InstanceId = inputs.Providers.RequireUniformResourceProvider().Instance.ToString(), Revision = inputs.Providers.RequireUniformResourceProvider().Configuration.Value },
                Target = new() { InstanceId = instance.ToString(), Revision = number.Value }
            });
            Assert.Equal(ProviderAccessStatus.Changed, switched.Status);
            await Refuse(client);
            await VerifyRefusalProbe(scenario, channel, probe);
            await host.StopAsync();
        }
        await using var restarted = OperatorHostApplication.Build(args);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(restarted);
        using var restartedChannel = GrpcChannel.ForAddress(restarted.Urls.Single());
        await Refuse(OperatorTestCredentials.Client(restartedChannel));
        await VerifyRefusalProbe(scenario, restartedChannel, probe);
        await restarted.StopAsync();

        async Task Refuse(ManifestOperator.ManifestOperatorClient client)
        {
            Assert.Equal(plan, (await client.GetPlanAsync(new() { PlanId = plan.PlanId })).Plan);
            Assert.Equal(PlanningStatus.PlanStale, (await client.CheckPlanAsync(new() { PlanId = plan.PlanId })).Status);
            Assert.Equal(CommitStatus.PlanStale, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
            var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = 1 });
            var refused = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
            Assert.Equal(PlanningStatus.UnsupportedShape, refused.Status); Assert.Null(refused.Plan);
        }
    }
}
