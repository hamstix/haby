using System.Diagnostics;
using System.Net;
using System.Text;
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
    private sealed class ApplicationRegistry(string mode, string credential) : ManifestOperator.ManifestOperatorBase
    {
        internal int Calls;
        public override Task<ConfigurationApplicationReply> MaterializeApplicationConfiguration(ConfigurationApplicationRequest request, ServerCallContext context)
        {
            Calls++;
            Assert.Equal("Bearer " + credential, context.RequestHeaders.GetValue("authorization"));
            Assert.Equal("override", request.Environment); Assert.Equal("named-application", request.ApplicationId);
            Assert.True(request.HasGeneration); Assert.Equal(3, request.Generation);
            return Task.FromResult(BatchReply(request, mode));
        }
    }
    private static ConfigurationApplicationReply BatchReply(ConfigurationApplicationRequest request, string mode)
    {
        var reply = new ConfigurationApplicationReply
        {
            Selector = request.Clone(),
            Target = new() { DeploymentId = DocumentRegistry.Deployment, Generation = request.Generation },
            Status = ConfigurationApplicationStatus.Succeeded
        };
        var count = mode switch { "maximum" => 512, "count-over" => 513, "empty" => 0, _ => 3 };
        for (var i = 0; i < count; i++)
        {
            var target = new ConfigurationDocumentIdentity
            {
                DeploymentId = DocumentRegistry.Deployment,
                Generation = request.Generation,
                ComponentId = "a" + new string('z', 62),
                DocumentId = "d" + i.ToString("D4") + new string('z', 58)
            };
            reply.Items.Add(new ConfigurationApplicationItem
            {
                Target = target,
                Status = ConfigurationDocumentStatus.Created,
                Artifact = new()
                {
                    Target = target.Clone(),
                    RevisionId = Guid.CreateVersion7().ToString(),
                    Environment = request.Environment,
                    ApplicationId = request.ApplicationId,
                    SourceRevision = new() { ApplicationRecordId = DocumentRegistry.Revision, RevisionNumber = long.MaxValue },
                    PlanId = DocumentRegistry.Revision,
                    FileName = new string('\uFFFF', 255),
                    Format = "json",
                    Serializer = "haby.configuration-json/v1",
                    Fingerprint = "sha256:configuration-revision-v1:" + new string('a', 64),
                    FrozenInputFingerprint = new string('b', 64),
                    TechnicalActorId = new string('\uFFFF', 256),
                    CreatedAtUtc = DateTimeOffset.UnixEpoch.ToString("O")
                }
            });
        }
        reply.SelectedCount = count;
        switch (mode)
        {
            case "partial":
                reply.Status = ConfigurationApplicationStatus.Failed;
                reply.Items[1].Status = ConfigurationDocumentStatus.StorageUnavailable; reply.Items[1].Artifact = null;
                reply.Items[2].Status = ConfigurationDocumentStatus.NotAttempted; reply.Items[2].Artifact = null; break;
            case "missing": reply.Items.RemoveAt(1); break;
            case "duplicate": reply.Items[1] = reply.Items[0].Clone(); break;
            case "out-of-order": (reply.Items[0], reply.Items[1]) = (reply.Items[1], reply.Items[0]); break;
            case "foreign-selector": reply.Selector.ApplicationId = "foreign"; break;
            case "foreign-generation": reply.Target.Generation++; break;
            case "foreign-item": reply.Items[1].Target.DeploymentId = DocumentRegistry.Revision; break;
            case "missing-artifact": reply.Items[1].Artifact = null; break;
            case "wrong-artifact": reply.Items[1].Artifact.Target = reply.Items[0].Target.Clone(); break;
            case "duplicate-revision": reply.Items[1].Artifact.RevisionId = reply.Items[0].Artifact.RevisionId; break;
            case "invalid-audit": reply.Items[1].Artifact.TechnicalActorId = "private-sentinel\n"; break;
            case "oversize-audit": reply.Items[1].Artifact.TechnicalActorId = new string('a', 257); break;
            case "oversize-wire": reply.Items[1].Artifact.TechnicalActorId = new string('a', 8 * 1024 * 1024); break;
            case "not-attempted": reply.Items[1].Status = ConfigurationDocumentStatus.NotAttempted; reply.Items[1].Artifact = null; break;
            case "success-after-failure": reply.Items[0].Status = ConfigurationDocumentStatus.InputUnavailable; reply.Items[0].Artifact = null; break;
            case "false-success": reply.Items[2].Status = ConfigurationDocumentStatus.InputUnavailable; reply.Items[2].Artifact = null; break;
            case "false-failure": reply.Status = ConfigurationApplicationStatus.Failed; break;
            case "unknown": reply.Items[1].Status = (ConfigurationDocumentStatus)999; break;
        }
        return reply;
    }

    [Theory]
    [InlineData("good", 0)]
    [InlineData("maximum", 0)]
    [InlineData("empty", 0)]
    [InlineData("partial", 1)]
    [InlineData("count-over", 2)]
    [InlineData("missing", 2)]
    [InlineData("duplicate", 2)]
    [InlineData("out-of-order", 2)]
    [InlineData("foreign-selector", 2)]
    [InlineData("foreign-generation", 2)]
    [InlineData("foreign-item", 2)]
    [InlineData("missing-artifact", 2)]
    [InlineData("wrong-artifact", 2)]
    [InlineData("duplicate-revision", 2)]
    [InlineData("invalid-audit", 2)]
    [InlineData("oversize-audit", 2)]
    [InlineData("oversize-wire", 3)]
    [InlineData("not-attempted", 2)]
    [InlineData("success-after-failure", 2)]
    [InlineData("false-success", 2)]
    [InlineData("false-failure", 2)]
    [InlineData("unknown", 2)]
    public async Task Application_batch_loopback_and_native_send_one_authenticated_request_and_validate_every_item_before_stdout(string mode, int expected)
    {
        var binary = AotBinary(); var credential = OperatorToken(); var registry = new ApplicationRegistry(mode, credential);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(o => o.MaxSendMessageSize = 9 * 1024 * 1024); builder.Services.AddSingleton(registry);
        await using var host = builder.Build(); host.MapGrpcService<ApplicationRegistry>(); await host.StartAsync();
        var config = Path.Combine(Path.GetTempPath(), "haby-batch-cli-" + Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllTextAsync(config, $"formatVersion: 1\ncurrentContext: wrong\ninstances:\n  selected:\n    endpoint: {host.Urls.Single()}\n    token: {credential}\ncontexts:\n  wrong:\n    instance: selected\n    environmentId: wrong\n  selected:\n    instance: selected\n    environmentId: selected\n");
        try
        {
            foreach (var format in new[] { "text", "json" })
            {
                var args = new[] { "configuration", "materialize", "named-application", "--all", "--config", config, "--context", "selected", "--environment", "override", "--generation", "3", "--output", format };
                var output = new StringWriter(); var error = new StringWriter();
                var cli = new CliApplication(new FixedEndpoint("http://127.0.0.1:1"), new GrpcRegistryClientFactory(), output, error);
                var before = registry.Calls;
                Assert.Equal(expected, await cli.RunAsync(args)); Assert.Equal(before + 1, registry.Calls); Verify(output.ToString(), error.ToString(), format);
                if (binary is not null)
                {
                    var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var arg in args) start.ArgumentList.Add(arg);
                    using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                    Assert.Equal(expected, process.ExitCode); Assert.Equal(before + 2, registry.Calls); Verify(await stdout, await stderr, format);
                }
            }
            var calls = registry.Calls;
            foreach (var flags in new[] { new[] { "--all", "--component", "api" }, new[] { "--all", "--document", "settings" }, Array.Empty<string>(), new[] { "--component", "api" }, new[] { "--all", "--generation", "0" } })
            {
                var output = new StringWriter(); var error = new StringWriter();
                var cli = new CliApplication(new FixedEndpoint(host.Urls.Single()), new GrpcRegistryClientFactory(), output, error);
                Assert.Equal(2, await cli.RunAsync(new[] { "configuration", "materialize", "named-application", "--config", config }.Concat(flags).ToArray()));
                Assert.Equal(calls, registry.Calls); Assert.Equal("", output.ToString());
            }
        }
        finally { File.Delete(config); await host.StopAsync(); }
        void Verify(string stdout, string stderr, string format)
        {
            Assert.DoesNotContain("private-sentinel", stdout + stderr);
            if (expected >= 2) { Assert.Equal("", stdout); Assert.NotEqual("", stderr); return; }
            Assert.Equal("", stderr); Assert.Contains(expected == 0 ? "succeeded" : "failed", stdout);
            if (format == "json")
            {
                using var doc = JsonDocument.Parse(stdout);
                var data = doc.RootElement.GetProperty("data"u8); var items = data.GetProperty("items"u8).EnumerateArray().ToArray();
                Assert.Equal(mode == "maximum" ? 512 : mode == "empty" ? 0 : 3, items.Length);
                Assert.Equal(3, data.GetProperty("target"u8).GetProperty("generation"u8).GetInt64());
                Assert.True(Encoding.UTF8.GetByteCount(stdout) < 8 * 1024 * 1024);
                if (mode == "maximum") Assert.True(Encoding.UTF8.GetByteCount(stdout) < 16 * 1024 + 512 * 12 * 1024);
            }
        }
    }
}
