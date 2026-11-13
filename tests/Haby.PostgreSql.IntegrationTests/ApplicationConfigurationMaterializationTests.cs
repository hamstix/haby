using System.Diagnostics;
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BatchStatus = Haby.Operator.Contracts.ConfigurationApplicationStatus;
using ItemStatus = Haby.Operator.Contracts.ConfigurationDocumentStatus;

namespace Haby.PostgreSql.IntegrationTests;

public abstract partial class ApplicationLoginScenario
{
    private protected static string BatchProfile(int extra = 1)
    {
        var profile = JsonNode.Parse(RenderingProfile)!;
        for (var i = extra - 1; i >= 0; i--)
            profile["spec"]!["documents"]!["a-static-" + i.ToString("D3")] = new JsonObject
            { ["fileName"] = "static-" + i + ".json", ["format"] = "json", ["template"] = new JsonObject { ["ordinary"] = true } };
        return profile.ToJsonString();
    }
    private protected static ConfigurationApplicationRequest BatchRequest(Scenario scenario, bool exact = false)
    {
        var request = new ConfigurationApplicationRequest { Environment = "pilot", ApplicationId = DocumentRequest(scenario).ApplicationId };
        if (exact) request.Generation = 1;
        return request;
    }
    private protected static async Task NativeBatch(string endpoint, ConfigurationApplicationRequest request, string expected)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrEmpty(binary));
        if (binary is null) return;
        var config = Path.Combine(Path.GetTempPath(), "haby-batch-" + Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllTextAsync(config, $"formatVersion: 1\ncurrentContext: pilot\ninstances:\n  local:\n    endpoint: {endpoint}\n    token: {OperatorTestCredentials.Credential(endpoint)}\ncontexts:\n  pilot:\n    instance: local\n    environmentId: pilot\n");
        try
        {
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "configuration", "materialize", request.ApplicationId, "--all", "--config", config, "--output", "json" }) start.ArgumentList.Add(arg);
            if (request.HasGeneration) { start.ArgumentList.Add("--generation"); start.ArgumentList.Add(request.Generation.ToString()); }
            using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
            Assert.Equal(0, process.ExitCode); Assert.Equal("", await stderr);
            using var doc = JsonDocument.Parse(await stdout); Assert.Equal("succeeded", doc.RootElement.GetProperty("outcome"u8).GetString());
            var data = doc.RootElement.GetProperty("data"u8); Assert.Equal(1, data.GetProperty("target"u8).GetProperty("generation"u8).GetInt64());
            var items = data.GetProperty("items"u8).EnumerateArray().ToArray(); Assert.Equal(4, items.Length);
            Assert.All(items, x => Assert.Equal(expected, x.GetProperty("outcome"u8).GetString()));
        }
        finally { File.Delete(config); }
    }
    private protected sealed class BatchSelectionPause(IConfigurationApplicationInventory inner) : IConfigurationApplicationInventory
    {
        internal readonly TaskCompletionSource Selected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public async ValueTask<ConfigurationApplicationInventory> SelectApplicationAsync(ConfigurationApplicationSelection selection, CancellationToken token)
        {
            Calls++; var selected = await inner.SelectApplicationAsync(selection, token);
            Selected.TrySetResult(); await Resume.Task.WaitAsync(TimeSpan.FromSeconds(30), token); return selected;
        }
    }
    private protected sealed class BatchCommitPause(IConfigurationDocumentStore inner) : IConfigurationDocumentStore
    {
        internal readonly TaskCompletionSource Committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public ValueTask<ConfigurationDocumentResult> SelectAsync(ConfigurationDocumentSelection selection, CancellationToken token) => inner.SelectAsync(selection, token);
        public ValueTask<ConfigurationDocumentResult> ReadAsync(ConfigurationDocumentTarget target, CancellationToken token) => inner.ReadAsync(target, token);
        public async ValueTask<ConfigurationDocumentResult> MaterializeAsync(ConfigurationDocumentTarget target, string actor, CancellationToken token)
        {
            Calls++; var result = await inner.MaterializeAsync(target, actor, token);
            if (Calls == 1)
            {
                Committed.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Canceled.TrySetResult(); }
            }
            return result;
        }
    }

}

[Collection("Login logging isolated")]
public sealed partial class ApplicationConfigurationTests(PostgreSqlFixture fixture) : ApplicationLoginScenario(fixture), IClassFixture<PostgreSqlFixture>
{

