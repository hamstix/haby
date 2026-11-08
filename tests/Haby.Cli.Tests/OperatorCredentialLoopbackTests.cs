using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
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
    private static string OperatorToken() => "haby_operator_v1." + Guid.CreateVersion7().ToString("N") + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private sealed class AuthenticatedRegistry(string credential) : ManifestOperator.ManifestOperatorBase
    {
        public int Calls;
        private void Check(ServerCallContext context)
        {
            Assert.Equal("Bearer " + credential, Assert.Single(context.RequestHeaders, x => x.Key == "authorization").Value);
            Calls++;
        }
        public override Task<ListEnvironmentsReply> ListEnvironments(ListEnvironmentsRequest request, ServerCallContext context)
        { Check(context); return Task.FromResult(new ListEnvironmentsReply { Status = EnvironmentStatus.Listed }); }
        public override Task<ConfigurationDocumentReply> GetConfiguration(ConfigurationDocumentRequest request, ServerCallContext context)
        { Check(context); return new DocumentRegistry().GetConfiguration(request, context); }
    }
    private sealed class OperatorInput(string value) : ISecretTerminal
    {
        public bool IsInputRedirected => true;
        public Stream OpenStandardInput() => new MemoryStream(Encoding.UTF8.GetBytes(value));
        public ConsoleKeyInfo ReadHiddenKey() => throw new InvalidOperationException();
    }

    [Fact]
    public async Task Operator_credentials_selected_per_instance_on_both_channels_redacted_and_native()
    {
        var binary = AotBinary();
        var firstToken = OperatorToken(); var secondToken = OperatorToken();
        var firstRegistry = new AuthenticatedRegistry(firstToken); var secondRegistry = new AuthenticatedRegistry(secondToken);
        static WebApplication Build(AuthenticatedRegistry registry)
        {
            var b = WebApplication.CreateBuilder(); b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, x => x.Protocols = HttpProtocols.Http2));
            b.Services.AddGrpc(); b.Services.AddSingleton(registry); var h = b.Build(); h.MapGrpcService<AuthenticatedRegistry>(); return h;
        }
        await using var first = Build(firstRegistry); await using var second = Build(secondRegistry); await first.StartAsync(); await second.StartAsync();
        var folder = Path.Combine(Path.GetTempPath(), "registry-credential-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var config = Path.Combine(folder, "config.yaml"); var file = Path.Combine(folder, "second.token");
        await File.WriteAllTextAsync(file, secondToken + "\r\n");
        var yaml = $"formatVersion: 1\ncurrentContext: first\ninstances:\n  first:\n    endpoint: {first.Urls.Single()}\n    token: {firstToken}\n  second:\n    endpoint: {second.Urls.Single()}\n    tokenFile: '{file.Replace("'", "''", StringComparison.Ordinal)}'\ncontexts:\n  first:\n    instance: first\n    environmentId: override\n  second:\n    instance: second\n    environmentId: override\n";
        await File.WriteAllTextAsync(config, yaml);
        try
        {
            foreach (var native in binary is null ? new[] { false } : new[] { false, true })
            {
                async Task<(int Exit, string Out, string Error)> Run(params string[] args)
                {
                    var all = args.Concat(["--config", config]).ToArray();
                    if (!native)
                    {
                        using var stdout = new StringWriter(); using var stderr = new StringWriter();
                        var exit = await new CliApplication(new EnvironmentEndpointSource(), new GrpcRegistryClientFactory(), stdout, stderr).RunAsync(all);
                        return (exit, stdout.ToString(), stderr.ToString());
                    }
                    var start = new ProcessStartInfo(binary!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var arg in all) start.ArgumentList.Add(arg);
                    using var process = Process.Start(start)!; var o = process.StandardOutput.ReadToEndAsync(); var e = process.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                    return (process.ExitCode, await o, await e);
                }
                foreach (var context in new[] { "first", "second" })
                {
                    var list = await Run("environment", "list", "--context", context); Assert.Equal(0, list.Exit); Assert.Empty(list.Error);
                    var large = await Run("configuration", "get", "named-application", "--component", "api", "--document", "settings", "--generation", "3", "--context", context);
                    Assert.Equal(0, large.Exit); Assert.Empty(large.Error);
                }
                foreach (var format in new[] { "text", "json" })
                {
                    // Inspection must not read a selected credential file, including an invalid UTF-8 one.
                    await File.WriteAllBytesAsync(file, [0xff]);
                    var view = await Run("config", "view", "-o", format); Assert.Equal(0, view.Exit);
                    Assert.Contains("[redacted]", view.Out); Assert.DoesNotContain(firstToken, view.Out + view.Error); Assert.DoesNotContain(secondToken, view.Out + view.Error);
                    var calls = secondRegistry.Calls;
                    Assert.Equal(2, (await Run("environment", "list", "--context", "second")).Exit); Assert.Equal(calls, secondRegistry.Calls);
                    Assert.Equal(0, (await Run("environment", "list", "--context", "first")).Exit);
                    await File.WriteAllTextAsync(file, secondToken + "\n");
                }
                Assert.True(firstRegistry.Calls > 0); Assert.True(secondRegistry.Calls > 0);
                File.Delete(file);
                Assert.Equal(0, (await Run("config", "view")).Exit);
                Assert.Equal(0, (await Run("--help")).Exit); Assert.Equal(0, (await Run("--version")).Exit);
                Assert.Equal(2, (await Run("environment", "list", "--context", "second")).Exit);
                await File.WriteAllTextAsync(file, secondToken + "\n");
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Operator_credential_file_accepts_only_no_newline_or_one_LF_or_CRLF(string suffix)
    {
        var value = OperatorToken(); using var stream = new MemoryStream(Encoding.UTF8.GetBytes(value + suffix));
        Assert.Equal(value, await CliOperatorCredential.ReadAsync(stream, default));
    }
    [Theory]
    [InlineData("\r")]
    [InlineData("\n\n")]
    [InlineData(" ")]
    [InlineData("\uFEFF")]
    [InlineData("\0")]
    public async Task Operator_credential_file_rejects_whitespace_BOM_and_other_material(string suffix)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(OperatorToken() + suffix));
        await Assert.ThrowsAsync<InvalidDataException>(() => CliOperatorCredential.ReadAsync(stream, default));
    }
    [Fact]
    public async Task Operator_credential_input_bounds_purpose_conflicts_and_config_write_policy()
    {
        foreach (var bytes in new[] { new byte[513], new byte[] { 0xff }, Encoding.UTF8.GetBytes("haby_delivery_v1." + Guid.NewGuid().ToString("N") + "." + new string('a', 64)) })
        {
            using var stream = new MemoryStream(bytes); await Assert.ThrowsAsync<InvalidDataException>(() => CliOperatorCredential.ReadAsync(stream, default));
        }
        var token = OperatorToken(); var other = OperatorToken();
        Assert.Throws<InvalidDataException>(() => CliConfigurationService.Parse($"formatVersion: 1\ninstances: {{local: {{endpoint: https://registry.example, token: {token}, tokenFile: absent}}}}\ncontexts: {{}}"));
        Assert.NotNull(CliApplication.ParseEndpoint("https://registry.example")); Assert.Null(CliApplication.ParseEndpoint("http://registry.example")); Assert.NotNull(CliApplication.ParseEndpoint("http://registry.example", true));
        var folder = Path.Combine(Path.GetTempPath(), "config-write-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "original.yaml"); var destination = Path.Combine(folder, "normalized.yaml");
        var original = $"# retain this comment\nformatVersion: 1\ncurrentContext: chosen\ninstances:\n  selected:\n    endpoint: https://registry.example\n    token: {token}\n  other:\n    endpoint: http://isolated.example\n    allowInsecureHttp: true\n    token: {other}\ncontexts:\n  chosen:\n    instance: selected\n    environmentId: pilot\n";
        await File.WriteAllTextAsync(source, original);
        var replacement = OperatorToken();
        try
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            async Task<int> Run(params string[] args) => await new CliApplication(new EnvironmentEndpointSource(), new GrpcRegistryClientFactory(), output, error,
                secretTerminal: new OperatorInput(replacement + "\n")).RunAsync(args.Concat(["--config", source]).ToArray());
            Assert.Equal(2, await Run("config", "set-credential", "selected", "--stdin", "--new-config-file", destination));
            Assert.False(File.Exists(destination));
            Assert.Equal(0, await Run("config", "set-credential", "selected", "--stdin", "--new-config-file", destination, "--normalize"));
            Assert.Equal(original, await File.ReadAllTextAsync(source));
            var parsed = CliConfigurationService.Parse(await File.ReadAllTextAsync(destination));
            Assert.Equal(replacement, parsed.Instances["selected"].Token); Assert.Equal(other, parsed.Instances["other"].Token); Assert.True(parsed.Instances["other"].AllowInsecureHttp);
            Assert.Equal("chosen", parsed.CurrentContext); Assert.Equal("pilot", parsed.Contexts["chosen"].EnvironmentId);
            Assert.Equal(2, await Run("config", "set-credential", "selected", "--stdin", "--new-config-file", destination, "--normalize"));
            foreach (var secret in new[] { token, other, replacement }) Assert.DoesNotContain(secret, output.ToString() + error.ToString());
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(destination));
        }
        finally { Directory.Delete(folder, true); }
    }
    private sealed class OperatorCreationRegistry : ManifestOperator.ManifestOperatorBase
    {
        public readonly string Credential = OperatorToken();
        public int Calls;
        public override Task<OperatorAccessReply> CreateOperatorAccess(CreateOperatorAccessRequest request, ServerCallContext context)
        {
            Calls++;
            return Task.FromResult(new OperatorAccessReply
            {
                Credential = Credential,
                Grant = new()
                { GrantId = Guid.ParseExact(Credential.AsSpan(17, 32), "N").ToString(), Actor = "operator-grant:" + Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O") }
            });
        }
    }
    [Fact]
    public async Task Operator_creation_material_only_explicit_destinations_uncertain_transfer_safe_and_native()
    {
        var binary = AotBinary(); var registry = new OperatorCreationRegistry();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, x => x.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(); builder.Services.AddSingleton(registry);
        await using var host = builder.Build(); host.MapGrpcService<OperatorCreationRegistry>(); await host.StartAsync();
        var folder = Path.Combine(Path.GetTempPath(), "operator-output-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var config = Path.Combine(folder, "config.yaml"); var tokenFile = Path.Combine(folder, "operator.token");
        await File.WriteAllTextAsync(config, $"formatVersion: 1\ncurrentContext: selected\ninstances:\n  local:\n    endpoint: {host.Urls.Single()}\n    token: {OperatorToken()}\ncontexts:\n  selected:\n    instance: local\n    environmentId: pilot\n");
        try
        {
            async Task<(int Exit, string Out, string Error)> Run(bool native, string format, params string[] args)
            {
                var all = new[] { "operator", "access", "create" }.Concat(args).Concat(["--config", config, "--output", format]).ToArray();
                if (!native)
                {
                    using var o = new StringWriter(); using var e = new StringWriter();
                    var result = await new CliApplication(new EnvironmentEndpointSource(), new GrpcRegistryClientFactory(), o, e).RunAsync(all);
                    return (result, o.ToString(), e.ToString());
                }
                var start = new ProcessStartInfo(binary!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in all) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                return (process.ExitCode, await stdout, await stderr);
            }
            foreach (var native in binary is null ? new[] { false } : new[] { false, true })
                foreach (var format in new[] { "text", "json" })
                {
                    var before = registry.Calls;
                    Assert.Equal(2, (await Run(native, format)).Exit); Assert.Equal(before, registry.Calls);
                    var file = await Run(native, format, "--token-output-file", tokenFile); Assert.Equal(0, file.Exit);
                    Assert.DoesNotContain(registry.Credential, file.Out + file.Error); Assert.Empty(file.Error);
                    Assert.Equal(registry.Credential, (await File.ReadAllTextAsync(tokenFile)).TrimEnd());
                    before = registry.Calls; Assert.Equal(2, (await Run(native, format, "--token-output-file", tokenFile)).Exit); Assert.Equal(before, registry.Calls);
                    File.Delete(tokenFile);
                    var sensitive = await Run(native, format, "--token-stdout"); Assert.Equal(0, sensitive.Exit); Assert.DoesNotContain(registry.Credential, sensitive.Error);
                    if (format == "text") Assert.Equal(registry.Credential, sensitive.Out.TrimEnd());
                    else { using var json = JsonDocument.Parse(sensitive.Out); Assert.Equal(registry.Credential, json.RootElement.GetProperty("data").GetProperty("credential").GetString()); }
                }
            using var broken = new BrokenCredentialOutput(); using var errors = new StringWriter();
            var exit = await new CliApplication(new EnvironmentEndpointSource(), new GrpcRegistryClientFactory(), broken, errors)
                .RunAsync(["operator", "access", "create", "--config", config, "--token-stdout"]);
            Assert.Equal(3, exit); Assert.Contains("may be active", errors.ToString()); Assert.DoesNotContain(registry.Credential, errors.ToString());
            Assert.DoesNotContain("private-output-diagnostic", errors.ToString());
        }
        finally { Directory.Delete(folder, true); }
    }

}
