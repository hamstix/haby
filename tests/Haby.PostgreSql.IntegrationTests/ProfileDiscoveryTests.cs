using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Haby.Tests.ProfilePersistenceSamples;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Fact]
    public async Task Profile_discovery_pages_maximum_numeric_revision_with_its_audit_without_payload_or_secret_reads_after_restart()
    {
        var connection = await NewDatabaseAsync();
        var args = AuthArguments(connection);
        var ids = new[] { "a", "a-1", "a.1", "a0", "aa" }.Concat(Enumerable.Range(0, 101).Select(x => $"profile-{x:000}")).ToArray();
        var expected = new Dictionary<string, ProfileMetadata>(StringComparer.Ordinal);
        var history = new Dictionary<int, ProfileMetadata>();
        var clock = new DiscoveryClock();
        var spy = new DiscoveryQuerySpy();
        var logs = new ProfileLog();
        for (var lifetime = 0; lifetime < 2; lifetime++)
        {
            spy.Strict = false;
            await using var host = OperatorHostApplication.Build(args, services =>
            {
                services.AddDbContextFactory<Haby.Persistence.PostgreSql.ManifestCatalogDbContext>(o => o.AddInterceptors(spy));
                services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
                services.AddSingleton<TimeProvider>(clock);
            });
            await host.Services.GetRequiredService<Haby.Persistence.PostgreSql.ManifestCatalogMigrationInitializer>().MigrateAsync();
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var client = OperatorTestCredentials.Client(channel);
            if (lifetime == 0)
            {
                Assert.Empty((await client.ListProfilesAsync(new())).Items);
                var lowerGrant = await host.Services.GetRequiredService<IOperatorAccessStore>().CreateAsync("lower revision importer", "test-importer", null, default);
                var lowerClient = new ManifestOperator.ManifestOperatorClient(channel);
                foreach (var id in ids)
                {
                    clock.Now = DiscoveryClock.Epoch.AddSeconds(990);
                    var source = Node(); source["metadata"]!["id"] = id; source["metadata"]!["revision"] = 10;
                    expected[id] = (await client.ImportProfileAsync(Payload(source))).Metadata;
                    if (id == "a") history[10] = expected[id];
                    if (id == "aa") continue; // A singleton identity alongside multi-revision Profiles.
                    clock.Now = DiscoveryClock.Epoch.AddSeconds(998);
                    source["metadata"]!["revision"] = 2;
                    var lower = await lowerClient.ImportProfileAsync(Payload(source), OperatorHeaders(lowerGrant.Credential));
                    Assert.NotEqual(expected[id].TechnicalActorId, lower.Metadata.TechnicalActorId);
                    Assert.True(DateTimeOffset.Parse(expected[id].ImportedAtUtc) < DateTimeOffset.Parse(lower.Metadata.ImportedAtUtc));
                    if (id == "a") history[2] = lower.Metadata;
                }
                // Many revisions in the first identity detect grouping/paging in the wrong order.
                for (var n = 11; n <= 115; n++)
                {
                    clock.Now = DiscoveryClock.Epoch.AddSeconds(1000 - n);
                    var source = Node(); source["metadata"]!["id"] = "a"; source["metadata"]!["revision"] = n;
                    expected["a"] = (await client.ImportProfileAsync(Payload(source))).Metadata;
                    history[n] = expected["a"];
                }
                // Independent arrange context. Payload deliberately unreadable: metadata must remain available.
                await using var arrange = ProfileContext(connection);
                await arrange.Database.ExecuteSqlRawAsync("UPDATE configuration_profile_revisions SET snapshot = 'sensitive-discovery-payload'");
            }
            spy.Strict = true;
            foreach (var size in new[] { 1, 50, 100 })
            {
                var seen = new List<string>(); string? after = null;
                do
                {
                    var request = new ListProfilesRequest { PageSize = size }; if (after is not null) request.AfterProfileId = after;
                    spy.Queries.Clear(); var page = await client.ListProfilesAsync(request);
                    Assert.Single(spy.Queries); AssertQuery(spy.Queries.Single(), grouped: true);
                    Assert.InRange(page.Items.Count, 0, size);
                    foreach (var item in page.Items) { Assert.Equal(expected[item.Reference.ProfileId], item); seen.Add(item.Reference.ProfileId); }
                    after = page.HasNextAfterProfileId ? page.NextAfterProfileId : null;
                    if (after is not null) Assert.Equal(page.Items[^1].Reference.ProfileId, after);
                } while (after is not null);
                Assert.Equal(ids.Order(StringComparer.Ordinal), seen);
                var revisions = new List<int>(); int? cursor = null;
                do
                {
                    var request = new ListProfileRevisionsRequest { ProfileId = "a", PageSize = size }; if (cursor is not null) request.AfterRevision = cursor.Value;
                    spy.Queries.Clear(); var page = await client.ListProfileRevisionsAsync(request);
                    Assert.Single(spy.Queries); AssertQuery(spy.Queries.Single(), grouped: false);
                    Assert.All(page.Items, x => Assert.Equal(history[x.Reference.Revision], x));
                    revisions.AddRange(page.Items.Select(x => x.Reference.Revision));
                    cursor = page.HasNextAfterRevision ? page.NextAfterRevision : null;
                } while (cursor is not null);
                Assert.Equal(new[] { 2, 10 }.Concat(Enumerable.Range(11, 105)), revisions);
            }
            Assert.Equal(50, (await client.ListProfilesAsync(new())).Items.Count);
            Assert.Equal(50, (await client.ListProfileRevisionsAsync(new() { ProfileId = "a" })).Items.Count);
            Assert.Empty((await client.ListProfileRevisionsAsync(new() { ProfileId = "unknown" })).Items);
            Assert.Equal(10, (await client.ListProfileRevisionsAsync(new() { ProfileId = "a0", AfterRevision = 3 })).Items.Single().Reference.Revision);
            Assert.Empty((await client.ListProfileRevisionsAsync(new() { ProfileId = "a", AfterRevision = int.MaxValue })).Items);
            Assert.Equal("a-1", (await client.ListProfilesAsync(new() { PageSize = 1, AfterProfileId = "a-0" })).Items.Single().Reference.ProfileId);
            Assert.Empty((await client.ListProfilesAsync(new() { AfterProfileId = "zz" })).Items);
            // Exact full final page may offer a cursor followed by an empty page.
            var last = await client.ListProfileRevisionsAsync(new() { ProfileId = "a0", PageSize = 2 });
            Assert.Equal(10, last.NextAfterRevision);
            Assert.Empty((await client.ListProfileRevisionsAsync(new() { ProfileId = "a0", PageSize = 2, AfterRevision = last.NextAfterRevision })).Items);
            foreach (var invalid in new[] { new ListProfilesRequest { PageSize = 0 }, new() { PageSize = 101 }, new() { AfterProfileId = "" }, new() { AfterProfileId = "BAD-sensitive" } })
                Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(async () => await client.ListProfilesAsync(invalid))).StatusCode);
            foreach (var invalid in new[] { new ListProfileRevisionsRequest(), new() { ProfileId = "a", PageSize = -1 }, new() { ProfileId = "a", PageSize = 101 }, new() { ProfileId = "a", AfterRevision = 0 }, new() { ProfileId = "a", AfterRevision = -1 } })
                Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(async () => await client.ListProfileRevisionsAsync(invalid))).StatusCode);
            var anonymous = new ManifestOperator.ManifestOperatorClient(channel);
            foreach (var headers in new Metadata?[] { null, OperatorHeaders(ConfigurationDeliveryCredential.Create(Guid.CreateVersion7())) })
            {
                await AuthDenied(async () => await anonymous.ListProfilesAsync(new(), headers));
                await AuthDenied(async () => await anonymous.ListProfileRevisionsAsync(new() { ProfileId = "a" }, headers));
            }
            await DiscoveryNative(host, expected["a"]);
            Assert.DoesNotContain("sensitive-discovery-payload", logs.Text);
            await host.StopAsync();
        }
        // Independent corruption context: fail the maximum; never fall back to an older valid audit.
        await using (var host = await ProfileHost(args, logs))
        {
            using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            foreach (var corruption in new[]
            {
                "UPDATE configuration_profile_revisions SET actor_id = E'bad\\nactor' WHERE profile_id = 'a' AND revision = 115",
                "UPDATE configuration_profile_revisions SET actor_id = '   ' WHERE profile_id = 'a' AND revision = 115",
                "UPDATE configuration_profile_revisions SET actor_id = 'valid', imported_at = 'infinity' WHERE profile_id = 'a' AND revision = 115",
                "UPDATE configuration_profile_revisions SET imported_at = '-infinity' WHERE profile_id = 'a' AND revision = 115"
            })
            {
                await using (var corrupt = ProfileContext(connection))
                    await corrupt.Database.ExecuteSqlRawAsync(corruption);
                foreach (var call in new Func<Task>[] { async () => { await client.ListProfilesAsync(new()); }, async () => { await client.ListProfileRevisionsAsync(new() { ProfileId = "a", AfterRevision = 114 }); } })
                {
                    var failure = await Assert.ThrowsAsync<RpcException>(call);
                    Assert.Equal(StatusCode.Internal, failure.StatusCode); Assert.Equal("Profile operation failed.", failure.Status.Detail);
                }
            }
            await host.StopAsync();
        }
        await ProfileState(connection, ids.Length * 2 - 1 + 105);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Profile_discovery_cancellation_reaches_database(bool revisions)
    {
        var connection = await NewDatabaseAsync(); var gate = new ProfileQueryGate();
        await using var host = await ProfileHost(AuthArguments(connection), new ProfileLog(), gate);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        gate.Arm(); using var cancel = new CancellationTokenSource();
        var pending = revisions ? ListRevisions() : ListProfiles();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); cancel.Cancel();
        Assert.Equal(StatusCode.Cancelled, (await Assert.ThrowsAsync<RpcException>(() => pending)).StatusCode);
        await gate.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await ProfileState(connection, 0); await host.StopAsync();
        async Task ListProfiles() => await client.ListProfilesAsync(new(), cancellationToken: cancel.Token);
        async Task ListRevisions() => await client.ListProfileRevisionsAsync(new() { ProfileId = "layout" }, cancellationToken: cancel.Token);
    }

    private static void AssertQuery(string sql, bool grouped)
    {
        Assert.DoesNotContain("snapshot", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", sql); Assert.Contains("ORDER BY", sql);
        if (grouped) { Assert.Contains("max(", sql, StringComparison.OrdinalIgnoreCase); Assert.Contains("GROUP BY", sql); Assert.Contains("COLLATE \"C\"", sql); }
    }

    private sealed class DiscoveryClock : TimeProvider
    {
        internal static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        internal DateTimeOffset Now { get; set; } = Epoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class DiscoveryQuerySpy : DbCommandInterceptor
    {
        internal bool Strict { get; set; }
        internal List<string> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("configuration_profile_revisions", StringComparison.Ordinal)) Queries.Add(command.CommandText);
            else if (Strict) Assert.Contains("operator_access_grants", command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private static async Task DiscoveryNative(WebApplication host, ProfileMetadata expected)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
        if (binary is null) return;
        var config = Path.GetTempFileName();
        try
        {
            await OperatorTestCredentials.WriteConfigAsync(config, host.Urls.Single());
            foreach (var command in new[] { new[] { "profile", "list" }, new[] { "profile", "revision", "list", "a" } })
                foreach (var format in new[] { "text", "json" })
                {
                    var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    var tail = command.Length == 2 ? new[] { "--page-size", "1" } : new[] { "--page-size", "1", "--after-revision", "114" };
                    foreach (var arg in command.Concat(tail).Concat(new[] { "--config", config, "-o", format })) start.ArgumentList.Add(arg);
                    using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                    Assert.Equal(0, process.ExitCode); Assert.Empty(await error);
                    var text = await output; Assert.DoesNotContain("sensitive", text);
                    if (format == "json")
                    {
                        using var json = JsonDocument.Parse(text); Assert.Equal("listed", json.RootElement.GetProperty("outcome"u8).GetString());
                        var item = json.RootElement.GetProperty("data"u8).GetProperty("items"u8)[0];
                        Assert.Equal(expected.Reference.Revision, item.GetProperty("reference"u8).GetProperty("revision"u8).GetInt32());
                        Assert.Equal(expected.TechnicalActorId, item.GetProperty("technicalActorId"u8).GetString());
                        Assert.Equal(expected.ImportedAtUtc, item.GetProperty("importedAtUtc"u8).GetString());
                    }
                    else { Assert.Contains(expected.TechnicalActorId, text); Assert.Contains(expected.ImportedAtUtc, text); }
                }
        }
        finally { File.Delete(config); }
    }
}
