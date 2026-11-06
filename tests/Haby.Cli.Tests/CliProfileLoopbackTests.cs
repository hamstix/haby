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
    private sealed class ProfileRegistry : ManifestOperator.ManifestOperatorBase
    {
        public override Task<ProfileReply> ValidateProfile(ProfilePayloadRequest request, ServerCallContext context)
        {
            Assert.Equal("source-sensitive-marker", request.Payload.ToStringUtf8());
            return Task.FromResult(new ProfileReply { Status = ProfileStatus.Valid, Reference = Reference() });
        }
        public override Task<ProfileReply> ImportProfile(ProfilePayloadRequest request, ServerCallContext context)
        {
            Assert.Equal("source-sensitive-marker", request.Payload.ToStringUtf8());
            return Task.FromResult(new ProfileReply { Status = ProfileStatus.Created, Reference = Reference(), Metadata = Metadata() });
        }
        public override Task<ProfileReply> GetProfileRevision(ProfileRevisionRequest request, ServerCallContext context)
        {
            Assert.Equal("layout", request.ProfileId);
            return Task.FromResult(new ProfileReply
            {
                Status = request.Revision == 1 ? ProfileStatus.Found : ProfileStatus.NotFound,
                Reference = request,
                Metadata = request.Revision == 1 ? Metadata() : null
            });
        }
        private static ProfileRevisionRequest Reference() => new() { ProfileId = "layout", Revision = 1 };
        private static ProfileMetadata Metadata() => new()
        {
            Reference = Reference(),
            TechnicalActorId = "trusted-host",
            ImportedAtUtc = "2026-11-05T15:00:00.0000000+00:00"
        };
    }

    [Fact]
    public async Task Profile_commands_use_configured_generated_client_and_published_binary_with_safe_output()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var host = builder.Build(); host.MapGrpcService<ProfileRegistry>(); await host.StartAsync();
        var file = Path.GetTempFileName(); var config = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "source-sensitive-marker");
            await File.WriteAllTextAsync(config, $"""
                formatVersion: 1
                currentContext: absent
                instances:
                  local:
                    endpoint: {host.Urls.Single()}
                contexts:
                  absent:
                    instance: local
                    environmentId: never-created
                """);
            var binary = AotBinary();
            foreach (var (args, outcome, exit) in new (string[], string, int)[]
            {
                (["profile", "validate", file], "valid", 0),
                (["profile", "import", file], "created", 0),
                (["profile", "revision", "get", "layout", "1"], "found", 0),
                (["profile", "revision", "get", "layout", "2"], "notFound", 1)
            })
                foreach (var format in new[] { "text", "json" })
                {
                    var arguments = args.Concat(new[] { "--config", config, "-o", format }).ToArray();
                    using var output = new StringWriter(); using var error = new StringWriter();
                    var cli = new CliApplication(new FixedEndpoint("http://localhost:1"), new GrpcRegistryClientFactory(), output, error);
                    Assert.Equal(exit, await cli.RunAsync(arguments));
                    Assert.Equal("", error.ToString()); Verify(output.ToString());
                    if (binary is not null)
                    {
                        var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                        foreach (var arg in arguments) start.ArgumentList.Add(arg);
                        using var process = Process.Start(start)!;
                        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                        Assert.Equal(exit, process.ExitCode); Assert.Equal("", await stderr); Verify(await stdout);
                    }
                    void Verify(string text)
                    {
                        Assert.Contains(outcome, text); Assert.Contains("layout", text); Assert.DoesNotContain("source-sensitive-marker", text);
                        if (format == "json")
                        {
                            using var json = JsonDocument.Parse(text);
                            Assert.Equal(outcome, json.RootElement.GetProperty("outcome"u8).GetString());
                            Assert.Equal(1, json.RootElement.GetProperty("formatVersion"u8).GetInt32());
                        }
                        if (outcome is "created" or "found") Assert.Contains("trusted-host", text);
                    }
                }
        }
        finally { File.Delete(file); File.Delete(config); await host.StopAsync(); }
    }
}

public sealed class CliProfilePresentationTests
{
    [Fact]
    public void Malformed_or_wrong_operation_replies_fail_before_any_output()
    {
        using var output = new StringWriter();
        var json = new CliJsonPresenter(output); var text = new CliTextPresenter(output);
        foreach (var reply in new ProfileReply[]
        {
            new(), new() { Status = (ProfileStatus)999 },
            new() { Status = ProfileStatus.Created },
            new() { Status = ProfileStatus.Valid },
            new() { Status = ProfileStatus.NotFound, Reference = new() { ProfileId = "layout" } },
            new() { Status = ProfileStatus.Found, Reference = new() { ProfileId = "layout", Revision = 1 } },
            new() { Status = ProfileStatus.InvalidDeclaration },
            new() { Status = ProfileStatus.RevisionContentConflict, Reference = new() { ProfileId = "layout", Revision = 1 } }
        })
        {
            Assert.Throws<InvalidDataException>(() => json.PrintProfile("profile revision get", reply));
            Assert.Throws<InvalidDataException>(() => text.PrintProfile("profile revision get", reply));
        }
        Assert.Equal("", output.ToString());
    }
}