    [Fact]
    public async Task Application_batch_second_failure_preserves_prefix_then_same_generation_retry_repairs_and_keeps_original_audit()
    {
        var scenario = await Prepare(full: true, render: true, renderingProfile: BatchProfile());
        await AccessReady(scenario); await AccessExecute(scenario);
        ConfigurationApplicationReply first;
        await using (var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"],
            services => services.AddSingleton(ConfigurationRenderingCatalog.Empty)))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            first = await OperatorTestCredentials.Client(channel).MaterializeApplicationConfigurationAsync(BatchRequest(scenario));
            Assert.Equal(BatchStatus.Failed, first.Status); Assert.Equal(4, first.SelectedCount);
            Assert.Equal(new[] { ("api", "a-static-000"), ("api", "settings"), ("worker", "a-static-000"), ("worker", "settings") },
                first.Items.Select(x => (x.Target.ComponentId, x.Target.DocumentId)));
            Assert.Equal(new[] { ItemStatus.Created, ItemStatus.InputUnavailable, ItemStatus.NotAttempted, ItemStatus.NotAttempted }, first.Items.Select(x => x.Status));
            await using var verify = Context(scenario.Connection);
            var saved = Assert.Single(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync());
            Assert.Equal(first.Items[0].Artifact.RevisionId, saved.Id.ToString());
            Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
            await host.StopAsync();
        }
        var before = await ConfigurationState(scenario);
        await using var repaired = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(repaired); using var retryChannel = GrpcChannel.ForAddress(repaired.Urls.Single());
        var client = OperatorTestCredentials.Client(retryChannel);
        var retry = await client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario, true));
        Assert.Equal(BatchStatus.Succeeded, retry.Status);
        Assert.Equal(new[] { ItemStatus.Existing, ItemStatus.Created, ItemStatus.Created, ItemStatus.Created }, retry.Items.Select(x => x.Status));
        Assert.Equal(first.Items[0].Artifact, retry.Items[0].Artifact);
        var repeat = await client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario, true));
        Assert.All(repeat.Items, x => Assert.Equal(ItemStatus.Existing, x.Status));
        Assert.Equal(retry.Items.Select(x => x.Artifact), repeat.Items.Select(x => x.Artifact));
        Assert.Equal(before, await ConfigurationState(scenario));
        await using (var verify = Context(scenario.Connection)) Assert.Equal(4, await verify.ConfigurationDocuments.AsNoTracking().CountAsync());
        await repaired.StopAsync();
    }

    [Fact]
    public async Task Application_batch_overlapping_batches_and_single_document_converge_on_immutable_winners()
    {
        var scenario = await Prepare(full: true, render: true, renderingProfile: BatchProfile());
        await AccessReady(scenario); await AccessExecute(scenario); var barrier = new OutputBarrier(3);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
            services.AddSingleton<ComposeConfigurationDocument>(p => new(PostgreSqlConnectionRendering.CreateCatalog(), PostgreSqlConnectionRendering.FormatterCatalog(), barrier.Wrap(p.GetRequiredService<IConfigurationOutputEvidence>()))));
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var one = client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario, true)).ResponseAsync;
        var two = client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario, true)).ResponseAsync;
        var single = client.MaterializeConfigurationAsync(DocumentRequest(scenario, exact: true)).ResponseAsync;
        await Task.WhenAll(one, two, single);
        var firstBatch = await one; var secondBatch = await two; var singleReply = await single;
        Assert.Equal(3, barrier.Count); Assert.Equal(BatchStatus.Succeeded, firstBatch.Status); Assert.Equal(BatchStatus.Succeeded, secondBatch.Status);
        Assert.Equal(firstBatch.Items.Select(x => x.Artifact), secondBatch.Items.Select(x => x.Artifact));
        Assert.Equal(singleReply.Artifact, firstBatch.Items.Single(x => x.Target.ComponentId == "api" && x.Target.DocumentId == "settings").Artifact);
        await using var verify = Context(scenario.Connection); Assert.Equal(4, await verify.ConfigurationDocuments.AsNoTracking().CountAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task Application_batch_published_native_one_call_creates_every_frozen_document_with_shared_secret_and_distinct_timeouts()
    {
        var scenario = await Prepare(full: true, render: true, renderingProfile: BatchProfile());
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        Assert.Equal(BatchStatus.NotApplied, (await client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario))).Status);
        Assert.Equal(BatchStatus.NotApplied, (await client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario, true))).Status);
        Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 1 })).Status);
        await NativeBatch(host.Urls.Single(), BatchRequest(scenario), "created");
        // Managed fallback keeps the same actual Host test useful when native is not requested.
        var result = await client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario));
        Assert.Equal(BatchStatus.Succeeded, result.Status); Assert.Equal(4, result.Items.Count);
        await NativeBatch(host.Urls.Single(), BatchRequest(scenario, true), "existing");
        var values = new List<ConfigurationDeferredValue>();
        await using (var verify = Context(scenario.Connection))
        {
            var rows = await verify.ConfigurationDocuments.AsNoTracking().Where(x => x.Document == "settings").OrderBy(x => x.Component).ToArrayAsync();
            Assert.Equal(2, rows.Length);
            foreach (var row in rows)
            {
                var tree = Assert.IsType<ConfigurationObject>(ConfigurationDocumentCodec.Decode(row.Payload, row.Fingerprint).Tree);
                values.Add(Assert.IsType<ConfigurationDeferredValue>(Assert.IsType<ConfigurationObject>(Assert.IsType<ConfigurationObject>(tree.Members["PostgreSQL"]).Members["DefaultConnection"]).Members["ConnectionString"]));
            }
            Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
        }
        Assert.Equal(values[0].Arguments.Members["password"], values[1].Arguments.Members["password"]);
        Assert.Equal(ConfigurationNumber.Parse("200"), Assert.IsType<ConfigurationObject>(values[0].Arguments.Members["settings"]).Members["timeout"]);
        Assert.Equal(ConfigurationNumber.Parse("15"), Assert.IsType<ConfigurationObject>(values[1].Arguments.Members["settings"]).Members["timeout"]);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("count")]
    [InlineData("corrupt")]
    [InlineData("missing")]
    public async Task Application_batch_inventory_refusals_precede_writes_and_empty_layout_succeeds(string mode)
    {
        var scenario = await Prepare(full: true, render: mode != "empty", configureManifest: mode == "count" ? manifest =>
        {
            var docs = new JsonObject();
            for (var i = 0; i < 511; i++) docs["d" + i.ToString("D3")] = new JsonObject
            { ["fileName"] = "d" + i + ".json", ["format"] = "json", ["template"] = new JsonObject() };
            manifest["manifest"]!["spec"]!["components"]!["api"]!["documents"] = docs;
        }
        : null);
        await AccessReady(scenario); await AccessExecute(scenario);
        if (mode == "corrupt") await Sql(scenario.Connection, "SET session_replication_role = replica; UPDATE application_deployment_generations SET configuration_json = '{}' ");
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var request = BatchRequest(scenario); if (mode == "missing") request.ApplicationId = "absent";
        var result = await client.MaterializeApplicationConfigurationAsync(request);
        Assert.Equal(mode switch { "empty" => BatchStatus.Succeeded, "count" => BatchStatus.LimitExceeded, "corrupt" => BatchStatus.InputCorrupt, _ => BatchStatus.NotFound }, result.Status);
        Assert.Empty(result.Items);
        await using var verify = Context(scenario.Connection); Assert.Empty(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync());
        await host.StopAsync();
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public async Task Application_batch_existing_metadata_byte_reservation_is_checked_before_other_documents_are_written(int length, bool allowed)
    {
        var scenario = await Prepare(full: true, render: true, renderingProfile: BatchProfile());
        await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var original = await client.MaterializeConfigurationAsync(DocumentRequest(scenario)); Assert.Equal(ItemStatus.Created, original.Status);
        ConfigurationDocumentRow row;
        await using (var read = Context(scenario.Connection)) row = await read.ConfigurationDocuments.AsNoTracking().SingleAsync();
        var decoded = ConfigurationDocumentCodec.Decode(row.Payload, row.Fingerprint);
        var replacement = decoded with { Metadata = decoded.Metadata with { Actor = new string('\uFFFF', length) } };
        var payload = ConfigurationDocumentCodec.Encode(replacement); var fingerprint = ConfigurationDocumentCodec.Fingerprint(payload);
        // Controlled fixture corruption with a consistent codec hash, not a production mutation API.
        await using (var arrange = Context(scenario.Connection))
        {
            await arrange.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
            await arrange.Database.ExecuteSqlInterpolatedAsync($"UPDATE configuration_document_revisions SET payload = {payload}, fingerprint = {fingerprint} WHERE id = {row.Id}");
        }
        var reply = await client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario, true));
        Assert.Equal(allowed ? BatchStatus.Succeeded : BatchStatus.InputCorrupt, reply.Status);
        await using var verify = Context(scenario.Connection);
        Assert.Equal(allowed ? 4 : 1, await verify.ConfigurationDocuments.AsNoTracking().CountAsync());
        Assert.Equal(payload, (await verify.ConfigurationDocuments.AsNoTracking().SingleAsync(x => x.Id == row.Id)).Payload);
        await host.StopAsync();
    }

    [Fact]
    public async Task Application_batch_freezes_inventory_once_despite_current_desired_and_assignment_changes()
    {
        var scenario = await Prepare(full: true, render: true, renderingProfile: BatchProfile());
        await AccessReady(scenario); await AccessExecute(scenario);
        BatchSelectionPause? pause = null;
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
            services.AddSingleton<IConfigurationApplicationInventory>(p => pause = new(new PostgreSqlConfigurationDocumentStore(
                p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), p.GetRequiredService<ComposeConfigurationDocument>(), TimeProvider.System))));
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        _ = host.Services.GetRequiredService<IConfigurationApplicationInventory>();
        var pending = client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario)).ResponseAsync;
        await pause!.Selected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // Only this disposable fixture relaxes the current generation-1 constraint.
        await Sql(scenario.Connection, "ALTER TABLE application_deployments DROP CONSTRAINT \"CK_application_deployments_desired_generation\"; UPDATE application_deployments SET desired_generation = 2");
        // A different current assignment cannot influence the saved generation's layout.
        await using (var services = OwnerServices(scenario))
        {
            var env = (await services.GetRequiredService<IEnvironmentCatalog>().GetAsync(EnvironmentId.Create("pilot"), default))!;
            var store = services.GetRequiredService<IProfileAssignmentStore>();
            var target = new Haby.Domain.ProfileAssignmentTarget.Environment();
            var current = await store.FindAsync(env, target, default);
            Assert.Equal(AssignmentOutcome.Updated, (await store.ApplyAsync(env, new(target, []), new AssignmentPrecondition.ExpectedConcurrencyToken(current!.Token), new("test-change", DateTimeOffset.UtcNow, null), default)).Outcome);
        }
        pause.Resume.TrySetResult();
        var reply = await pending;
        Assert.Equal(BatchStatus.Succeeded, reply.Status); Assert.Equal(1, reply.Target.Generation); Assert.Equal(4, reply.Items.Count); Assert.Equal(1, pause.Calls);
        Assert.All(reply.Items, i => Assert.Equal(1, i.Target.Generation));
        Assert.Equal(BatchStatus.NotApplied, (await client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario))).Status);
        var exact = await client.MaterializeApplicationConfigurationAsync(BatchRequest(scenario, true));
        Assert.Equal(reply.Items.Select(i => i.Artifact), exact.Items.Select(i => i.Artifact));
        await host.StopAsync();
    }

    [Fact]
    public async Task Application_batch_canceled_lost_response_retains_committed_prefix_and_restart_exact_retry_converges()
    {
        var scenario = await Prepare(full: true, render: true, renderingProfile: BatchProfile());
        await AccessReady(scenario); await AccessExecute(scenario);
        ConfigurationDocumentRow saved;
        BatchCommitPause? pause = null;
        await using (var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
            services.AddSingleton<IConfigurationDocumentStore>(p => pause = new(new PostgreSqlConfigurationDocumentStore(
                p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), p.GetRequiredService<ComposeConfigurationDocument>(), TimeProvider.System)))))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            using var cts = new CancellationTokenSource();
            _ = host.Services.GetRequiredService<IConfigurationDocumentStore>();
            var pending = OperatorTestCredentials.Client(channel).MaterializeApplicationConfigurationAsync(BatchRequest(scenario, true), cancellationToken: cts.Token).ResponseAsync;
            await pause!.Committed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using (var verify = Context(scenario.Connection)) saved = Assert.Single(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync());
            cts.Cancel(); var error = await Assert.ThrowsAsync<RpcException>(() => pending); Assert.Equal(StatusCode.Cancelled, error.StatusCode);
            await pause.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(30)); Assert.Equal(1, pause.Calls);
            await using (var verify = Context(scenario.Connection)) Assert.Single(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync());
            await host.StopAsync();
        }
        await using var restart = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(restart); using var retryChannel = GrpcChannel.ForAddress(restart.Urls.Single());
        var result = await OperatorTestCredentials.Client(retryChannel).MaterializeApplicationConfigurationAsync(BatchRequest(scenario, true));
        Assert.Equal(BatchStatus.Succeeded, result.Status); Assert.Equal(ItemStatus.Existing, result.Items[0].Status);
        Assert.Equal(saved.Id.ToString(), result.Items[0].Artifact.RevisionId);
        await using (var verify = Context(scenario.Connection))
        {
            Assert.Equal(saved.Payload, (await verify.ConfigurationDocuments.AsNoTracking().SingleAsync(x => x.Id == saved.Id)).Payload);
            Assert.Equal(4, await verify.ConfigurationDocuments.AsNoTracking().CountAsync());
        }
        await restart.StopAsync();
    }

}
