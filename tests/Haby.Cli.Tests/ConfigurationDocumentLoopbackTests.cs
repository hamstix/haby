using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
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
    private sealed class DocumentRegistry : ManifestOperator.ManifestOperatorBase
    {
        internal const string Deployment = "019a2b70-1234-7123-8123-123456789abc";
        internal const string Revision = "019a2b70-1234-7123-8123-123456789abd";
        internal string Mode = "good";
        internal int Calls;
        public override Task<ConfigurationDocumentReply> MaterializeConfiguration(ConfigurationDocumentRequest request, ServerCallContext context) => Reply(request, true, false);
        public override Task<ConfigurationDocumentReply> GetConfiguration(ConfigurationDocumentRequest request, ServerCallContext context) => Reply(request, false, false);
        public override Task<ConfigurationDocumentReply> PreviewConfiguration(ConfigurationDocumentRequest request, ServerCallContext context) => Reply(request, false, true);
        private Task<ConfigurationDocumentReply> Reply(ConfigurationDocumentRequest request, bool materialize, bool preview)
        {
            Calls++;
            Assert.Equal("named-application", request.ApplicationId); Assert.Equal("override", request.Environment);
            Assert.Equal("api", request.ComponentId); Assert.Equal("settings", request.DocumentId); Assert.True(request.HasGeneration); Assert.Equal(3, request.Generation);
            var target = new ConfigurationDocumentIdentity { DeploymentId = Deployment, Generation = 3, ComponentId = "api", DocumentId = "settings" };
            var reply = new ConfigurationDocumentReply
            {
                Selector = request.Clone(),
                Target = target,
                Status = materialize ? ConfigurationDocumentStatus.Existing : ConfigurationDocumentStatus.Found,
                Artifact = new()
                {
                    RevisionId = Revision,
                    Target = target.Clone(),
                    Environment = request.Environment,
                    ApplicationId = request.ApplicationId,
                    SourceRevision = new() { ApplicationRecordId = Revision, RevisionNumber = 1 },
                    PlanId = Revision,
                    FileName = "appsettings.json",
                    Format = "json",
                    Serializer = "haby.configuration-json/v1",
                    Fingerprint = "sha256:configuration-revision-v1:" + new string('a', 64),
                    FrozenInputFingerprint = new string('b', 64),
                    TechnicalActorId = "original-actor",
                    CreatedAtUtc = DateTimeOffset.UnixEpoch.ToString("O")
                }
            };
            if (preview) reply.PreviewJson = ByteString.CopyFromUtf8("{\"document\":{\"Logging\":true,\"Connection\":\"***REDACTED***\"},\"redactedPaths\":[\"/Connection\"]}");
            switch (Mode)
            {
                case "depth-61":
                case "depth-62":
                case "depth-63":
                case "depth-64":
                case "depth-65":
                    var depth = int.Parse(Mode.AsSpan(6), System.Globalization.CultureInfo.InvariantCulture);
                    var document = "true";
                    for (var i = 0; i < depth; i++) document = i % 2 == 0 ? "[" + document + "]" : "{\"child\":" + document + "}";
                    reply.PreviewJson = ByteString.CopyFromUtf8("{\"document\":" + document + ",\"redactedPaths\":[]}");
                    break;
                case "foreign-selector": reply.Selector.Environment = "foreign"; break;
                case "foreign-generation": reply.Target.Generation = 4; break;
                case "missing-metadata": reply.Artifact = null; break;
                case "invalid-audit": reply.Artifact.TechnicalActorId = "\nprivate-sentinel"; break;
                case "malformed": reply.PreviewJson = ByteString.CopyFromUtf8("{private-sentinel"); break;
                case "duplicate": reply.PreviewJson = ByteString.CopyFromUtf8("{\"document\":{},\"document\":\"private-sentinel\",\"redactedPaths\":[]}"); break;
                case "invalid-path": reply.PreviewJson = ByteString.CopyFromUtf8("{\"document\":{\"ordinary\":\"private-sentinel\"},\"redactedPaths\":[\"/ordinary\"]}"); break;
                case "oversized": reply.PreviewJson = ByteString.CopyFromUtf8("{\"document\":\"" + new string('a', 4 * 1024 * 1024) + "\",\"redactedPaths\":[]}"); break;
                case "boundary":
                    const string prefix = "{\"document\":\"Ж\\u003C"; const string suffix = "\",\"redactedPaths\":[]}";
                    var padding = 4 * 1024 * 1024 - Encoding.UTF8.GetByteCount(prefix + suffix);
                    reply.PreviewJson = ByteString.CopyFromUtf8(prefix + new string('a', padding) + suffix); break;
                case "outer-boundary":
                case "outer-over":
                    reply.PreviewJson = ByteString.CopyFromUtf8("{\"document\":\"\",\"redactedPaths\":[]}");
                    var data = ConfigurationDocumentPresentation.Check("preview", request, reply).Data;
                    var baseline = JsonSerializer.Serialize(new CliEnvelope<CliConfigurationDocumentData>(1, "configuration preview", "found", data, []),
                        CliOutputJsonContext.Default.CliEnvelopeCliConfigurationDocumentData);
                    var extra = 8 * 1024 * 1024 - Encoding.UTF8.GetByteCount(baseline) - Encoding.UTF8.GetByteCount(Environment.NewLine) + (Mode == "outer-over" ? 1 : 0);
                    // Each raw '<' costs six bytes after the CLI's required escaping.
                    reply.PreviewJson = ByteString.CopyFromUtf8("{\"document\":\"" + new string('<', extra / 6) + new string('a', extra % 6) + "\",\"redactedPaths\":[]}");
                    break;
                case "wire-boundary":
                case "wire-over":
                    reply.PreviewJson = ByteString.Empty;
                    var desiredWireBytes = 8 * 1024 * 1024 + (Mode == "wire-over" ? 1 : 0);
                    // One field tag and a four-byte length prefix at this size.
                    reply.PreviewJson = ByteString.CopyFrom(new byte[desiredWireBytes - reply.CalculateSize() - 5]);
                    Assert.Equal(desiredWireBytes, reply.CalculateSize());
                    break;
                case "not-found": reply.Status = ConfigurationDocumentStatus.NotFound; reply.Artifact = null; reply.PreviewJson = ByteString.Empty; break;
            }
            return Task.FromResult(reply);
        }
    }

    [Theory]
    [InlineData("good", 0)]
    [InlineData("depth-61", 0)]
    [InlineData("depth-62", 0)]
    [InlineData("depth-63", 0)]
    [InlineData("depth-64", 0)]
    [InlineData("depth-65", 2)]
    [InlineData("boundary", 0)]
    [InlineData("outer-boundary", 0)]
    [InlineData("outer-over", 2)]
    [InlineData("wire-boundary", 2)]
    [InlineData("wire-over", 3)]
    [InlineData("not-found", 1)]
    [InlineData("foreign-selector", 2)]
    [InlineData("foreign-generation", 2)]
    [InlineData("missing-metadata", 2)]
    [InlineData("invalid-audit", 2)]
    [InlineData("malformed", 2)]
    [InlineData("duplicate", 2)]
    [InlineData("invalid-path", 2)]
    [InlineData("oversized", 2)]
    public async Task Configuration_document_loopback_and_native_validate_complete_reply_before_any_output(string mode, int expected)
    {
        var binary = AotBinary();
        var registry = new DocumentRegistry { Mode = mode };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        // Permit a one-over fake reply here to prove the configuration client's receive
        // limit independently of the production Host's matching send limit.
        builder.Services.AddGrpc(o => o.MaxSendMessageSize = mode == "wire-over" ? 9 * 1024 * 1024 : 8 * 1024 * 1024); builder.Services.AddSingleton(registry);
        await using var host = builder.Build(); host.MapGrpcService<DocumentRegistry>(); await host.StartAsync();
        var config = Path.Combine(Path.GetTempPath(), "haby-document-cli-" + Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllTextAsync(config, $"formatVersion: 1\ncurrentContext: wrong\ninstances:\n  selected:\n    endpoint: {host.Urls.Single()}\ncontexts:\n  wrong:\n    instance: selected\n    environmentId: wrong\n  selected:\n    instance: selected\n    environmentId: selected\n");
        try
        {
            foreach (var format in new[] { "text", "json" })
            {
                var args = new[] { "configuration", "preview", "named-application", "--config", config, "--context", "selected", "--environment", "override",
                    "--component", "api", "--document", "settings", "--generation", "3", "--output", format };
                var output = new StringWriter(); var error = new StringWriter();
                var cli = new CliApplication(new FixedEndpoint("http://127.0.0.1:1"), new GrpcRegistryClientFactory(), output, error);
                Assert.Equal(expected, await cli.RunAsync(args)); Verify(output.ToString(), error.ToString(), format);
                if (binary is not null)
                {
                    var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var arg in args) start.ArgumentList.Add(arg);
                    using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                    Assert.Equal(expected, process.ExitCode); Verify(await stdout, await stderr, format);
                }
            }
            var calls = registry.Calls;
            var invalidOutput = new StringWriter(); var invalidError = new StringWriter();
            var invalidCli = new CliApplication(new FixedEndpoint(host.Urls.Single()), new GrpcRegistryClientFactory(), invalidOutput, invalidError);
            Assert.Equal(2, await invalidCli.RunAsync(["configuration", "get", "named-application", "--environment", "override", "--component", "api", "--document", "settings", "--generation", "0", "--config", config]));
            Assert.Equal(calls, registry.Calls); Assert.Equal("", invalidOutput.ToString());
        }
        finally { File.Delete(config); await host.StopAsync(); }

        void Verify(string stdout, string stderr, string format)
        {
            Assert.DoesNotContain("private-sentinel", stdout + stderr);
            if (expected >= 2) { Assert.Equal("", stdout); Assert.NotEqual("", stderr); return; }
            Assert.Equal("", stderr); Assert.Contains(expected == 0 ? "found" : "notFound", stdout);
            if (format == "json")
            {
                using var doc = JsonDocument.Parse(stdout, new JsonDocumentOptions { MaxDepth = 67 }); var data = doc.RootElement.GetProperty("data");
                Assert.Equal(3, data.GetProperty("target").GetProperty("generation").GetInt64());
                if (expected == 0) Assert.Equal(JsonValueKind.Object, data.GetProperty("preview").ValueKind);
                Assert.True(Encoding.UTF8.GetByteCount(stdout) <= 8 * 1024 * 1024);
                if (mode == "outer-boundary") Assert.Equal(8 * 1024 * 1024, Encoding.UTF8.GetByteCount(stdout));
            }
            if (mode.StartsWith("depth-", StringComparison.Ordinal))
            {
                var previewText = format == "json" ? stdout : stdout[(stdout.IndexOf(Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine.Length)..];
                using var parsed = JsonDocument.Parse(previewText, new JsonDocumentOptions { MaxDepth = 67 });
                var preview = format == "json" ? parsed.RootElement.GetProperty("data").GetProperty("preview") : parsed.RootElement;
                var value = preview.GetProperty("document");
                var containers = 0;
                while (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    containers++;
                    value = value.ValueKind == JsonValueKind.Array ? value[0] : value.GetProperty("child");
                }
                Assert.Equal(int.Parse(mode.AsSpan(6), System.Globalization.CultureInfo.InvariantCulture), containers);
                Assert.True(value.GetBoolean());
            }
        }
    }
}
