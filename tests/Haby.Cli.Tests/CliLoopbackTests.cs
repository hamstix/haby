using System.Diagnostics;
using System.Net;
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
        try
        {
            await File.WriteAllTextAsync(path, "{}");
            using var output = new StringWriter();
            using var error = new StringWriter();
            var cli = new CliApplication(new FixedEndpoint(address), new GrpcRegistryClientFactory(), output, error);
            Assert.Equal(CliApplication.Rejected, await cli.RunAsync(["manifest", "validate", path]));
            Assert.Contains("InvalidManifest", output.ToString());
            Assert.Equal("", error.ToString());

            var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
            if (binary is not null)
            {
                Assert.True(File.Exists(binary), $"AOT binary is missing: {binary}");
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
                process.StartInfo.Environment["HABY_OPERATOR_URL"] = address;
                Assert.True(process.Start());
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(CliApplication.Rejected, process.ExitCode);
                Assert.Contains("InvalidManifest", await process.StandardOutput.ReadToEndAsync(timeout.Token));
                Assert.Equal("", await process.StandardError.ReadToEndAsync(timeout.Token));
            }
        }
        finally
        {
            File.Delete(path);
            await app.StopAsync();
        }
    }

    private sealed class FixedEndpoint(string address) : IEndpointSource
    {
        public string? GetAddress() => address;
        public string? GetSkipTlsValidation() => null;
    }
}
