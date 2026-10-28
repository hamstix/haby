using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Haby.Operator.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.Cli.Tests;

public sealed partial class CliLoopbackTests
{
    private sealed class RecoveryRegistry : ManifestOperator.ManifestOperatorBase
    {
        internal static int Assignments;
        internal static StringWriter? ManagedError;
        public override Task<RecoveryReply> InspectOperationRecovery(OperationRequest request, ServerCallContext context)
        {
            if (request.ResourceId == "transport") throw new RpcException(new Status(StatusCode.Unavailable, "note-secret-sentinel"));
            var reply = CliRecoveryTests.Reply(RecoveryStatus.Found);
            reply.Preview.Operation.Key = request.Clone();
            reply.Receipt.Key = request.Clone();
            if (request.ResourceId == "stale") reply.Preview.UncertainAttemptId = "new-attempt";
            if (request.ResourceId == "retry")
            { reply.Preview.ClearUncertainAttemptId(); reply.Preview.Operation.State = OperationState.Succeeded; }
            return Task.FromResult(reply);
        }
        public override Task<RecoveryReply> AssignExistingOperation(AssignExistingOperationRequest request, ServerCallContext context)
        {
            Assert.True(request.Acknowledged);
            Assert.Equal("environment", request.EnvironmentRecordId); Assert.Equal("attempt-id", request.ExpectedAttemptId);
            Assert.Equal(RecoveryReason.OperatorAcceptedExistingDatabase, request.Reason);
            Assert.Equal("note-secret-sentinel", request.Note);
            // For the managed command prove the summary is visible before the assignment RPC is sent.
            if (ManagedError is { } error) Assert.Contains("example_db", error.ToString());
            Interlocked.Increment(ref Assignments);
            return Task.FromResult(CliRecoveryTests.Reply(request.Key.ResourceId switch
            {
                "absent" => RecoveryStatus.TargetAbsent,
                "unavailable" => RecoveryStatus.TargetUnavailable,
                "unsupported" => RecoveryStatus.UnsupportedRecoveryAction,
                "retry" => RecoveryStatus.AlreadyAssigned,
                _ => RecoveryStatus.Assigned
            }));
        }
    }

    [Fact]
    public async Task Recovery_native_and_managed_commands_require_consent_bounded_note_and_show_summary_before_assignment()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var host = builder.Build(); host.MapGrpcService<RecoveryRegistry>(); await host.StartAsync();
        var binary = AotBinary(); var endpoint = host.Urls.Single();
        var config = Path.GetTempFileName(); var note = Path.GetTempFileName();
        await File.WriteAllTextAsync(config, "formatVersion: 1\ninstances: {}\ncontexts: {}");
        await File.WriteAllTextAsync(note, "note-secret-sentinel", new UTF8Encoding(false));
        try
        {
            foreach (var format in new[] { "text", "json" })
                foreach (var resource in new[] { "db", "retry", "stale", "absent", "unavailable", "unsupported", "transport" })
                {
                    var args = AssignmentArgs(resource);
                    var expected = resource switch { "stale" or "absent" or "unavailable" or "unsupported" => 1, "transport" => 3, _ => 0 };
                    await Run(args, format, expected, resource is not ("stale" or "transport"), summary: resource != "transport");
                }
            await Run(["operation", "recovery", "inspect", "deployment", "1", "db", "--action", "create"], "json", 0, false, summary: false);
            var valid = AssignmentArgs("db");
            foreach (var option in new[] { "--acknowledge-assignment", "--note-file", "--expected-attempt-id", "--environment-record-id", "--reason" })
            {
                var args = valid.ToList(); var index = args.IndexOf(option);
                args.RemoveRange(index, option == "--acknowledge-assignment" ? 1 : 2);
                await Run([.. args], "json", 2, false, summary: false);
            }
            await Run([.. valid, "--acknowledge-assignment=false"], "json", 2, false, summary: false);
            foreach (var bytes in new[] { new byte[2049], new byte[] { 0xff, 0xfe, 0xff }, Encoding.UTF8.GetBytes(new string('x', 513)), Array.Empty<byte>() })
            {
                await File.WriteAllBytesAsync(note, bytes);
                await Run(valid, "json", 2, false, summary: false);
            }

            string[] AssignmentArgs(string resource) => ["operation", "recovery", "assign-existing", "deployment", "1", resource,
                "--action", "create", "--environment-record-id", "environment", "--expected-attempt-id", "attempt-id",
                "--acknowledge-assignment", "--reason", "operatorAcceptedExistingDatabase", "--note-file", note];

            async Task Run(string[] args, string format, int exit, bool assigns, bool summary)
            {
                args = [.. args, "--config", config, "-o", format];
                using var stdout = new StringWriter(); using var stderr = new StringWriter();
                RecoveryRegistry.ManagedError = stderr;
                var before = RecoveryRegistry.Assignments;
                var cli = new CliApplication(new FixedEndpoint(endpoint), new GrpcRegistryClientFactory(), stdout, stderr);
                Assert.Equal(exit, await cli.RunAsync(args));
                Assert.Equal(assigns ? 1 : 0, RecoveryRegistry.Assignments - before);
                Verify(stdout.ToString(), stderr.ToString());
                RecoveryRegistry.ManagedError = null;
                if (binary is null) return;
                var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                start.Environment["HABY_OPERATOR_URL"] = endpoint;
                before = RecoveryRegistry.Assignments;
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree: true); throw; }
                Assert.Equal(exit, process.ExitCode);
                Assert.Equal(assigns ? 1 : 0, RecoveryRegistry.Assignments - before);
                Verify(await output, await errors);
                void Verify(string text, string error)
                {
                    Assert.DoesNotContain("note-secret-sentinel", text + error);
                    if (summary) { Assert.Contains("example_db", error); Assert.Contains("Pinned Provider", error); }
                    if (exit >= 2) { Assert.Equal("", text); return; }
                    if (format == "json") { using var document = JsonDocument.Parse(text); Assert.Equal(1, document.RootElement.GetProperty("formatVersion"u8).GetInt32()); }
                }
            }
        }
        finally { RecoveryRegistry.ManagedError = null; File.Delete(note); File.Delete(config); await host.StopAsync(); }
    }
}
