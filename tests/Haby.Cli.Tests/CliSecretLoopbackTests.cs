using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Haby.Cli;
using Haby.Operator.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.Cli.Tests;

public sealed partial class CliLoopbackTests
{
    private sealed class SecretRegistry : ManifestOperator.ManifestOperatorBase
    {
        public override Task<SecretReply> CreateSecret(CreateSecretRequest request, ServerCallContext context)
        {
            Assert.Equal("pilot", request.Environment);
            Assert.True(Guid.TryParse(request.RequestId, out _));
            Assert.True(request.Material.Span.SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf, 65, 13, 10 }), "Transport must preserve exact input bytes.");
            if (request.Name == "uncertain") throw new RpcException(new Status(StatusCode.Unavailable, "unsafe-detail"));
            if (request.Name == "unknown") return Task.FromResult(new SecretReply());
            if (request.Name == "missing-secret") return Task.FromResult(new SecretReply { Status = SecretStatus.Created, RequestId = request.RequestId });
            if (request.Name == "wrong-status") return Task.FromResult(new SecretReply { Status = SecretStatus.Listed });
            if (request.Name is "missing-id" or "mismatched-id" or "missing-version" or "bad-identity")
            {
                var invalid = new SecretReply { Status = SecretStatus.Created, Secret = Metadata() };
                invalid.Secret.Name = request.Name;
                if (request.Name != "missing-id") invalid.RequestId = request.Name == "mismatched-id" ? Guid.NewGuid().ToString() : request.RequestId;
                if (request.Name == "missing-version") invalid.Secret.Versions.Clear();
                if (request.Name == "bad-identity") invalid.Secret.SecretId = "invalid";
                return Task.FromResult(invalid);
            }
            return Task.FromResult(new SecretReply { Status = SecretStatus.ExistingEquivalent, RequestId = request.RequestId, Secret = Metadata() });
        }
        public override Task<SecretReply> GetSecret(GetSecretRequest request, ServerCallContext context) =>
            Task.FromResult(new SecretReply { Status = SecretStatus.Found, Secret = Metadata() });
        public override Task<ListSecretsReply> ListSecrets(ListSecretsRequest request, ServerCallContext context)
        {
            var reply = new ListSecretsReply { Status = SecretStatus.Listed, NextCursor = "secret1:019d03cf-a000-7000-8000-000000000001:admin" };
            reply.Items.Add(Metadata());
            return Task.FromResult(reply);
        }
        private static SecretMetadata Metadata() => new()
        {
            Name = "admin",
            EnvironmentRecordId = "019d03cf-a000-7000-8000-000000000001",
            SecretId = "019d03cf-a000-7000-8000-000000000002",
            Purpose = "ProviderAdministrator",
            CreatedAtUtc = "2026-10-29T00:00:00.0000000+00:00",
            Versions = { new SecretVersionMetadata { VersionId = "019d03cf-a000-7000-8000-000000000003", Number = 1, CreatedAtUtc = "2026-10-29T00:00:00.0000000+00:00" } }
        };
    }

    [Fact]
    public async Task Secret_commands_use_generated_transport_and_published_native_binary_without_disclosure()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var host = builder.Build();
        host.MapGrpcService<SecretRegistry>();
        await host.StartAsync();
        var endpoint = host.Urls.Single();
        var requestId = Guid.NewGuid().ToString();
        var binary = AotBinary();
        var bytes = new byte[] { 0xef, 0xbb, 0xbf, 65, 13, 10 };
        foreach (var format in new[] { "text", "json" })
            foreach (var verb in new[] { "create", "get", "list", "uncertain" })
            {
                var arguments = new List<string> { "secret", verb == "uncertain" ? "create" : verb };
                if (verb != "list") arguments.Add(verb == "uncertain" ? "uncertain" : "admin");
                arguments.AddRange(["--environment", "pilot", "-o", format]);
                if (verb is "create" or "uncertain") arguments.AddRange(["--stdin", "--request-id", requestId]);
                using var output = new StringWriter();
                using var error = new StringWriter();
                var cli = new CliApplication(new FixedEndpoint(endpoint), new GrpcRegistryClientFactory(), output, error,
                    secretTerminal: new SecretInputTests.Terminal(bytes));
                var exit = await cli.RunAsync(arguments.ToArray());
                Check(exit, output.ToString(), error.ToString());
                if (binary is not null)
                {
                    var start = new ProcessStartInfo(binary)
                    { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var argument in arguments) start.ArgumentList.Add(argument);
                    start.Environment["HABY_OPERATOR_URL"] = endpoint;
                    using var process = Process.Start(start)!;
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    await process.StandardInput.BaseStream.WriteAsync(bytes);
                    process.StandardInput.Close();
                    await process.WaitForExitAsync();
                    Check(process.ExitCode, await stdout, await stderr);
                }

                void Check(int actualExit, string stdout, string stderr)
                {
                    Assert.Equal(verb == "uncertain" ? 3 : 0, actualExit);
                    Assert.DoesNotContain("unsafe-detail", stdout + stderr);
                    Assert.True(!(stdout + stderr).Contains(Convert.ToBase64String(bytes), StringComparison.Ordinal), "Command output must redact protected input.");
                    if (verb == "uncertain")
                    {
                        Assert.Empty(stdout);
                        Assert.Contains(requestId, stderr);
                        Assert.Contains("same Environment, name, exact input", stderr);
                    }
                    else if (format == "json")
                    {
                        using var json = JsonDocument.Parse(stdout);
                        var data = json.RootElement.GetProperty("data");
                        if (verb == "create") Assert.Equal(requestId, data.GetProperty("requestId").GetString());
                        Assert.DoesNotContain("material", stdout);
                    }
                    else
                    {
                        Assert.Contains("admin", stdout);
                        Assert.Contains("version 1", stdout);
                        Assert.DoesNotContain(requestId, stdout);
                    }
                }
            }
        using var invalidOutput = new StringWriter();
        using var invalidError = new StringWriter();
        var rejected = new CliApplication(new FixedEndpoint(endpoint), new GrpcRegistryClientFactory(), invalidOutput, invalidError,
            secretTerminal: new SecretInputTests.Terminal(bytes));
        Assert.Equal(2, await rejected.RunAsync(["secret", "create", "admin", "--environment", "pilot"]));
        Assert.Empty(invalidOutput.ToString());
        Assert.Equal(2, await rejected.RunAsync(["secret", "create", "admin", "literal", "--environment", "pilot"]));
        Assert.Equal(2, await rejected.RunAsync(["secret", "create", "admin", "--stdin", "--environment", "pilot", "--request-id", "invalid"]));
        using var generatedOutput = new StringWriter();
        using var generatedError = new StringWriter();
        var generated = new CliApplication(new FixedEndpoint(endpoint), new GrpcRegistryClientFactory(), generatedOutput, generatedError,
            secretTerminal: new SecretInputTests.Terminal(bytes));
        Assert.Equal(0, await generated.RunAsync(["secret", "create", "admin", "--environment", "pilot", "--stdin", "-o", "json"]));
        using (var json = JsonDocument.Parse(generatedOutput.ToString()))
            Assert.True(Guid.TryParse(json.RootElement.GetProperty("data").GetProperty("requestId").GetString(), out _));
        Assert.Empty(generatedError.ToString());
        foreach (var invalid in new[] { "unknown", "missing-secret", "wrong-status", "missing-id", "mismatched-id", "missing-version", "bad-identity" })
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var cli = new CliApplication(new FixedEndpoint(endpoint), new GrpcRegistryClientFactory(), stdout, stderr,
                secretTerminal: new SecretInputTests.Terminal(bytes));
            Assert.Equal(3, await cli.RunAsync(["secret", "create", invalid, "--environment", "pilot", "--stdin", "--request-id", requestId]));
            Assert.Empty(stdout.ToString());
            Assert.Contains(requestId, stderr.ToString());
            Assert.Contains("Retry secret create", stderr.ToString());
        }
        await host.StopAsync();
    }
}
