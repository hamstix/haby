using System.Diagnostics;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Fact]
    public async Task Environment_races_retry_and_restart_preserve_identity_without_other_catalog_state()
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        string recordId;
        await using (var host = await OperatorHostApplication.BuildMigratedAsync(args))
        {
            await host.StartAsync();
            using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(host.Urls.Single());
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            Assert.Equal(EnvironmentStatus.NotFound, (await client.GetEnvironmentAsync(new() { Name = "pilot" })).Status);
            Assert.Empty((await client.ListEnvironmentsAsync(new())).Items);
            foreach (var name in new[] { "INVALID", "", new string('a', 65), "bad--name" })
            {
                Assert.Equal(EnvironmentStatus.InvalidName, (await client.CreateEnvironmentAsync(new() { Name = name })).Status);
                Assert.Equal(EnvironmentStatus.InvalidName, (await client.GetEnvironmentAsync(new() { Name = name })).Status);
            }
            // Hold every contender at the same gate, using independent factory-created DbContexts.
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = Enumerable.Range(0, 16).Select(async _ =>
            {
                await gate.Task;
                return await client.CreateEnvironmentAsync(new() { Name = "pilot" });
            }).ToArray();
            gate.SetResult();
            var replies = await Task.WhenAll(requests);
            Assert.Single(replies, x => x.Status == EnvironmentStatus.Created);
            Assert.Equal(15, replies.Count(x => x.Status == EnvironmentStatus.Existing));
            recordId = replies[0].Environment.RecordId;
            Assert.Equal(7, Guid.Parse(recordId).Version);
            Assert.All(replies, x => Assert.Equal(recordId, x.Environment.RecordId));
            var retry = await client.CreateEnvironmentAsync(new() { Name = "pilot" });
            Assert.Equal(EnvironmentStatus.Existing, retry.Status);
            Assert.Equal(recordId, retry.Environment.RecordId);
            // The old SecretStore entry point must share the same creation algorithm and identity.
            var legacy = await host.Services.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            Assert.Equal(recordId, legacy.ToString());
            await host.StopAsync();
        }

        await using (var restarted = await OperatorHostApplication.BuildMigratedAsync(args))
        {
            await restarted.StartAsync();
            using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(restarted.Urls.Single());
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            var found = await client.GetEnvironmentAsync(new() { Name = "pilot" });
            Assert.Equal(EnvironmentStatus.Found, found.Status);
            Assert.Equal(recordId, found.Environment.RecordId);
            var repeated = await client.CreateEnvironmentAsync(new() { Name = "pilot" });
            Assert.Equal(EnvironmentStatus.Existing, repeated.Status);
            Assert.Equal(found.Environment, repeated.Environment);
            Assert.Equal(found.Environment, Assert.Single((await client.ListEnvironmentsAsync(new())).Items));
            await EnvironmentNativeSmokeAsync(restarted, recordId);
            await restarted.StopAsync();
        }

        // Verification uses a fresh DbContext after both Hosts (and all act contexts) are disposed.
        await using var verify = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
        var persisted = await verify.Database.SqlQueryRaw<EnvironmentCheck>(
            "SELECT id AS \"Id\", environment_id AS \"Name\" FROM environments").ToListAsync();
        Assert.Equal(recordId, Assert.Single(persisted).Id.ToString());
        Assert.Equal("pilot", persisted[0].Name);
        // Identifiers below are fixed test-owned table names, never external input.
