using System.Diagnostics;
using System.Net;
using Grpc.Core;
using Haby.Cli;
using Haby.Operator.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.Cli.Tests;

public sealed partial class CliLoopbackTests
{
    private sealed class ObservationRegistry : ManifestOperator.ManifestOperatorBase
    {
        internal static string Mode = "fresh";
        internal static int Calls;
        internal static string LastRequest = "";
        public override Task<GenerationReadinessReply> WaitGeneration(GenerationWaitRequest request, ServerCallContext context)
        {
            Interlocked.Increment(ref Calls); Assert.True(Guid.TryParse(request.RequestId, out var id) && id != Guid.Empty);
            Assert.Equal("exact-deployment", request.DeploymentId); Assert.Equal(7, request.Generation);
            return Task.FromResult(Reply(request.RequestId, false));
        }
        public override Task<GenerationReadinessReply> ResumeGeneration(GenerationResumeRequest request, ServerCallContext context)
        {
            Interlocked.Increment(ref Calls); Assert.True(Guid.TryParse(request.RequestId, out var id) && id != Guid.Empty);
            Assert.Equal("Workload/api/main", Assert.Single(request.Members).Subject);
            return Task.FromResult(Reply(request.RequestId, true));
        }
        private static GenerationReadinessReply Reply(string key, bool resume)
        {
            LastRequest = key;
            if (Mode == "unavailable") throw new RpcException(new Status(StatusCode.Unavailable, "private-transport-detail-sentinel"));
            var replay = Mode == "replay";
            var ready = !resume && Mode is not ("pending" or "deadline" or "apiUnavailable");
            var value = new GenerationReadinessReply
            {
                Outcome = resume ? "resumed" : "observed",
                RequestId = key,
                Replayed = replay,
                CurrentReady = ready && !replay,
                OriginalRequestReady = ready,
                CurrentApplied = ready && !replay && !Mode.StartsWith("ready", StringComparison.Ordinal),
                OriginalRequestApplied = ready && !Mode.StartsWith("ready", StringComparison.Ordinal),
                CompletionReason = ready ? Mode.StartsWith("ready", StringComparison.Ordinal) ? "prerequisite-incomplete" : "applied" : "not-ready",
                SubmittedId = Guid.NewGuid().ToString(),
                SavedReadyId = Guid.NewGuid().ToString(),
                Stop = Mode == "deadline" ? "RequestDeadline" : "Complete",
                Total = 1
            };
            value.Members.Add(new GenerationReadinessMember
            {
                Subject = "Workload/api/main",
                AttemptId = Guid.NewGuid().ToString(),
                DeadlineUtc = DateTimeOffset.UtcNow.ToString("O"),
                Budget = "Active",
                Outcome = Mode == "apiUnavailable" ? "Unavailable" : ready ? "Ready" : resume ? "NotObserved" : "Pending",
                Code = Mode == "apiUnavailable" ? "observation-unavailable" : ready ? "ready" : "rollout-pending",
                Temporary = Mode == "apiUnavailable"
            });
            if (ready && !Mode.StartsWith("ready", StringComparison.Ordinal)) { value.RetirementId = Guid.NewGuid().ToString("D"); value.RetirementCompleted = true; value.AppliedGeneration = 7; }
            if (Mode.EndsWith("Canceled", StringComparison.Ordinal)) value.Stop = "Canceled";
            if (Mode.EndsWith("Deadline", StringComparison.Ordinal)) value.Stop = "RequestDeadline";
            if (Mode.EndsWith("Missing", StringComparison.Ordinal)) value.ClearStop();
            if (Mode.EndsWith("Unknown", StringComparison.Ordinal)) value.Stop = "future-stop";
            return value;
        }
        public override Task<WorkloadSubmissionReply> SubmitGeneration(GenerationRequest request, ServerCallContext context) =>
            throw new InvalidOperationException("Observation cannot submit.");
    }
    [Theory]
    [InlineData(false, "fresh", 0)]
    [InlineData(false, "readyOnly", 1)]
    [InlineData(false, "appliedCanceled", 2)]
    [InlineData(false, "appliedDeadline", 2)]
    [InlineData(false, "appliedMissing", 2)]
    [InlineData(false, "appliedUnknown", 2)]
    [InlineData(false, "readyCanceled", 2)]
    [InlineData(false, "readyDeadline", 2)]
    [InlineData(false, "readyMissing", 2)]
    [InlineData(false, "readyUnknown", 2)]
    [InlineData(false, "replay", 1)]
    [InlineData(false, "pending", 1)]
    [InlineData(false, "deadline", 3)]
    [InlineData(false, "unavailable", 3)]
    [InlineData(false, "apiUnavailable", 3)]
    [InlineData(true, "fresh", 0)]
    [InlineData(true, "replay", 0)]
    public async Task Observation_uses_one_generated_RPC_and_truthful_text_JSON_and_native_exits(bool resume, string mode, int expected)
    {
        ObservationRegistry.Mode = mode;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var host = builder.Build(); host.MapGrpcService<ObservationRegistry>(); await host.StartAsync();
        var endpoint = host.Urls.Single(); var binary = AotBinary(); var config = Path.GetTempFileName();
        await File.WriteAllTextAsync(config, "formatVersion: 1\ninstances: {}\ncontexts: {}");
        try
        {
            foreach (var format in new[] { "text", "json" })
            {
                var arguments = new List<string> { "deployment", "generation", resume ? "resume" : "wait", "exact-deployment", "7", "--config", config, "-o", format };
                if (resume) arguments.AddRange(["--member", "Workload/api/main=" + Guid.NewGuid()]);
                using var output = new StringWriter(); using var error = new StringWriter();
                var cli = new CliApplication(new FixedEndpoint(endpoint), new GrpcRegistryClientFactory(), output, error);
                var before = ObservationRegistry.Calls;
                Assert.Equal(expected, await cli.RunAsync(arguments.ToArray())); Assert.Equal(1, ObservationRegistry.Calls - before);
                CheckOutput(output.ToString(), error.ToString());
                if (binary is null) continue;
                var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                start.Environment["HABY_OPERATOR_URL"] = endpoint;
                before = ObservationRegistry.Calls;
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree: true); throw; }
                Assert.Equal(expected, process.ExitCode); Assert.Equal(1, ObservationRegistry.Calls - before);
                CheckOutput(await stdout, await stderr);
            }
        }
        finally { File.Delete(config); await host.StopAsync(); }
        void CheckOutput(string stdout, string stderr)
        {
            if (mode == "unavailable" || mode.StartsWith("applied", StringComparison.Ordinal) || mode.StartsWith("ready", StringComparison.Ordinal) && mode != "readyOnly")
            {
                Assert.Contains(ObservationRegistry.LastRequest, stderr); Assert.Contains("identical inputs", stderr);
                Assert.DoesNotContain("private-transport-detail-sentinel", stderr); Assert.Equal("", stdout);
            }
            else
            {
                Assert.Contains("Workload/api/main", stdout); Assert.Equal("", stderr);
                if (stdout.TrimStart().StartsWith('{'))
                {
                    using var json = System.Text.Json.JsonDocument.Parse(stdout);
                    Assert.Equal(1, json.RootElement.GetProperty("data"u8).GetProperty("total"u8).GetInt32());
                }
            }
        }
    }
    [Fact]
    public void Observation_rejects_prefix_and_replayed_current_ready_claims()
    {
        var value = new GenerationReadinessReply
        {
            Outcome = "observed",
            CurrentReady = true,
            OriginalRequestReady = true,
            SubmittedId = Guid.NewGuid().ToString(),
            SavedReadyId = Guid.NewGuid().ToString(),
            Total = 2
        };
        value.Members.Add(new GenerationReadinessMember { Subject = "Workload/api/main", Outcome = "Ready", Budget = "Active", Code = "ready", AttemptId = Guid.NewGuid().ToString(), DeadlineUtc = DateTimeOffset.UtcNow.ToString("O") });
        Assert.Throws<InvalidDataException>(() => ObservationPresentation.Exit(false, value));
        value.Total = 1; value.Replayed = true;
        Assert.Throws<InvalidDataException>(() => ObservationPresentation.Exit(false, value));
    }
}
