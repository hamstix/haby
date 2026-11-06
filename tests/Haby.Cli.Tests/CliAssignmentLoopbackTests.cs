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
    internal static ProfileAssignmentReply AssignmentReplyForTest() => AssignmentRegistry.Reply("production", ProfileAssignmentStatus.Found);
    private sealed class AssignmentRegistry : ManifestOperator.ManifestOperatorBase
    {
        internal static readonly string Token = Guid.NewGuid().ToString();
        internal int Reads, Validations, Writes;
        internal ProfileAssignmentTarget ValidationTarget { get; set; } = new() { Kind = "environment" };
        internal ProfileAssignmentTarget? ForeignReplyTarget { get; set; }
        public override Task<ProfileAssignmentReply> ValidateProfileAssignment(ProfilePayloadRequest request, ServerCallContext context)
        {
            Interlocked.Increment(ref Validations);
            Assert.Equal("sensitive-assignment-source", request.Payload.ToStringUtf8());
            return Task.FromResult(new ProfileAssignmentReply { Status = ProfileAssignmentStatus.Valid, Target = ValidationTarget.Clone() });
        }
        public override Task<ProfileAssignmentReply> GetProfileAssignment(GetProfileAssignmentRequest request, ServerCallContext context)
        {
            Interlocked.Increment(ref Reads);
            Assert.Equal("environment", request.Target.Kind);
            return Task.FromResult(request.Environment == "new" ? new ProfileAssignmentReply { Status = ProfileAssignmentStatus.NotFound } : Reply(request.Environment, ProfileAssignmentStatus.Found));
        }
        public override Task<ProfileAssignmentReply> ApplyProfileAssignment(ApplyProfileAssignmentRequest request, ServerCallContext context)
        {
            Interlocked.Increment(ref Writes);
            Assert.Equal("sensitive-assignment-source", request.Payload.ToStringUtf8());
            if (ForeignReplyTarget is { } foreign)
            {
                Assert.Equal("2", request.ApplicationVersion);
                if (!request.ExpectedAbsence) Assert.Equal(Token, request.ExpectedConcurrencyToken);
                var reply = Reply(request.Environment, request.ExpectedAbsence ? ProfileAssignmentStatus.Created : ProfileAssignmentStatus.Updated);
                reply.Assignment.ConcurrencyToken = Guid.NewGuid().ToString();
                reply.Target = foreign.Clone();
                reply.Assignment.Target = foreign.Clone();
                if (foreign.Kind == "component") reply.Assignment.CheckedRevision = new()
                { ApplicationRecordId = "137aa6d4-084d-4f8e-8f50-a34206555a0b", RevisionNumber = 1 };
                return Task.FromResult(reply);
            }
            if (request.Environment == "lost") throw new RpcException(new(StatusCode.Unavailable, "private-driver-exception"));
            if (request.Environment == "malformed")
            {
                var reply = Reply(request.Environment, ProfileAssignmentStatus.Created);
                reply.Assignment.ConcurrencyToken = "invalid";
                return Task.FromResult(reply);
            }
            if (request.Environment == "new")
            {
                Assert.True(request.ExpectedAbsence);
                return Task.FromResult(Reply(request.Environment, ProfileAssignmentStatus.Created));
            }
            if (request.ExpectedAbsence || request.ExpectedConcurrencyToken != Token) return Task.FromResult(new ProfileAssignmentReply { Status = ProfileAssignmentStatus.Conflict });
            return Task.FromResult(Reply(request.Environment, ProfileAssignmentStatus.Unchanged));
        }
        internal static ProfileAssignmentReply Reply(string environment, ProfileAssignmentStatus status) => new()
        {
            Status = status,
            Target = new() { Kind = "environment" },
            Assignment = new()
            {
                Target = new() { Kind = "environment" },
                Environment = environment,
                EnvironmentRecordId = "137aa6d4-084d-4f8e-8f50-a34206555a0b",
                ConcurrencyToken = Token,
                TechnicalActorId = "trusted-host",
                ChangedAtUtc = "2026-11-06T08:00:00.0000000+00:00"
            }
        };
    }

    [Fact]
    public async Task Assignment_explicit_preconditions_reject_foreign_targets_before_output_including_published_binary()
    {
        var service = new AssignmentRegistry
        { ValidationTarget = new() { Kind = "component", ApplicationId = "assigned", ComponentRef = "api" } };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(); builder.Services.AddSingleton(service);
        await using var host = builder.Build(); host.MapGrpcService<AssignmentRegistry>(); await host.StartAsync();
        var file = Path.GetTempFileName(); var config = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "sensitive-assignment-source");
            await File.WriteAllTextAsync(config, $"""
                formatVersion: 1
                currentContext: chosen
                instances:
                  local:
                    endpoint: {host.Urls.Single()}
                contexts:
                  chosen:
                    instance: local
                    environmentId: production
                """);
            var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
            if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
            foreach (var native in binary is null ? new[] { false } : new[] { false, true })
                foreach (var format in new[] { "text", "json" })
                    foreach (var absent in new[] { true, false })
                        foreach (var foreign in new ProfileAssignmentTarget[]
                        {
                            new() { Kind = "component", ApplicationId = "assigned", ComponentRef = "worker" },
                            new() { Kind = "component", ApplicationId = "another", ComponentRef = "api" },
                            new() { Kind = "application", ApplicationId = "assigned" },
                            new() { Kind = "environment" }
                        })
                        {
                            service.ForeignReplyTarget = foreign;
                            var before = (service.Reads, service.Validations, service.Writes);
                            string[] precondition = absent ? ["--if-absent"] : ["--if-match", AssignmentRegistry.Token];
                            string[] args = ["profile", "assignment", "apply", file, "--application-version", "2",
                                .. precondition, "--config", config, "-o", format];
                            string stdout, stderr; int code;
                            if (native)
                            {
                                var start = new ProcessStartInfo(binary!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                                foreach (var arg in args) start.ArgumentList.Add(arg);
                                using var process = Process.Start(start)!;
                                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                                code = process.ExitCode; stdout = await output; stderr = await error;
                            }
                            else
                            {
                                using var output = new StringWriter(); using var error = new StringWriter();
                                var cli = new CliApplication(new FixedEndpoint("http://localhost:1"), new GrpcRegistryClientFactory(), output, error);
                                code = await cli.RunAsync(args); stdout = output.ToString(); stderr = error.ToString();
                            }
                            Assert.Equal(3, code);
                            Assert.Equal("", stdout);
                            Assert.Contains("Assignment outcome is uncertain", stderr);
                            Assert.Contains("Read the exact scope", stderr);
                            Assert.DoesNotContain("sensitive-assignment-source", stderr);
                            Assert.Equal((0, 1, 1), (service.Reads - before.Reads, service.Validations - before.Validations, service.Writes - before.Writes));
                        }
        }
        finally { File.Delete(file); File.Delete(config); await host.StopAsync(); }
    }

    [Fact]
    public async Task Assignment_commands_and_published_binary_use_default_read_CAS_explicit_flags_and_no_retry()
    {
        var service = new AssignmentRegistry();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(); builder.Services.AddSingleton(service);
        await using var host = builder.Build(); host.MapGrpcService<AssignmentRegistry>(); await host.StartAsync();
        var file = Path.GetTempFileName(); var config = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "sensitive-assignment-source");
            await File.WriteAllTextAsync(config, $"""
                formatVersion: 1
                currentContext: chosen
                instances:
                  local:
                    endpoint: {host.Urls.Single()}
                contexts:
                  chosen:
                    instance: local
                    environmentId: production
                """);
            var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
            if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
            foreach (var native in binary is null ? new[] { false } : new[] { false, true })
                foreach (var format in new[] { "text", "json" })
                    foreach (var (command, outcome, exit, reads, validations, writes) in new (string[], string?, int, int, int, int)[]
                    {
                        (["validate", file], "valid", 0, 0, 1, 0),
                        (["get"], "found", 0, 1, 0, 0),
                        (["apply", file], "unchanged", 0, 1, 1, 1),
                        (["apply", file, "--environment", "new"], "created", 0, 1, 1, 1),
                        (["apply", file, "--if-absent"], "conflict", 1, 0, 1, 1),
                        (["apply", file, "--if-match", AssignmentRegistry.Token], "unchanged", 0, 0, 1, 1),
                        (["apply", file, "--if-match", Guid.NewGuid().ToString()], "conflict", 1, 0, 1, 1),
                        (["apply", file, "--if-absent", "--if-match", AssignmentRegistry.Token], null, 2, 0, 0, 0),
                        (["apply", file, "--if-match", "bad"], null, 2, 0, 0, 0),
                        (["get", "--component", "api"], null, 2, 0, 0, 0),
                        (["apply", file, "--environment", "lost", "--if-absent"], null, 3, 0, 1, 1),
                        (["apply", file, "--environment", "malformed", "--if-absent"], null, 3, 0, 1, 1)
                    })
                    {
                        var before = (service.Reads, service.Validations, service.Writes);
                        var args = new[] { "profile", "assignment" }.Concat(command).Concat(["--config", config, "-o", format]).ToArray();
                        string stdout, stderr; int code;
                        if (native)
                        {
                            var start = new ProcessStartInfo(binary!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                            foreach (var arg in args) start.ArgumentList.Add(arg);
                            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                            code = process.ExitCode; stdout = await output; stderr = await error;
                        }
                        else
                        {
                            using var output = new StringWriter(); using var error = new StringWriter();
                            var cli = new CliApplication(new FixedEndpoint("http://localhost:1"), new GrpcRegistryClientFactory(), output, error);
                            code = await cli.RunAsync(args); stdout = output.ToString(); stderr = error.ToString();
                        }
                        Assert.Equal(exit, code);
                        Assert.Equal((reads, validations, writes), (service.Reads - before.Reads, service.Validations - before.Validations, service.Writes - before.Writes));
                        if (outcome is not null)
                        {
                            Assert.Equal("", stderr); Assert.Contains(outcome, stdout);
                            if (format == "json") { using var json = JsonDocument.Parse(stdout); Assert.Equal(outcome, json.RootElement.GetProperty("outcome"u8).GetString()); }
                        }
                        else { Assert.Equal("", stdout); Assert.NotEqual("", stderr); }
                        if (exit == 3) { Assert.Contains("Read the exact scope", stderr); Assert.Contains("not which request", stderr); }
                        Assert.DoesNotContain("sensitive-assignment-source", stdout + stderr);
                        Assert.DoesNotContain("private-driver-exception", stdout + stderr);
                    }
        }
        finally { File.Delete(file); File.Delete(config); await host.StopAsync(); }
    }
}

