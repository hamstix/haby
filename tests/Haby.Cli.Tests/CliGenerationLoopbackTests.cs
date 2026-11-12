using System.Diagnostics;
using System.Net;
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
    private static ResourceStageCheckpointSummary ResourceCheckpoint() => new()
    {
        CheckpointId = "019d0000-0000-7000-8000-000000000001",
        FormatVersion = 1,
        InputFingerprint = "v4:" + new string('A', 64),
        ConfirmedAtUtc = "2026-11-12T00:00:00.0000000+00:00",
        ActiveResources = 1,
        OriginalResults = 1
    };
    private sealed class GenerationRegistry : ManifestOperator.ManifestOperatorBase
    {
        internal static int Executions;
        public override async Task<GenerationExecutionReply> ExecuteGeneration(GenerationRequest request, ServerCallContext context)
        {
            Interlocked.Increment(ref Executions);
            Assert.Equal(1, request.Generation);
            if (request.DeploymentId == "cancel") await Task.Delay(Timeout.Infinite, context.CancellationToken);
            if (request.DeploymentId == "transport")
                throw new RpcException(new Status(StatusCode.Unavailable, "secret-transport-sentinel"));
            var status = Enum.Parse<GenerationExecutionStatus>(request.DeploymentId);
            var reply = new GenerationExecutionReply { Status = status };
            if (status == GenerationExecutionStatus.Succeeded)
            {
                reply.Progress = new()
                {
                    DeploymentId = "deployment",
                    Generation = 1,
                    DesiredGeneration = 1,
                    AppliedGeneration = 1,
                    ResourcesReady = ResourceCheckpoint(),
                    Completed = 1,
                    Total = 1
                };
                reply.Progress.Nodes.Add(new GenerationNodeProgress
                {
                    Operation = CliOperationTests.Snapshot(OperationStatus.Succeeded, OperationState.Succeeded).Operation,
                    LastAttemptOutcome = OperationAttemptOutcome.Succeeded
                });
            }
            else if (status is GenerationExecutionStatus.RetryableFailure or GenerationExecutionStatus.Canceled)
            {
                reply.Progress = new() { DeploymentId = "deployment", Generation = 1, DesiredGeneration = 1, Total = 1 };
                reply.Progress.Nodes.Add(new GenerationNodeProgress
                {
                    Operation = CliOperationTests.Snapshot(OperationStatus.RetryableFailure, OperationState.Preparing).Operation,
                    LastAttemptOutcome = OperationAttemptOutcome.FailedBeforeDispatch
                });
                reply.StoppedOperationId = "db";
                reply.StopOutcome = OperationStatus.RetryableFailure;
            }
            return reply;
        }
        public override Task<OperationReply> ExecuteOperation(OperationRequest request, ServerCallContext context) =>
            throw new InvalidOperationException("Generation CLI must not execute individual operations.");
    }

    [Fact]
    public async Task Generation_command_makes_one_RPC_and_native_formats_all_outcomes_without_retries()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var host = builder.Build(); host.MapGrpcService<GenerationRegistry>(); await host.StartAsync();
        var binary = AotBinary();
        var endpoint = host.Urls.Single();
        var config = Path.GetTempFileName();
        await File.WriteAllTextAsync(config, "formatVersion: 1\ninstances: {}\ncontexts: {}");
        try
        {
            foreach (var status in Enum.GetValues<GenerationExecutionStatus>().Where(s => s != GenerationExecutionStatus.Unspecified))
                foreach (var format in new[] { "text", "json" })
                {
                    var exit = status == GenerationExecutionStatus.Succeeded ? 0 : status == GenerationExecutionStatus.HandlerUnavailable ? 2 : status == GenerationExecutionStatus.Canceled ? 3 : 1;
                    var args = new[] { "deployment", "generation", "execute", status.ToString(), "1", "--config", config, "-o", format };
                    using var output = new StringWriter(); using var error = new StringWriter();
                    var cli = new CliApplication(new FixedEndpoint(endpoint), new GrpcRegistryClientFactory(), output, error);
                    var callsBefore = GenerationRegistry.Executions;
                    Assert.Equal(exit, await cli.RunAsync(args));
                    Assert.Equal(1, GenerationRegistry.Executions - callsBefore);
                    Verify(output.ToString());
                    Assert.Equal("", error.ToString());
                    if (binary is not null)
                    {
                        var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                        foreach (var argument in args) start.ArgumentList.Add(argument);
                        start.Environment["HABY_OPERATOR_URL"] = endpoint;
                        using var process = Process.Start(start)!;
                        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree: true); throw; }
                        Assert.Equal(exit, process.ExitCode); Verify(await stdout);
                        Assert.Equal("", await stderr);
                    }
                    void Verify(string text)
                    {
                        Assert.Contains(GenerationPresentation.Outcome(status), text);
                        if (format != "json") return;
                        Assert.EndsWith(Environment.NewLine, text);
                        using var json = JsonDocument.Parse(text);
                        Assert.Equal("deployment generation execute", json.RootElement.GetProperty("command").GetString());
                        Assert.Equal(GenerationPresentation.Outcome(status), json.RootElement.GetProperty("outcome").GetString());
                        if (status is GenerationExecutionStatus.RetryableFailure or GenerationExecutionStatus.Canceled)
                        {
                            var data = json.RootElement.GetProperty("data");
                            Assert.Equal("retryableFailure", data.GetProperty("stopOutcome").GetString());
                            Assert.False(data.GetProperty("progress").TryGetProperty("appliedGeneration", out _));
                        }
                    }
                }
            foreach (var id in new[] { "transport", "cancel" })
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                var cli = new CliApplication(new FixedEndpoint(endpoint), new GrpcRegistryClientFactory(), output, error);
                var before = GenerationRegistry.Executions;
                Assert.Equal(3, await cli.RunAsync(["deployment", "generation", "execute", id, "1", "--config", config, "-o", "json"], cancellation.Token));
                Assert.Equal(1, GenerationRegistry.Executions - before);
                Assert.Equal("", output.ToString());
                Assert.Contains("does not prove rollback", error.ToString());
                Assert.Contains("deployment generation get", error.ToString());
                Assert.DoesNotContain("secret-transport-sentinel", error.ToString());
                if (id == "transport" && binary is not null)
                {
                    var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var arg in new[] { "deployment", "generation", "execute", id, "1", "--config", config, "-o", "json" }) start.ArgumentList.Add(arg);
                    start.Environment["HABY_OPERATOR_URL"] = endpoint;
                    var nativeBefore = GenerationRegistry.Executions;
                    using var process = Process.Start(start)!;
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree: true); throw; }
                    Assert.Equal(3, process.ExitCode);
                    Assert.Equal(1, GenerationRegistry.Executions - nativeBefore);
                    Assert.Equal("", await stdout);
                    var message = await stderr;
                    Assert.Contains("deployment generation get", message);
                    Assert.DoesNotContain("secret-transport-sentinel", message);
                }
            }
        }
        finally { File.Delete(config); await host.StopAsync(); }
    }

    [Fact]
    public void Generation_success_requires_exact_applied_generation_and_all_nodes_succeeded()
    {
        var reply = new GenerationExecutionReply { Status = GenerationExecutionStatus.Succeeded };
        Assert.Throws<InvalidDataException>(() => GenerationPresentation.Exit(reply));
        reply.Progress = new() { Generation = 1, DesiredGeneration = 1, Total = 1, Completed = 1 };
        Assert.Throws<InvalidDataException>(() => GenerationPresentation.Exit(reply));
        reply.Progress.AppliedGeneration = 1;
        reply.Progress.Nodes.Add(new GenerationNodeProgress { Operation = CliOperationTests.Snapshot(OperationStatus.Found, OperationState.Dispatching).Operation });
        Assert.Throws<InvalidDataException>(() => GenerationPresentation.Exit(reply));
        reply.Progress.Nodes[0].Operation.State = OperationState.Succeeded;
        Assert.Throws<InvalidDataException>(() => GenerationPresentation.Exit(reply));
        reply.Progress.ResourcesReady = ResourceCheckpoint();
        Assert.Equal(0, GenerationPresentation.Exit(reply));
    }

    [Fact]
    public void Mixed_completed_nodes_cannot_hide_missing_aggregate_reuse_completion()
    {
        var reply = new GenerationExecutionReply
        {
            Status = GenerationExecutionStatus.Succeeded,
            Progress = new() { Generation = 2, DesiredGeneration = 2, AppliedGeneration = 1, ReusedResources = 2, Total = 1, Completed = 1 }
        };
        reply.Progress.Nodes.Add(new GenerationNodeProgress { Operation = CliOperationTests.Snapshot(OperationStatus.Succeeded, OperationState.Succeeded).Operation });
        Assert.Throws<InvalidDataException>(() => GenerationPresentation.Exit(reply));
        reply.Status = GenerationExecutionStatus.InputUnavailable;
        Assert.Equal(1, GenerationPresentation.Exit(reply));
        reply.Status = GenerationExecutionStatus.Succeeded; reply.Progress.AppliedGeneration = 2;
        reply.Progress.ResourcesReady = ResourceCheckpoint();
        Assert.Equal(0, GenerationPresentation.Exit(reply));
    }

    [Fact]
    public void Reuse_only_success_requires_explicit_reuse_and_an_applied_target_without_fake_nodes()
    {
        var reply = new GenerationExecutionReply
        {
            Status = GenerationExecutionStatus.Succeeded,
            Progress = new() { Generation = 2, DesiredGeneration = 2, AppliedGeneration = 1, ReusedResources = 2 }
        };
        Assert.Throws<InvalidDataException>(() => GenerationPresentation.Exit(reply));
        reply.Progress.AppliedGeneration = 2;
        reply.Progress.ResourcesReady = ResourceCheckpoint();
        Assert.Equal(0, GenerationPresentation.Exit(reply));
        Assert.Equal(2, GenerationPresentation.Progress(reply.Progress)!.ReusedResources);
        reply.Progress.ReusedResources = 0;
        Assert.Throws<InvalidDataException>(() => GenerationPresentation.Exit(reply));
    }
}
