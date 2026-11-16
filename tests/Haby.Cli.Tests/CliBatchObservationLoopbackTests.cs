using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Grpc.Core;
using Google.Protobuf;
using Haby.Operator.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.Cli.Tests;

public sealed partial class CliLoopbackTests
{
    private sealed class BatchRegistry : ManifestOperator.ManifestOperatorBase
    {
        internal static string Mode = "fresh";
        internal static int Calls;
        internal static GenerationBatchWaitRequest? Last;
        public override Task<GenerationBatchReadinessReply> WaitGenerationBatch(GenerationBatchWaitRequest request, ServerCallContext context)
        {
            Calls++; Last = request.Clone(); BatchObservationWire.Validate(request);
            if (Mode == "unavailable") throw new RpcException(new Status(StatusCode.Unavailable, "private-batch-sentinel"));
            if (Mode == "invalid") return Task.FromResult(new GenerationBatchReadinessReply { RequestId = request.RequestId, InvalidRequest = true });
            var reply = new GenerationBatchReadinessReply { RequestId = request.RequestId, Total = request.Members.Count, CurrentReady = Mode == "fresh", Deadline = Mode == "deadline" };
            foreach (var member in request.Members)
                reply.Members.Add(new GenerationBatchSummary
                {
                    Member = member.Clone(),
                    Outcome = Mode == "deadline" ? BatchReadinessOutcome.Interrupted : BatchReadinessOutcome.Observed,
                    CurrentReady = Mode == "fresh",
                    OriginalRequestReady = Mode is "fresh" or "replay",
                    Replayed = Mode == "replay",
                    SubmittedId = Guid.NewGuid().ToString("D"),
                    SavedReadyId = Guid.NewGuid().ToString("D"),
                    Stop = BatchReadinessStop.Complete
                });
            if (Mode == "malformed") reply.Members.RemoveAt(0);
            return Task.FromResult(reply);
        }
    }
    [Theory]
    [InlineData("fresh", 0)]
    [InlineData("replay", 1)]
    [InlineData("pending", 1)]
    [InlineData("invalid", 2)]
    [InlineData("deadline", 3)]
    [InlineData("unavailable", 3)]
    [InlineData("malformed", 3)]
    public async Task Batch_200_members_use_one_generated_RPC_with_ordered_keys_and_native_exits(string mode, int expected)
    {
        BatchRegistry.Mode = mode;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var host = builder.Build(); host.MapGrpcService<BatchRegistry>(); await host.StartAsync();
        var endpoint = host.Urls.Single(); var binary = AotBinary(); var config = Path.GetTempFileName(); var file = Path.GetTempFileName();
        var deployment = "019a2b70-1234-7123-8123-123456789abc";
        var key = Guid.NewGuid().ToString("D");
        await File.WriteAllTextAsync(file, "{\"members\":[" + string.Join(',', Enumerable.Range(1, 200).Select(n => $"{{\"deploymentId\":\"{deployment}\",\"generation\":{n}}}")) + "]}");
        await File.WriteAllTextAsync(config, "formatVersion: 1\ninstances: {}\ncontexts: {}");
        try
        {
            foreach (var format in new[] { "text", "json" })
            {
                string[] arguments = ["deployment", "generation", "wait-batch", "--file", file, "--request-id", key, "--config", config, "-o", format];
                using var output = new StringWriter(); using var error = new StringWriter();
                var cli = new CliApplication(new FixedEndpoint(endpoint), new GrpcRegistryClientFactory(), output, error);
                var before = BatchRegistry.Calls;
                Assert.Equal(expected, await cli.RunAsync(arguments)); Assert.Equal(1, BatchRegistry.Calls - before);
                Check(output.ToString(), error.ToString());
                if (binary is null) continue;
                var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                start.Environment["HABY_OPERATOR_URL"] = endpoint;
                before = BatchRegistry.Calls;
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree: true); throw; }
                Assert.Equal(expected, process.ExitCode); Assert.Equal(1, BatchRegistry.Calls - before); Check(await stdout, await stderr);
            }
        }
        finally { File.Delete(file); File.Delete(config); await host.StopAsync(); }
        void Check(string stdout, string stderr)
        {
            Assert.Equal(key, BatchRegistry.Last!.RequestId); Assert.Equal(200, BatchRegistry.Last.Members.Count);
            Assert.DoesNotContain("private-batch-sentinel", stdout + stderr);
            if (mode is "unavailable" or "malformed")
            {
                Assert.Equal("", stdout); Assert.Contains(key, stderr);
                Assert.Contains(BatchObservationKeys.Member(key, deployment, 200), stderr); Assert.Contains("not guaranteed to resume", stderr);
            }
            else
            {
                Assert.Equal("", stderr);
                if (formatIsJson(stdout))
                {
                    using var json = JsonDocument.Parse(stdout); var data = json.RootElement.GetProperty("data");
                    var members = data.GetProperty("members"); Assert.Equal(mode == "invalid" ? 0 : 200, members.GetArrayLength());
                    if (mode != "invalid") for (var i = 0; i < 200; i++)
                    {
                        Assert.Equal(i + 1, members[i].GetProperty("generation").GetInt64());
                        Assert.Equal(BatchObservationKeys.Member(key, deployment, i + 1), members[i].GetProperty("requestId").GetString());
                    }
                }
            }
        }
        static bool formatIsJson(string value) => value.TrimStart().StartsWith('{');
    }
}

