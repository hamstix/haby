using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Haby.Tests.ProfilePersistenceSamples;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Fact]
    public async Task Profile_transport_persists_exact_revisions_and_audit_across_restart_without_delivery()
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        var source = Node();
        source["spec"]!["documents"]!["settings"]!["template"]!["protected"] = "sensitive-template-marker";
        ProfileReply original;
        var logs = new ProfileLog();
        var commands = new ProfileQueryGate();
        await using (var host = await ProfileHost(args, logs, commands))
        {
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            var validationCommands = commands.Count;
            foreach (var declaration in new[] { source, Node(false) })
            {
                var valid = await client.ValidateProfileAsync(Payload(declaration));
                Assert.Equal(ProfileStatus.Valid, valid.Status);
                Assert.Null(valid.Metadata);
                Assert.Equal("layout", valid.Reference.ProfileId);
                SafeProfileReply(valid);
            }
            foreach (var invalid in new[]
            {
                "null", "{}", "{bad", "{\"sensitive-template-marker\":true}",
                source.ToJsonString().Replace("ConfigurationProfile", "Application", StringComparison.Ordinal),
                source.ToJsonString().Replace("haby.dev/v1alpha1", "haby.dev/invalid", StringComparison.Ordinal),
                source.ToJsonString().Replace("\"spec\":{", "\"spec\":{\"unknown\":null,", StringComparison.Ordinal),
                """{"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"layout","revision":1},"spec":null}""",
                """{"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":null,"spec":{}}""",
                new string('[', 70) + "0" + new string(']', 70)
            })
            {
                var request = new ProfilePayloadRequest { Payload = ByteString.CopyFromUtf8(invalid) };
                foreach (var result in new[] { await client.ValidateProfileAsync(request), await client.ImportProfileAsync(request) })
                {
                    Assert.Equal(ProfileStatus.InvalidDeclaration, result.Status);
                    Assert.InRange(result.Diagnostics.Count, 1, 100);
                    Assert.True(result.ErrorCount >= result.Diagnostics.Count);
                    SafeProfileReply(result);
                }
            }
            var bounded = await client.ValidateProfileAsync(new()
            {
                Payload = ByteString.CopyFromUtf8(
                "{" + string.Join(",", Enumerable.Range(0, 150).Select(x => $"\"unknown{x}\":null")) + "}")
            });
            Assert.Equal(ProfileStatus.InvalidDeclaration, bounded.Status);
            Assert.InRange(bounded.Diagnostics.Count, 1, 100);
            Assert.Equal(bounded.ErrorCount > bounded.Diagnostics.Count, bounded.DiagnosticsTruncated);
            Assert.Equal(bounded.Diagnostics.Count, bounded.Diagnostics.Select(x => (x.Code, x.Path)).Distinct().Count());
            Assert.Equal(bounded.Diagnostics.OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal), bounded.Diagnostics);
            foreach (var import in new[] { false, true })
            {
                var oversized = new ProfilePayloadRequest { Payload = ByteString.CopyFrom(new byte[1024 * 1024 + 1]) };
                var failure = await Assert.ThrowsAsync<RpcException>(async () =>
                {
                    if (import) await client.ImportProfileAsync(oversized); else await client.ValidateProfileAsync(oversized);
                });
                Assert.Equal(StatusCode.ResourceExhausted, failure.StatusCode);
            }
            foreach (var reference in new[] { new ProfileRevisionRequest(), new() { ProfileId = "layout" },
                new() { ProfileId = "layout", Revision = -1 }, new() { ProfileId = "BAD", Revision = 1 } })
                Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(async () =>
                    await client.GetProfileRevisionAsync(reference))).StatusCode);
            Assert.Equal(validationCommands, commands.Count);
            // Independent context: validation/refusal caused no durable writes of any kind.
            await ProfileState(connection, 0);
            original = await client.ImportProfileAsync(Payload(source));
            Assert.Equal(ProfileStatus.Created, original.Status);
            Assert.True(commands.Count > validationCommands);
            Assert.Equal(OperatorHostApplication.TechnicalActorId, original.Metadata.TechnicalActorId);
            SafeProfileReply(original);
            await ProfileNative(host, source, "existingEquivalent");
            await host.StopAsync();
        }
        await using (var host = await ProfileHost(args, logs))
        {
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var client = new ManifestOperator.ManifestOperatorClient(channel);
            var found = await client.GetProfileRevisionAsync(original.Reference);
            Assert.Equal(ProfileStatus.Found, found.Status);
            Assert.Equal(original.Metadata, found.Metadata);
            var repeat = await client.ImportProfileAsync(Payload(Equivalent(source)));
            Assert.Equal(ProfileStatus.ExistingEquivalent, repeat.Status);
            Assert.Equal(original.Metadata, repeat.Metadata);
            var conflict = source.DeepClone().AsObject();
            conflict["spec"]!["documents"]!["settings"]!["template"]!["protected"] = "conflicting-marker";
            Assert.Equal(ProfileStatus.RevisionContentConflict, (await client.ImportProfileAsync(Payload(conflict))).Status);
            var next = source.DeepClone().AsObject(); next["metadata"]!["revision"] = 3;
            Assert.Equal(ProfileStatus.Created, (await client.ImportProfileAsync(Payload(next))).Status);
            var missing = await client.GetProfileRevisionAsync(new() { ProfileId = "layout", Revision = 2 });
            Assert.Equal(ProfileStatus.NotFound, missing.Status);
            Assert.Equal(2, missing.Reference.Revision);
            // The actual persisted secret-bearing declaration still refuses expansion atomically.
            var accepted = await host.Services.GetRequiredService<IConfigurationProfileRevisionQuery>()
                .FindAsync(new(ProfileId.Create("layout"), 1));
            var component = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Encoding.UTF8.GetBytes("""
                {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"empty"},"spec":{"components":{"api":{}}}}}
                """), new([], [], [], []))).Candidate.Declaration.Spec.Components.Single().Value;
            var expanded = new ConfigurationProfileExpander(new([accepted!.Declaration])).Expand(
                [new ProfileDecision.Use(accepted.Reference)], [], [new(ManifestLocalId.Create("api"), component, [])]);
            Assert.Null(expanded.Layouts);
            Assert.Contains(expanded.Diagnostics, x => x.Code == "profile.secret-projections.unsupported");
            await ProfileNative(host, source, "existingEquivalent");
            await ProfileState(connection, Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH") is null ? 2 : 3);
            // Privileged corruption uses its own context; neither get nor reimport may hide or repair it.
            await using (var arrange = ProfileContext(connection))
                await arrange.Database.ExecuteSqlRawAsync("UPDATE configuration_profile_revisions SET snapshot = 'sensitive-corrupt-marker' WHERE profile_id = 'layout' AND revision = 1");
            foreach (var import in new[] { false, true })
            {
                var error = await Assert.ThrowsAsync<RpcException>(async () =>
                {
                    if (import) await client.ImportProfileAsync(Payload(source));
                    else await client.GetProfileRevisionAsync(original.Reference);
                });
                Assert.Equal(StatusCode.Internal, error.StatusCode);
                Assert.Equal("Profile operation failed.", error.Status.Detail);
            }
            await using (var arrange = ProfileContext(connection))
                await arrange.Database.ExecuteSqlRawAsync("ALTER TABLE configuration_profile_revisions RENAME TO unavailable_profile_revisions");
            var unavailable = await Assert.ThrowsAsync<RpcException>(async () => await client.GetProfileRevisionAsync(original.Reference));
            Assert.Equal(StatusCode.Internal, unavailable.StatusCode);
            Assert.Equal("Profile operation failed.", unavailable.Status.Detail);
            await using (var arrange = ProfileContext(connection))
                await arrange.Database.ExecuteSqlRawAsync("ALTER TABLE unavailable_profile_revisions RENAME TO configuration_profile_revisions");
            await host.StopAsync();
        }
        Assert.DoesNotContain("unavailable_profile_revisions", logs.Text);
        Assert.DoesNotContain("Npgsql.PostgresException", logs.Text);
        Assert.DoesNotContain("sensitive-", logs.Text);
        Assert.DoesNotContain("future-token", logs.Text);
        await using var verify = ProfileContext(connection);
        Assert.Equal("sensitive-corrupt-marker", (await verify.ProfileRevisions.AsNoTracking().SingleAsync(x => x.ProfileId == "layout" && x.Revision == 1)).Snapshot);
    }

    [Fact]
    public async Task Profile_generated_transport_cancellation_reaches_real_store_without_writes()
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        var gate = new ProfileQueryGate();
        await using var host = await ProfileHost(args, new ProfileLog(), gate);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var client = new ManifestOperator.ManifestOperatorClient(channel);
        foreach (var import in new[] { false, true })
        {
            gate.Arm();
            using var cancel = new CancellationTokenSource();
            using var call = import ? client.ImportProfileAsync(Payload(Node()), cancellationToken: cancel.Token)
                : client.GetProfileRevisionAsync(new() { ProfileId = "layout", Revision = 1 }, cancellationToken: cancel.Token);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            cancel.Cancel();
            Assert.Equal(StatusCode.Cancelled, (await Assert.ThrowsAsync<RpcException>(async () => await call)).StatusCode);
            await gate.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        await ProfileState(connection, 0);
        await host.StopAsync();
    }

    private sealed class ProfileQueryGate : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private bool _armed;
        private int _count;
        internal int Count => _count;
        public override System.Data.Common.DbCommand CommandInitialized(Microsoft.EntityFrameworkCore.Diagnostics.CommandEndEventData eventData,
            System.Data.Common.DbCommand result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }
        internal TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Canceled { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _armed = true;
        }
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (_armed && command.CommandText.Contains("configuration_profile_revisions", StringComparison.Ordinal))
            {
                _armed = false;
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
            }
            return result;
        }
    }

    private static ProfilePayloadRequest Payload(JsonNode node) => new() { Payload = ByteString.CopyFrom(Bytes(node)) };
    private static void SafeProfileReply(ProfileReply reply)
    {
        foreach (var marker in new[] { "sensitive-", "future-token", "future-otel", "timeout", "ordinary", "Information", "template", "snapshot" })
            Assert.DoesNotContain(marker, reply.ToString(), StringComparison.OrdinalIgnoreCase);
    }
    private static ManifestCatalogDbContext ProfileContext(string connection) =>
        new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
    private static async Task ProfileState(string connection, int count)
    {
        await using var check = ProfileContext(connection);
        Assert.Equal(count, await check.ProfileRevisions.CountAsync());
        Assert.Empty(await check.Environments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.Secrets.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.ProviderInstances.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.Applications.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.DurablePlans.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.OperationOutputs.AsNoTracking().ToArrayAsync());
    }
    private static async Task<WebApplication> ProfileHost(string[] args, ProfileLog log, ProfileQueryGate? gate = null)
    {
        var host = OperatorHostApplication.Build(args, services =>
        {
            services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(log);
            if (gate is not null) services.AddDbContextFactory<ManifestCatalogDbContext>(options => options.AddInterceptors(gate));
        });
        try
        {
            await host.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            await host.StartAsync();
            return host;
        }
        catch { await host.DisposeAsync(); throw; }
    }
    private sealed class ProfileLog : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
        internal string Text => string.Join("\n", _lines);
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(_lines);
        public void Dispose() { }
        private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<string> lines) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lines.Enqueue(formatter(state, exception) + exception);
            }
        }
    }

    private static async Task ProfileNative(WebApplication host, JsonObject source, string importedOutcome)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
        if (binary is null) return;
        Assert.True(File.Exists(binary));
        var folder = Path.Combine(Path.GetTempPath(), "haby-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var config = Path.Combine(folder, "config.yaml");
        var file = Path.Combine(folder, "profile.json");
        var bom = Path.Combine(folder, "bom.json");
        var conflict = Path.Combine(folder, "conflict.json");
        var created = Path.Combine(folder, "created.json");
        try
        {
            await File.WriteAllTextAsync(config, $"""
                formatVersion: 1
                currentContext: bad
                instances:
                  bad:
                    endpoint: http://localhost:1
                  local:
                    endpoint: {host.Urls.Single()}
                contexts:
                  bad:
                    instance: bad
                    environmentId: absent
                  chosen:
                    instance: local
                    environmentId: never-created
                """);
            var native = source.DeepClone(); native["metadata"]!["id"] = "native-layout";
            await File.WriteAllBytesAsync(created, Bytes(native));
            await File.WriteAllBytesAsync(file, Bytes(Equivalent(source)));
            await File.WriteAllBytesAsync(bom, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(source)).ToArray());
            var changed = source.DeepClone(); changed["spec"]!["documents"]!["settings"]!["template"] = new JsonObject();
            await File.WriteAllBytesAsync(conflict, Bytes(changed));
            var nativeExists = await host.Services.GetRequiredService<IConfigurationProfileRevisionQuery>().FindAsync(new(ProfileId.Create("native-layout"), 1)) is not null;
            foreach (var format in new[] { "text", "json" })
                foreach (var (arguments, outcome, exit) in new (string[], string, int)[]
                {
                (["profile", "import", created], format == "text" && !nativeExists ? "created" : "existingEquivalent", 0),
                (["profile", "validate", file], "valid", 0),
                (["profile", "import", file], importedOutcome, 0),
                (["profile", "revision", "get", "layout", "1"], "found", 0),
                (["profile", "revision", "get", "layout", "2"], "notFound", 1),
                (["profile", "import", conflict], "revisionContentConflict", 1),
                (["profile", "validate", bom], "invalidDeclaration", 1)
                })
                {
                    var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var arg in arguments.Concat(new[] { "--config", config, "--context", "chosen", "-o", format })) start.ArgumentList.Add(arg);
                    using var process = Process.Start(start)!;
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                    Assert.Equal(exit, process.ExitCode);
                    Assert.Equal("", await stderr);
                    var text = await stdout;
                    Assert.Contains(outcome, text);
                    foreach (var marker in new[] { "sensitive-", "future-token", "future-otel", "Information", "timeout", "snapshot" }) Assert.DoesNotContain(marker, text);
                    if (format == "json")
                    {
                        using var json = JsonDocument.Parse(text);
                        Assert.Equal(outcome, json.RootElement.GetProperty("outcome"u8).GetString());
                        if (outcome is "created" or "found" or "existingEquivalent")
                        {
                            Assert.Equal(OperatorHostApplication.TechnicalActorId, json.RootElement.GetProperty("data"u8).GetProperty("technicalActorId"u8).GetString());
                            var data = json.RootElement.GetProperty("data"u8);
                            var reference = data.GetProperty("reference"u8);
                            var persisted = await host.Services.GetRequiredService<IConfigurationProfileRevisionQuery>().FindAsync(
                                new(ProfileId.Create(reference.GetProperty("profileId"u8).GetString()!), reference.GetProperty("revision"u8).GetInt32()));
                            Assert.Equal(persisted!.AuditMetadata.ImportedAt.ToString("O"), data.GetProperty("importedAtUtc"u8).GetString());
                        }
                    }
                }
        }
        finally { foreach (var path in new[] { config, file, bom, conflict, created }) File.Delete(path); Directory.Delete(folder); }
    }
}
