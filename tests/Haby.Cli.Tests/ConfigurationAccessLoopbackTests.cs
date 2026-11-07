using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
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
    private sealed class BrokenCredentialOutput : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("private-output-diagnostic");
    }
    private sealed class AccessRegistry : ManifestOperator.ManifestOperatorBase
    {
        public int Calls;
        public readonly string Id = Guid.CreateVersion7().ToString();
        public string Credential = "";
        public ConfigurationAccessMetadata Grant = null!;
        public override async Task<ConfigurationAccessReply> CreateConfigurationAccess(CreateConfigurationAccessRequest request, ServerCallContext context)
        {
            Calls++;
            var artifact = await new DocumentRegistry().GetConfiguration(request.Selector, context);
            Grant = new() { GrantId = Id, Document = artifact.Artifact, Actor = "trusted-actor", CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O") };
            if (request.HasExpiresAtUtc) Grant.ExpiresAtUtc = request.ExpiresAtUtc;
            Credential = "haby_delivery_v1." + Guid.Parse(Id).ToString("N") + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            return new() { Status = ConfigurationAccessStatus.Created, Grant = Grant.Clone(), Credential = Credential };
        }
        public override Task<ConfigurationAccessReply> GetConfigurationAccess(ConfigurationAccessRequest request, ServerCallContext context)
        {
            Calls++; Assert.Equal(Id, request.GrantId);
            return Task.FromResult(new ConfigurationAccessReply { Status = ConfigurationAccessStatus.Found, Grant = Grant.Clone() });
        }
        public override Task<ConfigurationAccessReply> RevokeConfigurationAccess(ConfigurationAccessRequest request, ServerCallContext context)
        {
            Calls++; Assert.Equal(Id, request.GrantId);
            if (!Grant.HasRevokedAtUtc) { Grant.RevokedAtUtc = DateTimeOffset.UtcNow.ToString("O"); Grant.RevokedBy = "trusted-revoker"; }
            return Task.FromResult(new ConfigurationAccessReply { Status = ConfigurationAccessStatus.Revoked, Grant = Grant.Clone() });
        }
    }

    [Fact]
    public async Task Configuration_access_explicit_sensitive_output_flags_new_file_metadata_and_native_binary()
    {
        var binary = AotBinary();
        var registry = new AccessRegistry();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(); builder.Services.AddSingleton(registry);
        await using var host = builder.Build(); host.MapGrpcService<AccessRegistry>(); await host.StartAsync();
        var folder = Path.Combine(Path.GetTempPath(), "haby-access-cli-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var config = Path.Combine(folder, "config.yaml"); var credentialFile = Path.Combine(folder, "retained.token");
        await File.WriteAllTextAsync(config, $"formatVersion: 1\ncurrentContext: selected\ninstances:\n  local:\n    endpoint: {host.Urls.Single()}\ncontexts:\n  selected:\n    instance: local\n    environmentId: override\n");
        try
        {
            foreach (var native in binary is null ? new[] { false } : new[] { false, true })
            {
                foreach (var format in new[] { "text", "json" })
                {
                    foreach (var destination in new[] { "file", "stdout" })
                    {
                        File.Delete(credentialFile);
                        var args = new List<string> { "create", "named-application", "--component", "api", "--document", "settings", "--generation", "3" };
                        args.AddRange(destination == "file" ? ["--token-output-file", credentialFile] : ["--token-stdout"]);
                        if (destination == "stdout") args.AddRange(["--expires-at", DateTimeOffset.UtcNow.AddDays(3).ToString("O")]);
                        var (code, stdout, stderr) = await Run(native, format, args.ToArray());
                        Assert.Equal(0, code);
                        Assert.True(registry.Credential.Length == 114);
                        Assert.Equal(destination == "stdout", registry.Grant.HasExpiresAtUtc);
                        Assert.False(stderr.Contains(registry.Credential, StringComparison.Ordinal));
                        if (destination == "file")
                        {
                            Assert.True((await File.ReadAllTextAsync(credentialFile)).TrimEnd() == registry.Credential);
                            Assert.False(stdout.Contains(registry.Credential, StringComparison.Ordinal)); Assert.Equal("", stderr);
                            var calls = registry.Calls;
                            var repeat = await Run(native, format, args.ToArray()); Assert.Equal(2, repeat.Code); Assert.Equal(calls, registry.Calls);
                        }
                        else if (format == "text") Assert.True(stdout.TrimEnd() == registry.Credential);
                        else
                        {
                            using var json = JsonDocument.Parse(stdout);
                            Assert.True(json.RootElement.GetProperty("data"u8).GetProperty("credential"u8).GetString() == registry.Credential);
                        }
                        foreach (var verb in new[] { "get", "revoke", "revoke" })
                        {
                            var ordinary = await Run(native, format, [verb, registry.Id]); Assert.Equal(0, ordinary.Code);
                            Assert.False((ordinary.Stdout + ordinary.Stderr).Contains(registry.Credential, StringComparison.Ordinal));
                            if (format == "json") { using var json = JsonDocument.Parse(ordinary.Stdout); Assert.False(json.RootElement.GetProperty("data"u8).TryGetProperty("credential"u8, out _)); }
                        }
                    }
                }
                var count = registry.Calls;
                foreach (var flags in new[] { Array.Empty<string>(), new[] { "--token-stdout", "--token-output-file", credentialFile }, new[] { "--token-stdout", "--expires-at", "bad" }, new[] { "--token-stdout", "--expires-at", "2000-01-01T00:00:00Z" } })
                {
                    var invalid = await Run(native, "json", new[] { "create", "named-application", "--component", "api", "--document", "settings" }.Concat(flags).ToArray());
                    Assert.Equal(2, invalid.Code); Assert.Equal("", invalid.Stdout); Assert.Equal(count, registry.Calls);
                }
            }
            var secret = "private-diagnostic-sentinel";
            var creation = new ConfigurationAccessReply { Credential = secret };
            var delivery = new DeliverConfigurationReply { Json = Google.Protobuf.ByteString.CopyFromUtf8(secret) };
            Assert.False(creation.ToString().Contains(secret, StringComparison.Ordinal)); Assert.False(delivery.ToString().Contains(secret, StringComparison.Ordinal));
            var failedOutput = new BrokenCredentialOutput(); var failedError = new StringWriter();
            var failingCli = new CliApplication(new FixedEndpoint(host.Urls.Single()), new GrpcRegistryClientFactory(), failedOutput, failedError);
            var failed = await failingCli.RunAsync(["configuration", "access", "create", "named-application", "--component", "api", "--document", "settings", "--generation", "3", "--token-stdout", "--config", config]);
            Assert.Equal(3, failed); Assert.Contains("may be active", failedError.ToString()); Assert.Contains(registry.Id, failedError.ToString());
            Assert.False(failedError.ToString().Contains(registry.Credential, StringComparison.Ordinal));
            Assert.False(failedError.ToString().Contains("private-output-diagnostic", StringComparison.Ordinal));
        }
        finally { foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file); Directory.Delete(folder); await host.StopAsync(); }

        async Task<(int Code, string Stdout, string Stderr)> Run(bool native, string format, string[] args)
        {
            var all = new[] { "configuration", "access" }.Concat(args).Concat(["--config", config, "--output", format]).ToArray();
            if (!native)
            {
                var stdout = new StringWriter(); var stderr = new StringWriter();
                var cli = new CliApplication(new FixedEndpoint(host.Urls.Single()), new GrpcRegistryClientFactory(), stdout, stderr);
                return (await cli.RunAsync(all), stdout.ToString(), stderr.ToString());
            }
            var start = new ProcessStartInfo(binary!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in all) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
            return (process.ExitCode, await output, await error);
        }
    }
}
