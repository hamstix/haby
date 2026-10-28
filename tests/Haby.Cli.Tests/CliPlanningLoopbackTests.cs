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
    private sealed class PlanningRegistry : ManifestOperator.ManifestOperatorBase
    {
        internal const string ChangeId = "019a2b70-1234-7123-8123-123456789abc";
        internal const string PlanId = "019a2b70-1234-7123-8123-123456789abd";
        private static DurableChangeSetSummary Change => new()
        {
            ChangeSetId = ChangeId,
            EnvironmentRecordId = "environment-record",
            Reference = new RevisionReference { ApplicationRecordId = "application", RevisionNumber = 3 },
            Precondition = PlanningPrecondition.ExpectedAbsence,
            CreatedAtUtc = "2026-10-28T00:00:00Z",
            TechnicalActorId = "server-actor"
        };
        private static DurablePlanSummary Plan => new()
        {
            PlanId = PlanningRegistry.PlanId,
            ChangeSetId = ChangeId,
            EnvironmentRecordId = "environment-record",
            EnvironmentId = "pilot",
            Reference = Change.Reference,
            ProviderAlias = "postgresql.primary",
            ProviderInstanceId = "provider-instance",
            ConfigurationRevision = 4,
            ImplementationId = "implementation",
            Fingerprint = "v1:exact",
            CreatedAtUtc = "2026-10-28T00:00:00Z",
            ExpiresAtUtc = "2026-10-29T00:00:00Z",
            TechnicalActorId = "server-actor"
        };
        private static DurableCommitSummary Commit => new()
        {
            ChangeSetId = ChangeId,
            PlanId = PlanId,
            DeploymentId = ChangeId,
            Generation = 1,
            CommittedAtUtc = "2026-10-28T00:00:00Z",
            TechnicalActorId = "server-actor"
        };
        public override Task<CommitReply> CommitDesiredState(CommitDesiredStateRequest request, ServerCallContext context)
        {
            Assert.Equal(ChangeId, request.ChangeSetId);
            Assert.Equal(PlanId, request.PlanId);
            return Task.FromResult(new CommitReply { Status = CommitStatus.Committed, Commit = Commit });
        }
        public override Task<CommitReply> GetCommit(ChangeSetRequest request, ServerCallContext context)
        {
            Assert.Equal(ChangeId, request.ChangeSetId);
            return Task.FromResult(new CommitReply { Status = CommitStatus.Found, Commit = Commit });
        }
        public override Task<CommitReply> GetGeneration(GenerationRequest request, ServerCallContext context)
        {
            Assert.Equal(ChangeId, request.DeploymentId);
            Assert.Equal(1, request.Generation);
            return Task.FromResult(new CommitReply
            {
                Status = CommitStatus.Found,
                DesiredGeneration = new DesiredGenerationSummary
                {
                    DeploymentId = ChangeId,
                    Generation = 1,
                    DesiredPresence = "Present",
                    ChangeSetId = ChangeId,
                    PlanId = PlanId,
                    Reference = Change.Reference,
                    EnvironmentId = "pilot",
                    ProviderInstanceId = Plan.ProviderInstanceId,
                    ConfigurationRevision = 4
                }
            });
        }
        public override Task<PlanningReply> CreateChangeSet(CreateChangeSetRequest request, ServerCallContext context)
        {
            Assert.Equal("pilot", request.EnvironmentId);
            Assert.Equal(PlanningPrecondition.ExpectedAbsence, request.Precondition);
            Assert.Equal(3, request.Reference.RevisionNumber);
            return Task.FromResult(new PlanningReply { Status = PlanningStatus.Created, ChangeSet = Change });
        }
        public override Task<PlanningReply> GetChangeSet(ChangeSetRequest request, ServerCallContext context)
        {
            Assert.Equal(ChangeId, request.ChangeSetId);
            return Task.FromResult(new PlanningReply { Status = PlanningStatus.Found, ChangeSet = Change });
        }
        public override Task<PlanningReply> CreatePlan(ChangeSetRequest request, ServerCallContext context)
        {
            Assert.Equal(ChangeId, request.ChangeSetId);
            return Task.FromResult(new PlanningReply { Status = PlanningStatus.Planned, Plan = Plan });
        }
        public override Task<PlanningReply> GetPlan(PlanRequest request, ServerCallContext context) =>
            Task.FromResult(request.PlanId == PlanId
                ? new PlanningReply { Status = PlanningStatus.Current, Plan = Plan }
                : new PlanningReply { Status = PlanningStatus.PlanNotFound });
        public override Task<PlanningReply> CheckPlan(PlanRequest request, ServerCallContext context) =>
            Task.FromResult(new PlanningReply { Status = PlanningStatus.PlanStale, Plan = Plan });
        public override Task<PlanningReply> ClosePlan(PlanRequest request, ServerCallContext context)
        {
            var plan = Plan;
            plan.ClosedAtUtc = "2026-10-28T01:00:00Z";
            plan.ClosedByTechnicalActorId = "server-actor";
            return Task.FromResult(new PlanningReply { Status = PlanningStatus.Closed, Plan = plan });
        }
    }

    [Fact]
    public async Task Planning_commands_use_context_and_generated_client_in_text_json_and_native_binary()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var host = builder.Build();
        host.MapGrpcService<PlanningRegistry>();
        await host.StartAsync();
        var config = Path.Combine(Path.GetTempPath(), "haby-planning-cli-" + Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllTextAsync(config, $"""
            formatVersion: 1
            currentContext: wrong
            instances:
              local:
                endpoint: {host.Urls.Single()}
            contexts:
              wrong:
                instance: local
                environmentId: wrong
              pilot:
                instance: local
                environmentId: pilot
            """);
        try
        {
            var binary = AotBinary();
            var cases = new (string[] Args, string Command, string Outcome, int Exit)[]
            {
                (["changeset", "create", "application", "3", "--expected-absence"], "changeset create", "created", 0),
                (["changeset", "get", PlanningRegistry.ChangeId], "changeset get", "found", 0),
                (["changeset", "commit", PlanningRegistry.ChangeId, "--plan", PlanningRegistry.PlanId], "changeset commit", "committed", 0),
                (["changeset", "commit", "get", PlanningRegistry.ChangeId], "changeset commit get", "found", 0),
                (["deployment", "generation", "get", PlanningRegistry.ChangeId, "1"], "deployment generation get", "found", 0),
                (["plan", "create", PlanningRegistry.ChangeId], "plan create", "planned", 0),
                (["plan", "get", PlanningRegistry.PlanId], "plan get", "current", 0),
                (["plan", "check", PlanningRegistry.PlanId], "plan check", "planStale", 1),
                (["plan", "close", PlanningRegistry.PlanId], "plan close", "closed", 0),
                (["plan", "get", "unknown"], "plan get", "planNotFound", 1)
            };
            foreach (var (args, command, outcome, exit) in cases)
                foreach (var format in new[] { "text", "json" })
                {
                    var arguments = args.Concat(new[] { "--config", config, "--context", "pilot", "-o", format }).ToArray();
                    using var output = new StringWriter();
                    using var error = new StringWriter();
                    var cli = new CliApplication(new FixedEndpoint("http://localhost:1"), new GrpcRegistryClientFactory(), output, error);
                    Assert.Equal(exit, await cli.RunAsync(arguments));
                    Assert.Equal("", error.ToString());
                    Verify(output.ToString());
                    if (binary is not null)
                    {
                        var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                        foreach (var argument in arguments) start.ArgumentList.Add(argument);
                        using var process = Process.Start(start)!;
                        var stdout = process.StandardOutput.ReadToEndAsync();
                        var stderr = process.StandardError.ReadToEndAsync();
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try { await process.WaitForExitAsync(deadline.Token); }
                        catch { process.Kill(entireProcessTree: true); throw; }
                        Assert.Equal(exit, process.ExitCode);
                        Assert.Equal("", await stderr);
                        Verify(await stdout);
                    }
                    void Verify(string text)
                    {
                        Assert.Contains(outcome, text);
                        if (format != "json") return;
                        using var document = JsonDocument.Parse(text);
                        Assert.Equal(1, document.RootElement.GetProperty("formatVersion"u8).GetInt32());
                        Assert.Equal(command, document.RootElement.GetProperty("command"u8).GetString());
                        Assert.Equal(outcome, document.RootElement.GetProperty("outcome"u8).GetString());
                    }
                }
        }
        finally { File.Delete(config); await host.StopAsync(); }
    }

    [Fact]
    public async Task Planning_requires_context_and_explicit_precondition_before_transport()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = new CliApplication(new FixedEndpoint("http://localhost:1"), new GrpcRegistryClientFactory(), output, error);
        Assert.Equal(2, await cli.RunAsync(["changeset", "create", "application", "1"]));
        // Explicit empty config prevents dependence on the developer's current context.
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "formatVersion: 1\ninstances: {}\ncontexts: {}");
            Assert.Equal(2, await cli.RunAsync(["changeset", "create", "application", "1", "--expected-absence", "--config", path]));
            Assert.Contains("requires a selected context", error.ToString());
            Assert.Equal("", output.ToString());
        }
        finally { File.Delete(path); }
    }
}
