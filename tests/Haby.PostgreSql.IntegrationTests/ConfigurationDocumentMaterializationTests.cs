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
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Status = Haby.Operator.Contracts.ConfigurationDocumentStatus;
using StoreStatus = Haby.Application.ConfigurationDocumentStatus;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    private static ConfigurationDocumentRequest DocumentRequest(Scenario scenario, string component = "api", bool exact = false)
    {
        var request = new ConfigurationDocumentRequest
        {
            Environment = "pilot",
            ApplicationId = scenario.Plan.Inputs.Configuration!.Manifest.ApplicationId.Value,
            ComponentId = component,
            DocumentId = "settings"
        };
        if (exact) request.Generation = scenario.Commit.Generation.Value;
        return request;
    }

    [Fact]
    public async Task Configuration_document_actual_Host_five_steps_two_documents_concurrent_repeat_native_restart_and_dump_restore_preserve_complete_artifacts()
    {
        var scenario = await Prepare(full: true, render: true);
        ConfigurationDocumentReply api;
        ConfigurationDocumentReply worker;
        ConfigurationDocumentRow[] rows;
        var barrier = new OutputBarrier(8);
        await using (var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
            services.AddSingleton<ComposeConfigurationDocument>(p => new(PostgreSqlConnectionRendering.CreateCatalog(), PostgreSqlConnectionRendering.FormatterCatalog(),
                barrier.Wrap(p.GetRequiredService<IConfigurationOutputEvidence>())))))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var client = OperatorTestCredentials.Client(channel);
            var request = DocumentRequest(scenario);
            Assert.Equal(Status.NotApplied, (await client.MaterializeConfigurationAsync(request)).Status);
            Assert.Equal(Status.NotFound, (await client.PreviewConfigurationAsync(DocumentRequest(scenario, exact: true))).Status);
            var executed = await client.ExecuteGenerationAsync(new() { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = 1 });
            Assert.Equal(GenerationExecutionStatus.Succeeded, executed.Status); Assert.Equal(5, executed.Progress.Completed);
            Assert.Equal(Status.NotFound, (await client.GetConfigurationAsync(request)).Status);
            var before = await ConfigurationState(scenario);
            var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.MaterializeConfigurationAsync(request).ResponseAsync));
            Assert.Single(replies, x => x.Status == Status.Created); Assert.Equal(7, replies.Count(x => x.Status == Status.Existing));
            api = replies[0]; Assert.All(replies, x => Assert.Equal(api.Artifact, x.Artifact));
            Assert.Equal(8, barrier.Count);
            await NativeDocument(host.Urls.Single(), DocumentRequest(scenario, "worker"), "materialize", "created", 0);
            worker = await client.MaterializeConfigurationAsync(DocumentRequest(scenario, "worker"));
            Assert.Contains(worker.Status, new[] { Status.Created, Status.Existing });
            Assert.NotEqual(api.Artifact.RevisionId, worker.Artifact.RevisionId);
            var preview = await client.PreviewConfigurationAsync(request); CheckPreview(preview, api);
            Assert.Equal(Status.Existing, (await client.MaterializeConfigurationAsync(DocumentRequest(scenario, exact: true))).Status);
            Assert.Equal(before, await ConfigurationState(scenario));
            await NativeDocument(host.Urls.Single(), request, "materialize", "existing", 0);
            await NativeDocument(host.Urls.Single(), request, "get", "found", 0);
            await NativeDocument(host.Urls.Single(), request, "preview", "found", 0);
            await using (var verify = Context(scenario.Connection))
                rows = await verify.ConfigurationDocuments.AsNoTracking().OrderBy(x => x.Component).ToArrayAsync();
            Assert.Equal(2, rows.Length);
            var decoded = rows.Select(x => ConfigurationDocumentCodec.Decode(x.Payload, x.Fingerprint)).ToArray();
            var values = decoded.Select(x => Assert.IsType<ConfigurationDeferredValue>(Assert.IsType<ConfigurationObject>(Assert.IsType<ConfigurationObject>(
                Assert.IsType<ConfigurationObject>(x.Tree).Members["PostgreSQL"]).Members["DefaultConnection"]).Members["ConnectionString"])).ToArray();
            Assert.Equal(values[0].Arguments.Members["password"], values[1].Arguments.Members["password"]);
            Assert.Equal(decoded[0].OutputAssociations, decoded[1].OutputAssociations);
            Assert.Equal(ConfigurationNumber.Parse("200"), Assert.IsType<ConfigurationObject>(values[0].Arguments.Members["settings"]).Members["timeout"]);
            Assert.Equal(ConfigurationNumber.Parse("15"), Assert.IsType<ConfigurationObject>(values[1].Arguments.Members["settings"]).Members["timeout"]);
            Assert.All(decoded, x => { Assert.Equal(scenario.Plan.Inputs.Configuration, ConfigurationPlanningSnapshotCodec.Decode(x.FrozenInputs, scenario.Plan.Inputs.Revision)); Assert.Contains("app-login-access", x.OutputAssociations); });
            await host.StopAsync();
        }
        await VerifySaved(scenario.Connection);
        var restored = _catalogPools.Register(new NpgsqlConnectionStringBuilder(await Database("configuration_restore_")));
        var directory = "/tmp/haby-configuration-" + Guid.NewGuid().ToString("N"); var dump = directory + "/catalog.dump";
        Exception? failure = null;
        try
        {
            await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + new NpgsqlConnectionStringBuilder(scenario.Connection).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
            await VerifySaved(restored);
        }
        catch (Exception e) { failure = e; throw; }
        finally
        {
            try { await fixture.RunClientUtilityAsync("rm", "-f", dump); await fixture.RunClientUtilityAsync("rmdir", directory); }
            catch when (failure is not null) { }
        }
        async Task VerifySaved(string connection)
        {
            await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
                services.AddSingleton(ConfigurationRenderingCatalog.Empty).AddSingleton(new ConfigurationFormatterCatalog([], []))
                    .AddDbContextFactory<ManifestCatalogDbContext>(o => o.AddInterceptors(new RenderingReadTrap())));
            await host.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var client = OperatorTestCredentials.Client(channel);
            foreach (var original in new[] { api, worker })
            {
                var request = original.Selector.Clone(); request.Generation = 1;
                Assert.Equal(original.Artifact, (await client.GetConfigurationAsync(request)).Artifact);
                var existing = await client.MaterializeConfigurationAsync(request);
                Assert.Equal(Status.Existing, existing.Status); Assert.Equal(original.Artifact, existing.Artifact);
                CheckPreview(await client.PreviewConfigurationAsync(request), original);
                await NativeDocument(host.Urls.Single(), request, "preview", "found", 0);
            }
            await using var verify = Context(connection);
            var actual = await verify.ConfigurationDocuments.AsNoTracking().OrderBy(x => x.Component).ToArrayAsync();
            Assert.Equal(rows.Select(x => (x.Id, x.Payload, x.Fingerprint)), actual.Select(x => (x.Id, x.Payload, x.Fingerprint)));
            await host.StopAsync();
        }
    }

    private static void CheckPreview(ConfigurationDocumentReply reply, ConfigurationDocumentReply original)
    {
        Assert.Equal(Status.Found, reply.Status); Assert.Equal(original.Artifact, reply.Artifact);
        using var doc = JsonDocument.Parse(reply.PreviewJson.Memory);
        var root = doc.RootElement;
        Assert.Equal(new[] { "/PostgreSQL/DefaultConnection/ConnectionString" }, root.GetProperty("redactedPaths"u8).EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("***REDACTED***", root.GetProperty("document"u8).GetProperty("PostgreSQL"u8).GetProperty("DefaultConnection"u8).GetProperty("ConnectionString"u8).GetString());
        Assert.Equal("Information", root.GetProperty("document"u8).GetProperty("Logging"u8).GetProperty("Level"u8).GetString());
        foreach (var hidden in new[] { "secretId", "versionId", "password", "formatter", "timeout", "sslMode" }) Assert.DoesNotContain(hidden, reply.PreviewJson.ToStringUtf8());
    }

    private static async Task<string> ConfigurationState(Scenario scenario)
    {
        await using var db = Context(scenario.Connection);
        // Protected in-memory comparison only; no raw state enters measurement artifacts.
        var rows = await db.Database.SqlQueryRaw<string>("""
            SELECT (jsonb_build_array(
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM application_deployments t),
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY operation_id) FROM durable_create_operations t),
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY attempt_id) FROM durable_create_operation_attempts t),
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY operation_id, output_contract, output_key) FROM operation_outputs t),
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY secret_version_id) FROM secret_payloads t)))::text AS "Value"
            """).SingleAsync();
        return rows;
    }

    private sealed class OutputBarrier(int participants)
    {
        private readonly int _participants = participants;
        private int _count;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Count => Math.Min(Volatile.Read(ref _count), _participants);
        internal IConfigurationOutputEvidence Wrap(IConfigurationOutputEvidence inner) => new BarrierEvidence(this, inner);
        private sealed class BarrierEvidence(OutputBarrier owner, IConfigurationOutputEvidence inner) : IConfigurationOutputEvidence
        {
            public async ValueTask<ExternalOperationOutput?> ReadAsync(DurableOperationKey producer, ManagedResourceKey resource, DurableActionExecutionKey action, string contract, string key, CancellationToken token)
            {
                var output = await inner.ReadAsync(producer, resource, action, contract, key, token);
                if (Interlocked.Increment(ref owner._count) <= owner._participants)
                {
                    if (owner._count == owner._participants) owner._ready.TrySetResult();
                    await owner._ready.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                }
                return output;
            }
        }
    }

    private static async Task NativeDocument(string endpoint, ConfigurationDocumentRequest request, string verb, string outcome, int exit)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrEmpty(binary));
        if (binary is null) return;
        var config = Path.Combine(Path.GetTempPath(), "haby-document-" + Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllTextAsync(config, $"formatVersion: 1\ncurrentContext: wrong\ninstances:\n  local:\n    endpoint: {endpoint}\n    token: {OperatorTestCredentials.Credential(endpoint)}\ncontexts:\n  wrong:\n    instance: local\n    environmentId: absent\n  right:\n    instance: local\n    environmentId: pilot\n");
        try
        {
            foreach (var format in new[] { "text", "json" })
            {
                var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "configuration", verb, request.ApplicationId, "--config", config, "--component", request.ComponentId, "--document", request.DocumentId,
                    "--generation", "1", "--output", format }) start.ArgumentList.Add(arg);
                foreach (var arg in format == "text" ? new[] { "--environment", request.Environment } : new[] { "--context", "right" }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                var expectedOutcome = verb == "materialize" && outcome == "created" && format == "json" ? "existing" : outcome;
                Assert.Equal(exit, process.ExitCode); Assert.Equal("", await stderr); var text = await stdout; Assert.Contains(expectedOutcome, text);
                if (format == "json")
                {
                    using var doc = JsonDocument.Parse(text); Assert.Equal(expectedOutcome, doc.RootElement.GetProperty("outcome"u8).GetString());
                    Assert.Equal(1, doc.RootElement.GetProperty("data"u8).GetProperty("target"u8).GetProperty("generation"u8).GetInt64());
                    if (verb == "preview") Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("data"u8).GetProperty("preview"u8).ValueKind);
                }
                foreach (var hidden in new[] { "secretId", "password", "formatter", "sslMode" }) Assert.DoesNotContain(hidden, text);
            }
        }
        finally { File.Delete(config); }
    }
}
