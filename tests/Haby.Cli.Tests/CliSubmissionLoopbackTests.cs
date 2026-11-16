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
    private sealed class SubmissionRegistry : ManifestOperator.ManifestOperatorBase
    {
        internal static int Calls;
        public override Task<WorkloadSubmissionReply> SubmitGeneration(GenerationRequest request, ServerCallContext context)
        {
            Interlocked.Increment(ref Calls);
            Assert.Equal("exact-deployment", request.DeploymentId); Assert.Equal(7, request.Generation);
            var reply = new WorkloadSubmissionReply
            {
                Outcome = "submitted",
                Progress = new()
                {
                    DeploymentId = request.DeploymentId,
                    Generation = request.Generation,
                    DesiredGeneration = 7,
                    Submission = "submitted",
                    Completed = 1,
                    Total = 1,
                    ResourcesReadyId = Guid.NewGuid().ToString(),
                    SubmittedId = Guid.NewGuid().ToString()
                }
            };
            reply.Progress.Members.Add(new WorkloadSubmissionMember
            {
                Component = "api",
                Workload = "main",
                Documents = "prepared",
                Identity = "confirmed",
                Publication = "published",
                Submission = "confirmed",
                RuntimeAuthority = "revoked"
            });
            return Task.FromResult(reply);
        }
        public override Task<GenerationExecutionReply> ExecuteGeneration(GenerationRequest request, ServerCallContext context) =>
            throw new InvalidOperationException("Submit cannot delegate to the Resource-only executor.");
    }
    [Fact]
    public async Task Submission_uses_one_generated_RPC_with_safe_text_JSON_and_published_native_output()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var host = builder.Build(); host.MapGrpcService<SubmissionRegistry>(); await host.StartAsync();
        var endpoint = host.Urls.Single(); var binary = AotBinary(); var config = Path.GetTempFileName();
        await File.WriteAllTextAsync(config, "formatVersion: 1\ninstances: {}\ncontexts: {}");
        try
        {
            foreach (var format in new[] { "text", "json" })
            {
                var arguments = new[] { "deployment", "generation", "submit", "exact-deployment", "7", "--config", config, "-o", format };
                using var output = new StringWriter(); using var error = new StringWriter();
                var cli = new CliApplication(new FixedEndpoint(endpoint), new GrpcRegistryClientFactory(), output, error);
                var before = SubmissionRegistry.Calls;
                Assert.Equal(0, await cli.RunAsync(arguments)); Assert.Equal(1, SubmissionRegistry.Calls - before);
                Assert.Contains("submitted", output.ToString()); Assert.Contains("revoked", output.ToString()); Assert.Equal("", error.ToString());
                if (binary is null) continue;
                var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                start.Environment["HABY_OPERATOR_URL"] = endpoint;
                before = SubmissionRegistry.Calls;
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree: true); throw; }
                Assert.Equal(0, process.ExitCode); Assert.Equal(1, SubmissionRegistry.Calls - before);
                Assert.Contains("submitted", await stdout); Assert.Equal("", await stderr);
            }
        }
        finally { File.Delete(config); await host.StopAsync(); }
    }
    [Fact]
    public void A_confirmed_prefix_cannot_be_presented_as_complete_submission()
    {
        var reply = new WorkloadSubmissionReply
        {
            Outcome = "submitted",
            Progress = new()
            {
                Submission = "submitted",
                Completed = 1,
                Total = 2,
                ResourcesReadyId = Guid.NewGuid().ToString(),
                SubmittedId = Guid.NewGuid().ToString()
            }
        };
        reply.Progress.Members.Add(new WorkloadSubmissionMember { Submission = "confirmed" });
        reply.Progress.Members.Add(new WorkloadSubmissionMember { Submission = "notAttempted" });
        Assert.Throws<InvalidDataException>(() => SubmissionPresentation.Exit(reply));
    }
}