#pragma warning disable EF1002
        foreach (var table in new[] { "secrets", "secret_versions", "secret_payloads", "provider_instances",
            "provider_configuration_revisions", "provider_aliases", "provider_alias_audit", "provider_revision_secret_refs",
            "applications", "manifest_revisions", "deployment_change_sets", "deployment_plans", "deployment_plan_closures",
            "application_deployments", "application_deployment_generations", "change_set_commits", "durable_create_operations",
            "durable_create_operation_attempts", "postgresql_database_recovery_decisions" })
            Assert.Equal(0L, await verify.Database.SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM {table}").SingleAsync());
#pragma warning restore EF1002
    }

    private sealed class EnvironmentCheck
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    [Fact]
    public async Task Environment_pages_use_ordinal_order_and_validate_boundaries_and_cursors()
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        await using var host = await OperatorHostApplication.BuildMigratedAsync(args);
        await host.StartAsync();
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(host.Urls.Single());
        var client = new ManifestOperator.ManifestOperatorClient(channel);
        var names = new[] { "z", "b", "a0", "a-1", "a", new string('c', 64) }.Concat(Enumerable.Range(0, 100).Select(i => $"page-{i:d3}")).ToArray();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var result = await client.CreateEnvironmentAsync(new() { Name = name });
            Assert.Equal(EnvironmentStatus.Created, result.Status);
            Assert.True(identities.Add(result.Environment.RecordId));
        }
        Assert.Equal(50, (await client.ListEnvironmentsAsync(new())).Items.Count);
        foreach (var size in new[] { 1, 2, 50, 100 })
        {
            var seen = new List<string>();
            string cursor = "";
            do
            {
                var page = await client.ListEnvironmentsAsync(new() { PageSize = size, Cursor = cursor });
                Assert.Equal(EnvironmentStatus.Listed, page.Status);
                Assert.InRange(page.Items.Count, 1, size);
                seen.AddRange(page.Items.Select(x => x.Name));
                Assert.Equal(seen.Count < names.Length, page.NextCursor.Length > 0);
                cursor = page.NextCursor;
            } while (cursor.Length > 0);
            Assert.Equal(names.Order(StringComparer.Ordinal), seen);
        }
        var empty = await client.ListEnvironmentsAsync(new() { Cursor = "env1:z", PageSize = 100 });
        Assert.Equal(EnvironmentStatus.Listed, empty.Status);
        Assert.Empty(empty.Items);
        Assert.Empty(empty.NextCursor);
        Assert.Equal("b", Assert.Single((await client.ListEnvironmentsAsync(new() { Cursor = "env1:az", PageSize = 1 })).Items).Name);
        foreach (var cursor in new[] { "env2:a", "env1:", "env1:A", "env1:a\n", "env1:" + new string('a', 65) })
        {
            var result = await client.ListEnvironmentsAsync(new() { Cursor = cursor });
            Assert.Equal(EnvironmentStatus.InvalidCursor, result.Status);
            Assert.Empty(result.Items);
            Assert.Empty(result.NextCursor);
        }
        foreach (var size in new[] { -1, 101, int.MaxValue })
            Assert.Equal(EnvironmentStatus.InvalidPageSize, (await client.ListEnvironmentsAsync(new() { PageSize = size })).Status);
        await host.StopAsync();
    }

    private static async Task EnvironmentNativeSmokeAsync(WebApplication host, string recordId)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
        if (binary is null) return;
        Assert.True(File.Exists(binary));
        var config = Path.Combine(Path.GetTempPath(), "haby-env-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            await File.WriteAllTextAsync(config, $"""
                formatVersion: 1
                currentContext: absent
                instances:
                  local:
                    endpoint: {host.Urls.Single()}
                contexts:
                  absent:
                    instance: local
                    environmentId: never-created
                """);
            foreach (var format in new[] { "text", "json" })
                foreach (var (args, outcome, exit) in new (string[], string, int)[]
                {
                    (["environment", "get", "never-created"], "notFound", 1),
                    (["config", "current-context"], "absent", 0),
                    (["environment", "get", "never-created"], "notFound", 1),
                    (["environment", "create", "pilot"], "existing", 0),
                    (["environment", "get", "pilot"], "found", 0),
                    (["environment", "list", "--page-size", "1"], "listed", 0),
                    (["environment", "get", "BAD"], "invalidName", 1),
                    (["environment", "list", "--cursor", "bad"], "invalidCursor", 1),
                    (["environment", "list", "--page-size", "101"], "invalidPageSize", 1)
                })
                {
                    var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var arg in args.Concat(new[] { "--config", config, "-o", format })) start.ArgumentList.Add(arg);
                    using var process = Process.Start(start)!;
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch { process.Kill(entireProcessTree: true); throw; }
                    Assert.Equal(exit, process.ExitCode);
                    Assert.Equal("", await stderr);
                    var text = await stdout;
                    Assert.Contains(outcome, text);
                    if (outcome is "existing" or "found" or "listed") Assert.Contains(recordId, text);
                    if (format == "json")
                    {
                        using var json = JsonDocument.Parse(text);
                        Assert.Equal(1, json.RootElement.GetProperty("formatVersion"u8).GetInt32());
                        if (args[0] == "environment") Assert.Equal(outcome, json.RootElement.GetProperty("outcome"u8).GetString());
                    }
                }
        }
        finally { File.Delete(config); }
    }
}
