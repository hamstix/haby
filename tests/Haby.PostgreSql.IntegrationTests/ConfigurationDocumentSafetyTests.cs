using System.Data.Common;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Status = Haby.Operator.Contracts.ConfigurationDocumentStatus;
using StoreStatus = Haby.Application.ConfigurationDocumentStatus;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    [Theory]
    [InlineData(63, true)]
    [InlineData(64, false)]
    public async Task Configuration_document_final_semantic_depth_64_and_65_is_checked_before_atomic_save(int segments, bool accepted)
    {
        var profile = JsonNode.Parse(RenderingProfile)!;
        profile["spec"]!["staticContributions"]!["logging"]!["path"] = "/" + string.Join('/', Enumerable.Repeat("p", segments));
        var scenario = await Prepare(full: true, render: true, renderingProfile: profile.ToJsonString());
        await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        var store = host.Services.GetRequiredService<IConfigurationDocumentStore>();
        var target = new ConfigurationDocumentTarget(scenario.Commit.DeploymentId, scenario.Commit.Generation, ManifestLocalId.Create("api"), ManifestLocalId.Create("settings"));
        var result = await store.MaterializeAsync(target, "boundary-actor", default);
        Assert.Equal(accepted ? StoreStatus.Created : StoreStatus.LimitExceeded, result.Status);
        var read = await store.ReadAsync(target, default);
        Assert.Equal(accepted ? StoreStatus.Found : StoreStatus.NotFound, read.Status);
        if (accepted) Assert.Equal(result.Revision, read.Revision);
        await using var verify = Context(scenario.Connection); Assert.Equal(accepted ? 1 : 0, await verify.ConfigurationDocuments.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Configuration_document_static_only_still_requires_applied_graph_and_needs_no_renderer_or_output_read()
    {
        var profile = JsonNode.Parse(RenderingProfile)!;
        profile["spec"]!["documents"]!["static"] = new JsonObject { ["fileName"] = "static.json", ["format"] = "json", ["template"] = new JsonObject { ["ordinary"] = true } };
        var scenario = await Prepare(full: true, render: true, renderingProfile: profile.ToJsonString());
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
            services.AddSingleton(ConfigurationRenderingCatalog.Empty).AddDbContextFactory<ManifestCatalogDbContext>(o => o.AddInterceptors(new RenderingReadTrap())));
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var request = DocumentRequest(scenario, exact: true); request.DocumentId = "static";
        Assert.Equal(Status.NotApplied, (await client.MaterializeConfigurationAsync(request)).Status);
        await AccessReady(scenario); await AccessExecute(scenario);
        Assert.Equal(Status.Created, (await client.MaterializeConfigurationAsync(request)).Status);
        var preview = await client.PreviewConfigurationAsync(request); Assert.Equal(Status.Found, preview.Status);
        Assert.Equal("{\"document\":{\"ordinary\":true},\"redactedPaths\":[]}", preview.PreviewJson.ToStringUtf8());
        await using var verify = Context(scenario.Connection); Assert.Single(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task Prior_document_catalog_refuses_naming_upgrade_and_preserves_frozen_plans_and_generations()
    {
        var scenario = await Prepare(full: true, render: true);
        await using (var arrange = Context(scenario.Connection))
            await arrange.Database.ExecuteSqlRawAsync("""
                DROP TABLE operator_access_grants;
                DROP TABLE configuration_access_grants;
                DROP TABLE configuration_document_revisions;
                DROP TABLE resource_management;
                ALTER TABLE deployment_change_sets DROP COLUMN expected_generation CASCADE;
                DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" LIKE '%ReuseManagedResources' OR "MigrationId" LIKE '%AddResourceNaming';
                DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" LIKE '%MaterializeConfigurationDocuments' OR "MigrationId" LIKE '%AddConfigurationAccess' OR "MigrationId" LIKE '%AddOperatorAccess';
                """);
        await using (var act = OwnerServices(scenario))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => act.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync());
            Assert.Contains("fresh catalog", error.Message);
        }
        await using var verify = Context(scenario.Connection);
        Assert.Equal(scenario.Plan.Inputs.Configuration, ConfigurationPlanningSnapshotCodec.Decode((await verify.DeploymentGenerations.AsNoTracking().SingleAsync()).ConfigurationJson, scenario.Plan.Inputs.Revision));
        Assert.DoesNotContain(await verify.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("AddResourceNaming", StringComparison.Ordinal));
        Assert.Equal(1, await verify.DurablePlans.AsNoTracking().CountAsync());
        await using var connection = new Npgsql.NpgsqlConnection(scenario.Connection); await connection.OpenAsync();
        Assert.Equal(true, await new Npgsql.NpgsqlCommand("SELECT to_regclass('public.configuration_document_revisions') IS NULL", connection).ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("liquid")]
    [InlineData("preview-budget")]
    public async Task Configuration_document_unsupported_or_oversized_static_input_leaves_no_artifact(string mode)
    {
        var profile = JsonNode.Parse(RenderingProfile)!;
        profile["spec"]!["documents"]!["settings"]!["template"] = new JsonObject { ["ordinary"] = mode == "liquid" ? "{{ parameters.token }}" : new string('<', 710000) };
        // Raw source stays below the unchanged import limit. Serializer escaping grows the preview past 4 MiB.
        var source = profile.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var scenario = await Prepare(full: true, render: true, renderingProfile: source);
        await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        Assert.Equal(mode == "liquid" ? Status.InputUnavailable : Status.LimitExceeded, (await client.MaterializeConfigurationAsync(DocumentRequest(scenario))).Status);
        await using var verify = Context(scenario.Connection); Assert.Empty(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync());
        await host.StopAsync();
    }

    [Theory]
    [InlineData("uncertain")]
    [InlineData("missing")]
    [InlineData("key")]
    [InlineData("contract")]
    [InlineData("producer")]
    [InlineData("payload")]
    [InlineData("attempt")]
    [InlineData("incomplete")]
    [InlineData("unavailable-renderer")]
    public async Task Configuration_document_invalid_evidence_or_gate_refuses_without_partial_record_or_other_mutation(string mode)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        if (mode != "unavailable-renderer")
        {
            var sql = mode switch
            {
                "uncertain" => "UPDATE durable_create_operation_attempts SET outcome = 'Uncertain' WHERE operation_id = 'app-login-access'",
                "missing" => "DELETE FROM operation_outputs WHERE operation_id = 'app-login-access'",
                "key" => "UPDATE operation_outputs SET output_key = 'wrong' WHERE operation_id = 'app-login-access'",
                "contract" => "UPDATE operation_outputs SET output_contract = 'wrong/v1' WHERE operation_id = 'app-login-access'",
                "producer" => "UPDATE operation_outputs SET operation_id = 'app-login-create' WHERE operation_id = 'app-login-access'",
                "attempt" => "UPDATE durable_create_operation_attempts SET attempt_number = attempt_number + 10 WHERE operation_id = 'app-login-access'",
                "incomplete" => "UPDATE durable_create_operations SET state = 'NeedsAttention' WHERE operation_id = 'db-restrict'",
                _ => "UPDATE operation_outputs SET payload = '{}' WHERE operation_id = 'app-login-access'"
            };
            await Sql(scenario.Connection, "SET session_replication_role = replica; " + sql);
        }
        var before = await ConfigurationState(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
        {
            if (mode == "unavailable-renderer") services.AddSingleton(ConfigurationRenderingCatalog.Empty);
            services.AddDbContextFactory<ManifestCatalogDbContext>(o => o.AddInterceptors(new RenderingReadTrap()));
        });
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var reply = await client.MaterializeConfigurationAsync(DocumentRequest(scenario));
        Assert.Equal(mode == "unavailable-renderer" ? Status.InputUnavailable : Status.NotApplied, reply.Status);
        Assert.Null(reply.Artifact); Assert.True(reply.PreviewJson.IsEmpty);
        Assert.Equal(before, await ConfigurationState(scenario));
        await using var verify = Context(scenario.Connection); Assert.Empty(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task Configuration_document_current_selector_is_resolved_once_and_never_falls_back_or_retargets()
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var pause = new SelectionPause();
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
            services.AddSingleton<IConfigurationDocumentStore>(p => pause.Wrap(new PostgreSqlConfigurationDocumentStore(p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
                p.GetRequiredService<ComposeConfigurationDocument>(), TimeProvider.System))));
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var request = DocumentRequest(scenario);
        var pending = client.MaterializeConfigurationAsync(request).ResponseAsync;
        await pause.Selected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The current pilot admits only generation 1. This controlled fixture permits a
        // competing future desired value without adding generation-2 admission to production.
        await Sql(scenario.Connection, "ALTER TABLE application_deployments DROP CONSTRAINT \"CK_application_deployments_desired_generation\"; UPDATE application_deployments SET desired_generation = 2");
        pause.Resume.TrySetResult();
        var original = await pending;
        Assert.Equal(Status.Created, original.Status); Assert.Equal(1, original.Target.Generation);
        // Current desired is now pending and has no saved layout: it cannot fall back to generation 1.
        Assert.Equal(Status.NotApplied, (await client.GetConfigurationAsync(request)).Status);
        Assert.Equal(Status.NotApplied, (await client.PreviewConfigurationAsync(request)).Status);
        Assert.Equal(Status.NotApplied, (await client.MaterializeConfigurationAsync(request)).Status);
        var explicitRequest = DocumentRequest(scenario, exact: true);
        Assert.Equal(original.Artifact, (await client.GetConfigurationAsync(explicitRequest)).Artifact);
        Assert.Equal(Status.Existing, (await client.MaterializeConfigurationAsync(explicitRequest)).Status);
        explicitRequest.Generation = 2;
        Assert.Equal(Status.NotFound, (await client.GetConfigurationAsync(explicitRequest)).Status);
        explicitRequest.Generation = 1; explicitRequest.ComponentId = "missing";
        Assert.Equal(Status.NotFound, (await client.GetConfigurationAsync(explicitRequest)).Status);
        explicitRequest.ComponentId = "api"; explicitRequest.ApplicationId = "absent";
        Assert.Equal(Status.NotFound, (await client.MaterializeConfigurationAsync(explicitRequest)).Status);
        await host.StopAsync();
    }

    private sealed class SelectionPause
    {
        public TaskCompletionSource Selected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IConfigurationDocumentStore Wrap(IConfigurationDocumentStore inner) => new Paused(this, inner);
        private sealed class Paused(SelectionPause pause, IConfigurationDocumentStore inner) : IConfigurationDocumentStore
        {
            public async ValueTask<ConfigurationDocumentResult> SelectAsync(ConfigurationDocumentSelection selection, CancellationToken token)
            {
                var result = await inner.SelectAsync(selection, token);
                pause.Selected.TrySetResult(); await pause.Resume.Task.WaitAsync(TimeSpan.FromSeconds(30), token); return result;
            }
            public ValueTask<ConfigurationDocumentResult> ReadAsync(ConfigurationDocumentTarget target, CancellationToken token) => inner.ReadAsync(target, token);
            public ValueTask<ConfigurationDocumentResult> MaterializeAsync(ConfigurationDocumentTarget target, string actor, CancellationToken token) => inner.MaterializeAsync(target, actor, token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configuration_document_cancellation_rolls_back_before_commit_and_retains_confirmed_commit(bool afterCommit)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        using var cancellation = new CancellationTokenSource(); var fault = new DocumentCommitCancellation(cancellation, afterCommit);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
            services.AddDbContextFactory<ManifestCatalogDbContext>(o => o.AddInterceptors(fault)));
        var store = host.Services.GetRequiredService<IConfigurationDocumentStore>();
        var target = new ConfigurationDocumentTarget(scenario.Commit.DeploymentId, scenario.Commit.Generation, ManifestLocalId.Create("api"), ManifestLocalId.Create("settings"));
        if (afterCommit)
        {
            var result = await store.MaterializeAsync(target, "original-actor", cancellation.Token);
            Assert.Equal(StoreStatus.Created, result.Status); Assert.True(cancellation.IsCancellationRequested);
            var repeat = await store.MaterializeAsync(target, "retry-actor", default);
            Assert.Equal(StoreStatus.Existing, repeat.Status); Assert.Equal(result.Revision, repeat.Revision);
        }
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MaterializeAsync(target, "original-actor", cancellation.Token).AsTask());
        await using var verify = Context(scenario.Connection);
        Assert.Equal(afterCommit ? 1 : 0, await verify.ConfigurationDocuments.AsNoTracking().CountAsync());
    }

    private sealed class DocumentCommitCancellation(CancellationTokenSource cancellation, bool afterCommit) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { if (afterCommit) cancellation.Cancel(); return Task.CompletedTask; }
    }

    [Theory]
    [InlineData("frozen-input")]
    [InlineData("corrupt-payload")]
    public async Task Configuration_document_existing_conflict_and_corrupt_storage_never_replace_or_repair(string mode)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var request = DocumentRequest(scenario, exact: true);
        Assert.Equal(Status.Created, (await client.MaterializeConfigurationAsync(request)).Status);
        string saved;
        await using (var read = Context(scenario.Connection)) saved = (await read.ConfigurationDocuments.AsNoTracking().SingleAsync()).Payload;
        await Sql(scenario.Connection, "SET session_replication_role = replica; " + (mode == "frozen-input" ? "UPDATE application_deployment_generations SET fingerprint = 'changed'" : "UPDATE configuration_document_revisions SET payload = '{}' "));
        var reply = await client.MaterializeConfigurationAsync(request);
        Assert.Equal(mode == "frozen-input" ? Status.IntegrityConflict : Status.InputCorrupt, reply.Status);
        await using var verify = Context(scenario.Connection);
        Assert.Equal(mode == "frozen-input" ? saved : "{}", (await verify.ConfigurationDocuments.AsNoTracking().SingleAsync()).Payload);
        await host.StopAsync();
    }
}