public sealed class BatchInputTests
{
    private const string Deployment = "019a2b70-1234-7123-8123-123456789abc";
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public async Task File_and_wire_full_list_bound_is_checked_before_calling_Host(int count, bool valid)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "{\"members\":[" + string.Join(',', Enumerable.Range(1, count).Select(n => $"{{\"deploymentId\":\"{Deployment}\",\"generation\":{n}}}")) + "]}");
            if (valid)
            {
                var request = await BatchFileReader.ReadAsync(file, "batch", default);
                Assert.Equal(count, request.Members.Count); Assert.True(BatchObservationWire.WorstReply(request).CalculateSize() < BatchObservationWire.ReplyBytes);
            }
            else await Assert.ThrowsAsync<InvalidDataException>(async () => await BatchFileReader.ReadAsync(file, "batch", default));
        }
        finally { File.Delete(file); }
    }
    [Theory]
    [InlineData("{\"members\":null}")]
    [InlineData("{\"members\":[null]}")]
    [InlineData("{\"members\":[],\"members\":[]}")]
    [InlineData("{\"members\":[],\"unknown\":1}")]
    [InlineData("{\"members\":[{\"deploymentId\":\"bad\",\"generation\":1}]}")]
    [InlineData("{\"members\":[{\"deploymentId\":\"019a2b70-1234-7123-8123-123456789abc\",\"generation\":1,\"generation\":2}]}")]
    [InlineData("{\"members\":[{\"deploymentId\":\"019a2b70-1234-7123-8123-123456789abc\",\"generation\":1},{\"deploymentId\":\"019a2b70-1234-7123-8123-123456789abc\",\"generation\":1}]}")]
    [InlineData("{\"members\":[{\"deploymentId\":\"019a2b70-1234-7123-8123-123456789abc\",\"generation\":0}]}")]
    public async Task Strict_file_input_rejects_null_duplicate_unknown_and_invalid_members(string json)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, json);
            var failure = await Record.ExceptionAsync(async () => await BatchFileReader.ReadAsync(file, "batch", default));
            Assert.True(failure is InvalidDataException or JsonException or ArgumentException);
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public async Task Oversized_file_is_rejected_and_keys_are_independent_of_order_and_list_changes()
    {
        var file = Path.GetTempFileName();
        try { await File.WriteAllTextAsync(file, new string(' ', 131073)); await Assert.ThrowsAsync<InvalidDataException>(async () => await BatchFileReader.ReadAsync(file, "batch", default)); }
        finally { File.Delete(file); }
        var first = BatchObservationKeys.Member("batch", Deployment, 1);
        var second = BatchObservationKeys.Member("batch", Deployment, long.MaxValue);
        Assert.Equal("bw1-180d527beaae4d3a1dc3d35573bb5e0e461aae9c8ae13ff8f9d467c81a2a7440", first);
        Assert.Equal("bw1-e8a631d1e17a8a1515f1aa1a3d9674b59b74481fe3dd6a57523539c0af7ce9b5", second);
        Assert.NotEqual(first, second); Assert.Equal(68, first.Length);
        Assert.Equal(first, new[] { long.MaxValue, 1L }.Select(n => BatchObservationKeys.Member("batch", Deployment, n)).Last());
        Assert.NotEqual(first, BatchObservationKeys.Member("batch2", Deployment, 1));
    }
}
