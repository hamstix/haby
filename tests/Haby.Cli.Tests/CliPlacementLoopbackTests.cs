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
            foreach (var kind in new[] { "named", "environment", "application", "component" })
                foreach (var format in new[] { "json", "text" })
                    foreach (var operation in new[] { "get", "create", "equal", "missing", "ambiguous", "false-absence", "bad-target" })
                    {
                        List<string> arguments = ["placement", "selection", operation == "get" ? "get" : "apply", kind, "--provider-alias", "cluster", "--config", config, "-o", format];
                        if (kind == "named") arguments.AddRange(["--selector", "production"]);
                        if (kind is "application" or "component") arguments.AddRange(["--application", "assigned"]);
                        if (kind == "component") arguments.AddRange(["--component", "api"]);
                        if (operation != "get")
                        {
                            arguments.AddRange(["--placement", "standard-runtime", "--revision", "2"]);
                            if (kind == "component") arguments.AddRange(["--application-version", "explicit-2"]);
                            if (operation is "equal" or "ambiguous" or "false-absence") arguments.AddRange(["--expected-token", "11111111-1111-4111-8111-111111111111"]);
                            if (operation is "create" or "ambiguous" or "bad-target") arguments.Add("--expected-absence");
                            if (operation == "false-absence") arguments.AddRange(["--expected-absence", "false"]);
                            if (operation == "bad-target") arguments.AddRange(kind == "named" ? ["--application", "extra"] : ["--selector", "extra"]);
                        }
                        var exit = operation is "missing" or "ambiguous" or "false-absence" or "bad-target" ? 2 : 0;
                        using var stdout = new StringWriter(); using var stderr = new StringWriter();
                        var cli = new CliApplication(new FixedEndpoint(host.Urls.Single()), new GrpcRegistryClientFactory(), stdout, stderr);
                        Assert.True(exit == await cli.RunAsync(arguments.ToArray()), string.Join(" ", arguments) + ": " + stderr);
                        CheckSelection(stdout.ToString());
                        if (AotBinary() is { } binary)
                        {
                            var result = await RunPlacementBinary(binary, arguments.ToArray()); Assert.Equal(exit, result.Exit); CheckSelection(result.Output);
                        }
                        void CheckSelection(string text)
                        {
                            if (exit != 0) return;
                            var outcome = operation == "get" ? "found" : operation == "create" ? "created" : "unchanged";
                            if (format == "text") { Assert.Contains(outcome, text); Assert.Contains("concurrencyToken:", text); return; }
                            using var json = JsonDocument.Parse(text); Assert.Equal(outcome, json.RootElement.GetProperty("outcome"u8).GetString());
                            var data = json.RootElement.GetProperty("data"u8);
                            Assert.Equal(kind, data.GetProperty("target"u8).GetProperty("kind"u8).GetString());
                            Assert.Equal("11111111-1111-4111-8111-111111111111", data.GetProperty("concurrencyToken"u8).GetString());
                            Assert.Equal(kind == "component", data.TryGetProperty("checkedRevision"u8, out _));
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
        public override Task<PlacementSelectionReply> ApplyPlacementSelection(ApplyPlacementSelectionRequest request, ServerCallContext context)
        {
            Authorized(context); Assert.Equal("dev", request.Environment); Assert.Equal("cluster", request.ProviderAlias);
            Assert.Equal("standard-runtime", request.PlacementName); Assert.Equal(2, request.Revision);
            Assert.Equal(request.Target.Kind == "component", request.HasApplicationVersion);
            if (request.HasApplicationVersion) Assert.Equal("explicit-2", request.ApplicationVersion);
            if (request.HasExpectedAbsence)
            { Assert.True(request.ExpectedAbsence); return Task.FromResult(SelectionReply(request.Target, PlacementSelectionStatus.Created)); }
            Assert.Equal("11111111-1111-4111-8111-111111111111", request.ExpectedToken);
            return Task.FromResult(SelectionReply(request.Target, PlacementSelectionStatus.Unchanged));
        }
        public override Task<PlacementSelectionReply> GetPlacementSelection(GetPlacementSelectionRequest request, ServerCallContext context)
        {
            Authorized(context); Assert.Equal("dev", request.Environment); Assert.Equal("cluster", request.ProviderAlias);
            return Task.FromResult(SelectionReply(request.Target, PlacementSelectionStatus.Found));
        }
        private static PlacementSelectionReply SelectionReply(PlacementSelectionTarget target, PlacementSelectionStatus status) => new()
        {
            Status = status,
            Selection = new()
            {
                Target = target.Clone(),
                Reference = new() { Environment = "dev", ProviderAlias = "cluster", Name = "standard-runtime", Revision = 2 },
                EnvironmentRecordId = "e3f3416d-ee2c-48c9-b3b3-b48459665ab7",
                ConcurrencyToken = "11111111-1111-4111-8111-111111111111",
                TechnicalActorId = "operator-test",
                ChangedAtUtc = "2026-11-11T19:00:00.0000000+00:00",
                CheckedRevision = target.Kind == "component" ? new() { ApplicationRecordId = "b2ec0060-195d-4602-ad07-2a8c1e96e187", RevisionNumber = 2 } : null
            }
        };
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