public sealed class CliAssignmentPresentationTests
{
    [Fact]
    public void Assignment_malformed_replies_are_rejected_before_text_or_JSON_output()
    {
        using var output = new StringWriter(); var json = new CliJsonPresenter(output); var text = new CliTextPresenter(output);
        foreach (var mutation in new Action<ProfileAssignmentReply>[]
        {
            r => r.Status = ProfileAssignmentStatus.Unspecified,
            r => r.Status = (ProfileAssignmentStatus)999,
            r => r.Status = ProfileAssignmentStatus.Valid,
            r => r.Target = null,
            r => r.Assignment = null,
            r => r.Assignment.ConcurrencyToken = "bad",
            r => r.Assignment.TechnicalActorId = "bad\nactor",
            r => r.Assignment.ChangedAtUtc = "invalid",
            r => r.Assignment.EnvironmentRecordId = Guid.Empty.ToString(),
            r => r.Assignment.Target.Kind = "unknown",
            r => r.Assignment.CheckedRevision = new() { ApplicationRecordId = Guid.NewGuid().ToString(), RevisionNumber = 1 },
            r => r.Assignment.Decisions.Add(new ProfileAssignmentDecision() { ProfileId = "layout", Disable = false }),
            r => r.Assignment.Decisions.Add(new ProfileAssignmentDecision() { ProfileId = "layout", UseRevision = 0 }),
            r => { r.Assignment.Decisions.Add(new ProfileAssignmentDecision() { ProfileId = "layout", UseRevision = 1 }); r.Assignment.Decisions.Add(new ProfileAssignmentDecision() { ProfileId = "layout", Disable = true }); },
            r => r.ErrorCount = 1,
            r => r.Diagnostics.Add(new ProfileDiagnostic() { Code = "bad", Path = "" })
        })
        {
            var reply = CliLoopbackTests.AssignmentReplyForTest(); mutation(reply);
            Assert.Throws<InvalidDataException>(() => json.PrintAssignment("profile assignment get", reply));
            Assert.Throws<InvalidDataException>(() => text.PrintAssignment("profile assignment get", reply));
        }
        Assert.Equal("", output.ToString());
    }
}
