using System.Diagnostics;
using System.Net;
using Google.Protobuf;
using Grpc.Core;
using Haby.Configuration.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.Cli.Tests;

public sealed partial class CliLoopbackTests
{
    [Fact]
    public async Task Runtime_startup_consumer_uses_generated_two_document_wire_and_never_starts_after_partial_refusal()
    {
        var native = Environment.GetEnvironmentVariable("HABY_AOT_RUNTIME_CONSUMER_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(native));
        if (native is not null) Assert.True(File.Exists(native));
        string? managedConsumer = null;
        if (native is null)
        {
            // ResolveReferences supplies the actual built project target, including custom configuration/artifacts paths.
            // The copied test directory omits framework-provided dependencies required by this independent process.
            managedConsumer = Assert.Single(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "runtime-startup-consumer.target")));
            Assert.True(Path.IsPathFullyQualified(managedConsumer) && File.Exists(managedConsumer));
            var consumerDirectory = Path.GetDirectoryName(managedConsumer)!;
            Assert.True(File.Exists(Path.Combine(consumerDirectory, "Haby.RuntimeStartup.Consumer.runtimeconfig.json")));
            Assert.True(File.Exists(Path.Combine(consumerDirectory, "Haby.RuntimeStartup.Consumer.deps.json")));
            Assert.True(File.Exists(Path.Combine(consumerDirectory, "Microsoft.Extensions.Logging.Abstractions.dll")));
        }
        var root = Path.Combine(Path.GetTempPath(), "haby221-consumer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "projected"); await File.WriteAllTextAsync(path, "fixture-projected-token");
            foreach (var refuseSecond in new[] { false, true })
            {
                var service = new StartupDelivery(refuseSecond);
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
                builder.Services.AddGrpc(); builder.Services.AddSingleton(service);
                await using var host = builder.Build(); host.MapGrpcService<StartupDelivery>(); await host.StartAsync();
                try
                {
                    var start = new ProcessStartInfo(native ?? "dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    if (native is null) start.ArgumentList.Add(managedConsumer!);
                    foreach (var argument in new[] { host.Urls.Single(), service.Binding, path, service.First, service.Second, "--allow-loopback-http" }) start.ArgumentList.Add(argument);
                    using var process = Process.Start(start)!;
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try { await process.WaitForExitAsync(deadline.Token); }
                    catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
                    var receivedStdout = await stdout;
                    var receivedStderr = await stderr;
                    var receivedCalls = service.Calls.Count;
                    var safeSummary = StartupProcessSummary(receivedStdout, receivedStderr, receivedCalls, process.ExitCode, refuseSecond);
                    Console.WriteLine(safeSummary);
                    Assert.True(process.ExitCode == (refuseSecond ? 1 : 0), safeSummary);
                    Assert.Equal(refuseSecond ? 1 : 0, process.ExitCode);
                    Assert.True(receivedStdout == (refuseSecond ? "" : "primary-started" + Environment.NewLine), safeSummary);
                    Assert.True(receivedStderr == (refuseSecond ? "runtime-startup-refused:access-denied" + Environment.NewLine : ""), safeSummary);
                    Assert.Equal(new[] { service.First, service.Second }, service.Calls);
                }
                finally { await host.StopAsync(); }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static string StartupProcessSummary(string stdout, string stderr, int calls, int exitCode, bool refusedScenario)
    {
        var primary = stdout == "primary-started" + Environment.NewLine;
        var unexpectedStdout = stdout.Length != 0 && !primary;
        var reason = stderr.Length > 128 ? "unexpected" : stderr switch
        {
            "" => "none",
            var value when value == "runtime-startup-refused:invalid-bootstrap" + Environment.NewLine => "invalid-bootstrap",
            var value when value == "runtime-startup-refused:credential-unavailable" + Environment.NewLine => "credential-unavailable",
            var value when value == "runtime-startup-refused:invalid-credential" + Environment.NewLine => "invalid-credential",
            var value when value == "runtime-startup-refused:invalid-response" + Environment.NewLine => "invalid-response",
            var value when value == "runtime-startup-refused:output-limit" + Environment.NewLine => "output-limit",
            var value when value == "runtime-startup-refused:identity-refused" + Environment.NewLine => "identity-refused",
            var value when value == "runtime-startup-refused:access-denied" + Environment.NewLine => "access-denied",
            var value when value == "runtime-startup-refused:configuration-unavailable" + Environment.NewLine => "configuration-unavailable",
            var value when value == "runtime-startup-refused:transport-failure" + Environment.NewLine => "transport-failure",
            var value when value == "runtime-startup-refused:caller-cancelled" + Environment.NewLine => "caller-cancelled",
            var value when value == "runtime-startup-refused:startup-deadline-exceeded" + Environment.NewLine => "startup-deadline-exceeded",
            var value when value == "runtime-startup-refused:unknown" + Environment.NewLine => "unknown",
            var value when value == "runtime-startup-refused:unknown;stage=args" + Environment.NewLine => "unknown-args",
            var value when value == "runtime-startup-refused:unknown;stage=endpoint-policy" + Environment.NewLine => "unknown-endpoint-policy",
            var value when value == "runtime-startup-refused:unknown;stage=channel" + Environment.NewLine => "unknown-channel",
            var value when value == "runtime-startup-refused:unknown;stage=client" + Environment.NewLine => "unknown-client",
            var value when value == "runtime-startup-refused:unknown;stage=fetch" + Environment.NewLine => "unknown-fetch",
            var value when value == "runtime-startup-refused:unknown;stage=primary" + Environment.NewLine => "unknown-primary",
            _ => "unexpected"
        };
        // Only fixed tokens, booleans and capped numeric counts enter assertions/logs. Never echo child output.
        return $"startup-process scenario={(refusedScenario ? "refused" : "success")}; primary={primary}; reason={reason}; " +
            $"stdout-unexpected={unexpectedStdout}; stderr-unexpected={reason == "unexpected"}; calls={(calls is >= 0 and <= 64 ? calls : -1)}; " +
            $"calls-overflow={calls is < 0 or > 64}; exit={(exitCode is >= 0 and <= 255 ? exitCode : -1)}; exit-overflow={exitCode is < 0 or > 255}";
    }

    private sealed class StartupDelivery(bool refuseSecond) : ConfigurationDelivery.ConfigurationDeliveryBase
    {
        internal readonly string Binding = Guid.NewGuid().ToString(), First = Guid.CreateVersion7().ToString(), Second = Guid.CreateVersion7().ToString();
        internal readonly List<string> Calls = [];
        public override Task<DeliverConfigurationReply> DeliverRuntime(DeliverRuntimeConfigurationRequest request, ServerCallContext context)
        {
            Assert.Equal(Binding, request.BindingId); Assert.Equal("Bearer fixture-projected-token", Assert.Single(context.RequestHeaders, x => x.Key == "authorization").Value);
            Assert.Equal(Calls.Count == 0 ? First : Second, request.RevisionId); Calls.Add(request.RevisionId);
            if (refuseSecond && request.RevisionId == Second) throw new RpcException(new(StatusCode.PermissionDenied, "private-body-transport-sentinel"));
            return Task.FromResult(new DeliverConfigurationReply
            {
                RevisionId = request.RevisionId,
                SourceFingerprint = "sha256:configuration-revision-v1:" + new string('a', 64),
                Json = ByteString.CopyFromUtf8("{\"state\":\"ready\"}")
            });
        }
    }
}
