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
    private sealed class EnvironmentRegistry : ManifestOperator.ManifestOperatorBase
    {
        internal const string Identity = "019d03cf-a000-7000-8000-000000000001";
        public override Task<EnvironmentReply> CreateEnvironment(EnvironmentNameRequest request, ServerCallContext context) =>
            Task.FromResult(new EnvironmentReply
            {
                Status = request.Name == "pilot" ? EnvironmentStatus.Existing : EnvironmentStatus.InvalidName,
                Environment = request.Name == "pilot" ? new EnvironmentMetadata { Name = "pilot", RecordId = Identity } : null
            });
        public override Task<EnvironmentReply> GetEnvironment(EnvironmentNameRequest request, ServerCallContext context) =>
            Task.FromResult(new EnvironmentReply
            {
                Status = request.Name == "pilot" ? EnvironmentStatus.Found : EnvironmentStatus.NotFound,
                Environment = request.Name == "pilot" ? new EnvironmentMetadata { Name = "pilot", RecordId = Identity } : null
            });
        public override Task<ListEnvironmentsReply> ListEnvironments(ListEnvironmentsRequest request, ServerCallContext context)
        {
            Assert.Equal(1, request.PageSize);
            if (request.Cursor == "bad") return Task.FromResult(new ListEnvironmentsReply { Status = EnvironmentStatus.InvalidCursor });
            Assert.True(request.Cursor is "" or "env1:pilot");
            var reply = new ListEnvironmentsReply { Status = EnvironmentStatus.Listed };
            if (request.Cursor.Length == 0)
            {
                reply.Items.Add(new EnvironmentMetadata { Name = "pilot", RecordId = Identity });
                reply.NextCursor = "env1:pilot";
            }
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task Environment_commands_use_generated_client_and_native_binary_with_explicit_continuation()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var host = builder.Build();
        host.MapGrpcService<EnvironmentRegistry>();
        await host.StartAsync();
        var config = Path.GetTempFileName();
        try
        {
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
                (["environment", "create", "pilot"], "existing", 0),
                (["environment", "create", "BAD"], "invalidName", 1),
                (["environment", "get", "pilot"], "found", 0),
                (["environment", "get", "never-created"], "notFound", 1),
                (["environment", "list", "--page-size", "1"], "listed", 0),
                (["environment", "list", "--page-size", "1", "--cursor", "env1:pilot"], "listed", 0),
                (["environment", "list", "--page-size", "1", "--cursor", "bad"], "invalidCursor", 1)
            })
                foreach (var format in new[] { "text", "json" })
                {
                    var arguments = args.Concat(new[] { "--config", config, "-o", format }).ToArray();
                    using var output = new StringWriter();
                    using var error = new StringWriter();
                    var cli = new CliApplication(new FixedEndpoint("http://localhost:1"), new GrpcRegistryClientFactory(), output, error);
                    Assert.Equal(exit, await cli.RunAsync(arguments));
                    Assert.Equal("", error.ToString());
                    Verify(output.ToString());
                    if (binary is not null)
                    {
                        var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                        foreach (var arg in arguments) start.ArgumentList.Add(arg);
                        using var process = Process.Start(start)!;
                        var stdout = process.StandardOutput.ReadToEndAsync();
                        var stderr = process.StandardError.ReadToEndAsync();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try { await process.WaitForExitAsync(timeout.Token); }
                        catch { process.Kill(entireProcessTree: true); throw; }
                        Assert.Equal(exit, process.ExitCode);
                        Assert.Equal("", await stderr);
                        Verify(await stdout);
                    }

                    void Verify(string text)
                    {
                        Assert.Contains(outcome, text);
                        if (args.Length == 4 && args[1] == "list") Assert.Contains("env1:pilot", text);
                        if (args[1] is "get" or "create" && exit == 0) Assert.Contains(EnvironmentRegistry.Identity, text);
                        if (format == "json")
                        {
                            using var json = JsonDocument.Parse(text);
                            Assert.Equal(1, json.RootElement.GetProperty("formatVersion"u8).GetInt32());
                            Assert.Equal("environment " + args[1], json.RootElement.GetProperty("command"u8).GetString());
                            Assert.Equal(outcome, json.RootElement.GetProperty("outcome"u8).GetString());
                        }
                    }
                }
        }
        finally { File.Delete(config); await host.StopAsync(); }
    }
}

public sealed class CliEnvironmentPresentationTests
{
    [Fact]
    public void Unknown_or_incomplete_environment_outcomes_fail_before_output()
    {
        using var output = new StringWriter();
        var json = new CliJsonPresenter(output);
        var text = new CliTextPresenter(output);
        foreach (var status in new[] { EnvironmentStatus.Unspecified, (EnvironmentStatus)999, EnvironmentStatus.Listed })
        {
            Assert.Throws<InvalidDataException>(() => json.PrintEnvironment("environment get", new() { Status = status }));
            Assert.Throws<InvalidDataException>(() => text.PrintEnvironment("environment get", new() { Status = status }));
        }
        Assert.Throws<InvalidDataException>(() => json.PrintEnvironment("environment get", new() { Status = EnvironmentStatus.Found }));
        Assert.Throws<InvalidDataException>(() => json.PrintEnvironments(new() { Status = EnvironmentStatus.Unspecified }));
        Assert.Equal("", output.ToString());
    }
}
