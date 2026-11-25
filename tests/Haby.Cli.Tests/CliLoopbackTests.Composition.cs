using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Haby.Cli.Composition.Sample;
using Grpc.Core;
using Haby.Operator.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.Cli.Tests;

public sealed partial class CliLoopbackTests
{
    private const string CompositionToken = "haby_operator_v1.11111111111111111111111111111111." + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private sealed class CompositionRegistry : ManifestOperator.ManifestOperatorBase
    {
        public override Task<ValidateReply> Validate(EnvelopeRequest request, ServerCallContext context)
        {
            if (context.RequestHeaders.GetValue("authorization") != "Bearer " + CompositionToken)
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Do not expose server details"));
            Assert.Equal("{}", request.OriginalEnvelope.ToStringUtf8());
            return Task.FromResult(new ValidateReply { Valid = new ValidManifest { ApplicationId = "public-composition", ApplicationVersion = "1", Warnings = new() } });
        }
    }

    [Fact]
    public async Task Public_composition_external_executable_preserves_configuration_and_authenticated_native_calls()
    {
        var native = Environment.GetEnvironmentVariable("HABY_AOT_COMPOSITION_PATH");
        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1")
        { Assert.False(string.IsNullOrWhiteSpace(native)); Assert.True(File.Exists(native)); }
        var web = WebApplication.CreateBuilder();
        web.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        web.Services.AddGrpc(); await using var host = web.Build(); host.MapGrpcService<CompositionRegistry>(); await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var directory = Path.Combine(Path.GetTempPath(), "public-cli-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "config.yaml"); var tokenFile = Path.Combine(directory, "operator-token");
        var envelope = Path.Combine(directory, "application.json"); var destination = Path.Combine(directory, "updated.yaml");
        var document = new SampleDocument(1, "dev", address, "development", tokenFile, false, "preserve note", "downstream-secret-sentinel", new() { ["color"] = "blue" });
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(document, SampleJsonContext.Default.SampleDocument));
        await File.WriteAllTextAsync(tokenFile, CompositionToken); await File.WriteAllTextAsync(envelope, "{}");
        async Task<(int Exit, string Output, string Error)> Run(params string[] arguments)
        {
            using var process = new Process();
            process.StartInfo = new(native ?? "dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (native is null) process.StartInfo.ArgumentList.Add(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cli-composition.target")).Trim().TrimStart('\uFEFF'));
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.Environment["HABY_SAMPLE_CONFIG"] = config;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Assert.True(process.Start());
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var result = (process.ExitCode, await output, await error);
            Assert.DoesNotContain(CompositionToken, result.Item2 + result.Item3); Assert.DoesNotContain("downstream-secret-sentinel", result.Item2 + result.Item3);
            return result;
        }
        try
        {
            var admissionExits = new List<int>();
            var custom = await Run("sample"); Assert.Equal(0, custom.Exit); Assert.Equal("custom", custom.Output.Trim());
            var help = await Run("--config", "missing", "registry", "manifest", "--help"); Assert.Equal(0, help.Exit); Assert.Contains("registry-sample registry manifest", help.Output);
            var group = await Run("registry", "manifest", "--output", "json"); Assert.Equal(2, group.Exit); Assert.Contains("Commands:", group.Error); Assert.Equal("", group.Output);
            var view = await Run("config", "view", "--output", "json"); Assert.Equal(0, view.Exit); Assert.Contains("preserve note", view.Output); Assert.Contains("[redacted]", view.Output);
            var valid = await Run("--context", "dev", "registry", "manifest", "validate", envelope, "--output", "json");
            Assert.True(valid.Exit == 0, valid.Error); using (var json = JsonDocument.Parse(valid.Output)) Assert.Equal("valid", json.RootElement.GetProperty("outcome").GetString());
            var customRpc = await Run("sample-registry", "--output", "json"); Assert.Equal(0, customRpc.Exit); Assert.Contains("valid", customRpc.Output);
            var explicitBad = await Run("registry", "manifest", "validate", envelope, "--config", Path.Combine(directory, "missing")); Assert.Equal(2, explicitBad.Exit);
            admissionExits.Add((await Run("sample-registry", "--config", Path.Combine(directory, "missing"))).Exit);
            var mutate = await Run("config", "set-credential", "registry", "--token-file", tokenFile, "--normalize", "--new-config-file", destination);
            Assert.Equal(0, mutate.Exit); var updated = JsonSerializer.Deserialize(await File.ReadAllTextAsync(destination), SampleJsonContext.Default.SampleDocument)!;
            Assert.Equal(document, updated with { Values = document.Values }); Assert.Equal("blue", updated.Values["color"]);
            Assert.Equal(document.DownstreamSecret, updated.DownstreamSecret);
            var refuse = await Run("config", "set-credential", "unknown", "--token-file", tokenFile, "--normalize", "--new-config-file", destination + ".refused");
            Assert.Equal(2, refuse.Exit); Assert.False(File.Exists(destination + ".refused"));
            var originalText = await File.ReadAllTextAsync(config);
            var unknownText = originalText[..^1] + ",\"unsupportedSection\":{\"keep\":true}}";
            await File.WriteAllTextAsync(config, unknownText);
            var unknown = await Run("config", "set-credential", "registry", "--token-file", tokenFile, "--normalize", "--new-config-file", destination + ".unknown");
            Assert.Equal(2, unknown.Exit); Assert.False(File.Exists(destination + ".unknown"));
            Assert.Equal(unknownText, await File.ReadAllTextAsync(config));
            await File.WriteAllTextAsync(config, originalText);
            await File.WriteAllTextAsync(tokenFile, "invalid-token-sentinel");
            var badToken = await Run("registry", "manifest", "validate", envelope); Assert.Equal(2, badToken.Exit); Assert.DoesNotContain("invalid-token-sentinel", badToken.Error);
            var customBadToken = await Run("sample-registry"); admissionExits.Add(customBadToken.Exit); Assert.DoesNotContain("invalid-token-sentinel", customBadToken.Error);
            var offline = await Run("config", "view"); Assert.Equal(0, offline.Exit);
            await File.WriteAllTextAsync(tokenFile, "haby_operator_v1.22222222222222222222222222222222." + new string('a', 64));
            var unauthorized = await Run("registry", "manifest", "validate", envelope); Assert.Equal(3, unauthorized.Exit); Assert.Contains("Unauthenticated", unauthorized.Error); Assert.DoesNotContain("server details", unauthorized.Error);
            var customUnauthorized = await Run("sample-registry"); Assert.Equal(3, customUnauthorized.Exit);
            Assert.Contains("Unauthenticated", customUnauthorized.Error); Assert.DoesNotContain("server details", customUnauthorized.Error);
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(document with { Endpoint = "http://remote.example", TokenFile = Path.Combine(directory, "missing-token") }, SampleJsonContext.Default.SampleDocument));
            var transport = await Run("registry", "manifest", "validate", envelope); Assert.Equal(2, transport.Exit); Assert.Contains("loopback", transport.Error);
            admissionExits.Add((await Run("sample-registry")).Exit);
            Assert.Equal(new[] { explicitBad.Exit, badToken.Exit, transport.Exit }, admissionExits);
        }
        finally
        {
            foreach (var path in new[] { config, tokenFile, envelope, destination }) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory); await host.StopAsync();
        }
    }
}
