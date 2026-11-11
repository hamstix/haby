using System.Net;
using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Haby.Operator.Contracts;
using Haby.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.Cli.Tests;

public sealed partial class CliLoopbackTests
{
    [Fact]
    public async Task Placement_commands_preserve_scope_credentials_outcomes_full_JSON_and_published_binary()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var host = builder.Build(); host.MapGrpcService<PlacementRegistry>(); await host.StartAsync();
        var folder = Path.Combine(Path.GetTempPath(), "haby-placement-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var config = Path.Combine(folder, "config.yaml"); var file = Path.Combine(folder, "placement.json");
        try
        {
            await File.WriteAllTextAsync(config, $"""
                formatVersion: 1
                currentContext: chosen
                instances:
                  local:
                    endpoint: {host.Urls.Single()}
                    token: haby_operator_v1.11111111111111111111111111111111.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
                contexts:
                  chosen:
                    instance: local
                    environmentId: dev
                """);
            await File.WriteAllTextAsync(file, PlacementSamples.Full);
            foreach (var format in new[] { "json", "text" })
                foreach (var (args, outcome, exit) in new (string[], string, int)[]
                {
                    (["placement", "validate", file], "valid", 0),
                    (["placement", "import", file, "--provider-alias", "cluster"], "created", 0),
                    (["placement", "revision", "get", "standard-runtime", "1", "--provider-alias", "cluster"], "found", 0),
                    (["placement", "revision", "get", "standard-runtime", "2", "--provider-alias", "cluster"], "notFound", 1)
                })
                {
                    var arguments = args.Concat(new[] { "--config", config, "-o", format }).ToArray();
                    using var stdout = new StringWriter(); using var stderr = new StringWriter();
                    var cli = new CliApplication(new FixedEndpoint(host.Urls.Single()), new GrpcRegistryClientFactory(), stdout, stderr);
                    Assert.True(exit == await cli.RunAsync(arguments), string.Join(" ", arguments) + ": " + stderr); Assert.Equal("", stderr.ToString()); Check(stdout.ToString());
                    if (AotBinary() is { } binary)
                    {
                        var result = await RunPlacementBinary(binary, arguments);
                        Assert.True(exit == result.Exit, string.Join(" ", arguments) + ": " + result.Error); Assert.Equal("", result.Error); Check(result.Output);
                    }
                    void Check(string text)
                    {
                        Assert.DoesNotContain("haby_operator_v1.11111111111111111111111111111111.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", text);
                        if (format == "text") { Assert.Contains(outcome, text); return; }
                        using var json = JsonDocument.Parse(text); Assert.Equal(outcome, json.RootElement.GetProperty("outcome").GetString());
                        var data = json.RootElement.GetProperty("data");
                        Assert.Equal(outcome == "found", data.TryGetProperty("declaration", out var declaration));
                        if (outcome == "found")
                        {
                            using var expected = JsonDocument.Parse(PlacementSamples.Full);
                            Assert.True(JsonElement.DeepEquals(expected.RootElement, declaration));
                            Assert.Equal("cluster", data.GetProperty("reference").GetProperty("providerAlias").GetString());
                            Assert.Equal("dev", data.GetProperty("reference").GetProperty("environment").GetString());
                        }
                    }
                }
        }
        finally { File.Delete(config); File.Delete(file); Directory.Delete(folder); await host.StopAsync(); }
    }

    private static async Task<(int Exit, string Output, string Error)> RunPlacementBinary(string binary, string[] arguments)
    {
        var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
        return (process.ExitCode, await stdout, await stderr);
    }

    private sealed class PlacementRegistry : ManifestOperator.ManifestOperatorBase
    {
        private static void Authorized(ServerCallContext context) => Assert.Equal("Bearer haby_operator_v1.11111111111111111111111111111111.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", context.RequestHeaders.GetValue("authorization"));
        public override Task<PlacementReply> ValidatePlacement(PlacementPayloadRequest request, ServerCallContext context)
        { Authorized(context); Assert.Equal(PlacementSamples.Full, request.Payload.ToStringUtf8()); return Task.FromResult(new PlacementReply { Status = PlacementStatus.Valid, Reference = Reference("", "") }); }
        public override Task<PlacementReply> ImportPlacement(PlacementPayloadRequest request, ServerCallContext context)
        {
            Authorized(context); Assert.Equal("dev", request.Environment); Assert.Equal("cluster", request.ProviderAlias);
            Assert.Equal(PlacementSamples.Full, request.Payload.ToStringUtf8()); return Task.FromResult(Reply(PlacementStatus.Created));
        }
        public override Task<PlacementReply> GetPlacementRevision(PlacementRevisionRequest request, ServerCallContext context)
        {
            Authorized(context); Assert.Equal("dev", request.Environment); Assert.Equal("cluster", request.ProviderAlias); Assert.Equal("standard-runtime", request.Name);
            return Task.FromResult(request.Revision == 1 ? Reply(PlacementStatus.Found) : new PlacementReply { Status = PlacementStatus.NotFound });
        }
        private static PlacementRevisionRequest Reference(string environment = "dev", string alias = "cluster") => new() { Environment = environment, ProviderAlias = alias, Name = "standard-runtime", Revision = 1 };
        private static PlacementReply Reply(PlacementStatus status) => new()
        {
            Status = status,
            Reference = Reference(),
            Metadata = new()
            {
                RecordId = "b2ec0060-195d-4602-ad07-2a8c1e96e187",
                EnvironmentRecordId = "e3f3416d-ee2c-48c9-b3b3-b48459665ab7",
                TechnicalActorId = "operator-test",
                ImportedAtUtc = "2026-11-11T19:00:00.0000000+00:00"
            },
            DeclarationJson = status == PlacementStatus.Found ? ByteString.CopyFromUtf8(PlacementSamples.Full) : ByteString.Empty
        };
    }
}
