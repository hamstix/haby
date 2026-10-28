using System.Diagnostics;
using System.Net;
using System.Text.Json;
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

public sealed class CliLoopbackTests
{
    private sealed class TestRegistry : ManifestOperator.ManifestOperatorBase
    {
        public override Task<ValidateReply> Validate(EnvelopeRequest request, ServerCallContext context)
        {
            Assert.Equal("{}", request.OriginalEnvelope.ToStringUtf8());
            return Task.FromResult(new ValidateReply
            {
                InvalidManifest = new InvalidManifest { Report = new DiagnosticReport() }
            });
        }
    }

    private sealed class SecondRegistry : ManifestOperator.ManifestOperatorBase
    {
        public override Task<ValidateReply> Validate(EnvelopeRequest request, ServerCallContext context) =>
            Task.FromResult(new ValidateReply
            {
                Valid = new ValidManifest
                {
                    ApplicationId = "second-instance",
                    ApplicationVersion = "1",
                    Warnings = new DiagnosticReport()
                }
            });
    }

    [Fact]
    public async Task Context_override_selects_second_disposable_loopback_host_and_aot_binary()
    {
        static WebApplication Build<T>() where T : class
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
                listen => listen.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            var app = builder.Build();
            app.MapGrpcService<T>();
            return app;
        }
        await using var first = Build<TestRegistry>();
        await using var second = Build<SecondRegistry>();
        await first.StartAsync();
        await second.StartAsync();
        var firstAddress = first.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var secondAddress = second.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var folder = Path.Combine(Path.GetTempPath(), "haby-two-hosts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var config = Path.Combine(folder, "config.yaml");
        var envelope = Path.Combine(folder, "envelope.json");
        try
        {
            await File.WriteAllTextAsync(config, $"""
                formatVersion: 1
                currentContext: first
                instances:
                  first:
                    endpoint: {firstAddress}
                  second:
                    endpoint: {secondAddress}
                contexts:
                  first:
                    instance: first
                    environmentId: dev-one
                  second:
                    instance: second
                    environmentId: dev-two
                """);
            await File.WriteAllTextAsync(envelope, "{}");
            using var output = new StringWriter();
            using var error = new StringWriter();
            var cli = new CliApplication(new FixedEndpoint("http://localhost:1"),
                new GrpcRegistryClientFactory(), output, error);
            Assert.Equal(CliApplication.Rejected, await cli.RunAsync(["manifest", "validate", envelope, "--config", config]));
            Assert.Contains("InvalidManifest", output.ToString());
            output.GetStringBuilder().Clear();
            Assert.Equal(CliApplication.Success, await cli.RunAsync(["manifest", "validate", envelope,
                "--config", config, "--context", "second"]));
            Assert.Contains("Valid second-instance 1", output.ToString());
            Assert.Equal("", error.ToString());

            var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
            if (binary is not null)
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo(binary)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in new[] { "manifest", "validate", envelope, "--config", config, "--context", "second" })
                    process.StartInfo.ArgumentList.Add(argument);
                process.StartInfo.Environment["HABY_OPERATOR_URL"] = firstAddress;
                Assert.True(process.Start());
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(CliApplication.Success, process.ExitCode);
                Assert.Contains("Valid second-instance 1", await process.StandardOutput.ReadToEndAsync(timeout.Token));
                Assert.Equal("", await process.StandardError.ReadToEndAsync(timeout.Token));
            }
        }
        finally
        {
            Directory.Delete(folder, true);
            await second.StopAsync();
            await first.StopAsync();
        }
    }

    [Fact]
    public async Task Generated_client_reaches_disposable_loopback_registry_and_aot_binary_when_supplied()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var app = builder.Build();
        app.MapGrpcService<TestRegistry>();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        var path = Path.Combine(Path.GetTempPath(), "haby-cli-smoke-" + Guid.NewGuid().ToString("N"));
        var config = path + ".yaml";
        try
        {
            await File.WriteAllTextAsync(path, "{}");
            await File.WriteAllTextAsync(config, "formatVersion: 1\ninstances: {}\ncontexts: {}");
            using var output = new StringWriter();
            using var error = new StringWriter();
            var cli = new CliApplication(new FixedEndpoint(address), new GrpcRegistryClientFactory(), output, error);
            Assert.Equal(CliApplication.Rejected, await cli.RunAsync(["manifest", "validate", path, "--config", config]));
            Assert.Contains("InvalidManifest", output.ToString());
            Assert.Equal("", error.ToString());

            var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
            if (binary is not null)
            {
                Assert.True(File.Exists(binary), $"AOT binary is missing: {binary}");
                using (var configProcess = new Process())
                {
                    configProcess.StartInfo = new ProcessStartInfo(binary)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    foreach (var argument in new[] { "config", "view", "--config", config, "-o", "json" })
                        configProcess.StartInfo.ArgumentList.Add(argument);
                    Assert.True(configProcess.Start());
                    using var configTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await configProcess.WaitForExitAsync(configTimeout.Token);
                    Assert.Equal(0, configProcess.ExitCode);
                    using var configJson = JsonDocument.Parse(await configProcess.StandardOutput.ReadToEndAsync(configTimeout.Token));
                    Assert.Equal("config view", configJson.RootElement.GetProperty("command").GetString());
                    Assert.Equal("", await configProcess.StandardError.ReadToEndAsync(configTimeout.Token));
                }
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo(binary)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                process.StartInfo.ArgumentList.Add("manifest");
                process.StartInfo.ArgumentList.Add("validate");
                process.StartInfo.ArgumentList.Add(path);
                process.StartInfo.ArgumentList.Add("--config");
                process.StartInfo.ArgumentList.Add(config);
                process.StartInfo.ArgumentList.Add("-o");
                process.StartInfo.ArgumentList.Add("json");
                process.StartInfo.Environment["HABY_OPERATOR_URL"] = address;
                process.StartInfo.Environment.Remove("HABYCTL_CONFIG");
                Assert.True(process.Start());
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(CliApplication.Rejected, process.ExitCode);
                using var result = JsonDocument.Parse(await process.StandardOutput.ReadToEndAsync(timeout.Token));
                Assert.Equal("invalidManifest", result.RootElement.GetProperty("outcome").GetString());
                Assert.Equal("", await process.StandardError.ReadToEndAsync(timeout.Token));
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(config);
            await app.StopAsync();
        }
    }

    private sealed class FixedEndpoint(string address) : IEndpointSource
    {
        public string? GetAddress() => address;
        public string? GetSkipTlsValidation() => null;
    }
}
