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
    private sealed class OperationRegistry : ManifestOperator.ManifestOperatorBase
    {
        internal static int Executions;
        public override async Task<OperationReply> ExecuteOperation(OperationRequest request, ServerCallContext context)
        {
            Interlocked.Increment(ref Executions);
            Assert.Equal("deployment", request.DeploymentId); Assert.Equal(1, request.Generation);
            if (request.OperationId == "cancel")
            {
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
            }
            if (request.OperationId == "transport")
                throw new RpcException(new Status(StatusCode.Unavailable, "secret-transport-sentinel"));
            return request.OperationId switch
            {
                "no-handler" => CliOperationTests.Snapshot(OperationStatus.HandlerUnavailable, OperationState.Pending),
                "success" => CliOperationTests.Snapshot(OperationStatus.Succeeded, OperationState.Succeeded),
                "conflict" => CliOperationTests.Snapshot(OperationStatus.Failed, OperationState.Failed),
                "retry" => CliOperationTests.Snapshot(OperationStatus.RetryableFailure, OperationState.Preparing),
                "attention" => CliOperationTests.Snapshot(OperationStatus.NeedsAttention, OperationState.NeedsAttention),
                "uncertain" => CliOperationTests.Snapshot(OperationStatus.DispatchUncertain, OperationState.Dispatching),
                _ => new OperationReply { Status = OperationStatus.OperationNotFound }
            };
        }
        public override Task<OperationReply> GetOperation(OperationRequest request, ServerCallContext context)
        {
            Assert.Equal("deployment", request.DeploymentId); Assert.Equal(1, request.Generation);
            Assert.True(request.OperationId == "db" || request.OperationId.StartsWith("reason-", StringComparison.Ordinal));
            var reply = CliOperationTests.Snapshot(OperationStatus.Found,
                request.OperationId == "db" ? OperationState.Dispatching : OperationState.Failed);
            if (request.OperationId.StartsWith("reason-", StringComparison.Ordinal))
                reply.Operation.Reason = (OperationReason)int.Parse(request.OperationId[7..], System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task Operation_commands_use_generated_transport_and_native_text_json_without_blind_retries()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var host = builder.Build(); host.MapGrpcService<OperationRegistry>(); await host.StartAsync();
        var binary = AotBinary();
        var endpoint = host.Urls.Single();
        var config = Path.GetTempFileName();
        await File.WriteAllTextAsync(config, "formatVersion: 1\ninstances: {}\ncontexts: {}");
        try
        {
            var cases = new (string Verb, string Resource, string Outcome, int Exit)[]
            {
                ("get", "db", "found", 0), ("execute", "no-handler", "handlerUnavailable", 2), ("execute", "success", "succeeded", 0),
                ("execute", "conflict", "failed", 1), ("execute", "retry", "retryableFailure", 1),
                ("execute", "attention", "needsAttention", 1), ("execute", "uncertain", "dispatchUncertain", 1),
                ("execute", "missing", "operationNotFound", 1), ("execute", "transport", "", 3)
            };
            cases = [.. cases, .. new[] { 0, 2, 5, 6, 7, 8, 9, 10, 11 }.Select(n => ("get", "reason-" + n, "found", 0))];
            foreach (var (verb, resource, outcome, exit) in cases)
                foreach (var format in new[] { "text", "json" })
                {
                    var args = new[] { "operation", verb, "deployment", "1", resource, "--config", config, "-o", format };
                    using var output = new StringWriter(); using var error = new StringWriter();
                    var cli = new CliApplication(new FixedEndpoint(endpoint), new GrpcRegistryClientFactory(), output, error);
                    var callsBefore = OperationRegistry.Executions;
                    Assert.Equal(exit, await cli.RunAsync(args));
                    Assert.Equal(verb == "execute" ? 1 : 0, OperationRegistry.Executions - callsBefore);
                    Verify(output.ToString(), error.ToString());
                    if (binary is not null)
                    {
                        var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                        foreach (var argument in args) start.ArgumentList.Add(argument);
                        start.Environment["HABY_OPERATOR_URL"] = endpoint;
                        using var process = Process.Start(start)!;
                        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree: true); throw; }
                        Assert.Equal(exit, process.ExitCode); Verify(await stdout, await stderr);
                    }
                    void Verify(string text, string errors)
                    {
                        Assert.DoesNotContain("secret-transport-sentinel", text + errors);
                        if (exit == 3) { Assert.Equal("", text); Assert.NotEqual("", errors); return; }
                        Assert.Equal("", errors); Assert.Contains(outcome, text);
                        string?[] spellings = [null, "providerInputUnavailable", "secretUnavailable", "unsupportedShape",
                            "databaseNameInvalid", "connectivity", "conflict", "serverRejected", "uncertainAfterDispatch",
                            "inputInvalid", "inputUnavailable", "handlerUnavailable"];
                        var reason = resource.StartsWith("reason-", StringComparison.Ordinal)
                            ? spellings[int.Parse(resource[7..], System.Globalization.CultureInfo.InvariantCulture)] : null;
                        if (reason is not null) Assert.Contains(reason, text);
                        if (format != "json") return;
                        using var json = JsonDocument.Parse(text);
                        Assert.Equal("operation " + verb, json.RootElement.GetProperty("command"u8).GetString());
                        Assert.Equal(outcome, json.RootElement.GetProperty("outcome"u8).GetString());
                        if (resource.StartsWith("reason-", StringComparison.Ordinal))
                        {
                            var operation = json.RootElement.GetProperty("data").GetProperty("operation");
                            if (reason is null) Assert.False(operation.TryGetProperty("reason", out _));
                            else Assert.Equal(reason, operation.GetProperty("reason").GetString());
                        }
                    }
                }
            using var canceledOutput = new StringWriter(); using var canceledError = new StringWriter();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var canceledCli = new CliApplication(new FixedEndpoint(endpoint), new GrpcRegistryClientFactory(), canceledOutput, canceledError);
            Assert.Equal(3, await canceledCli.RunAsync(["operation", "execute", "deployment", "1", "cancel", "--config", config], cancellation.Token));
            Assert.Equal("", canceledOutput.ToString());
        }
        finally { File.Delete(config); await host.StopAsync(); }
    }
}
