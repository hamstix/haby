using System.Diagnostics;
using System.Net;
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
    private sealed class ProviderRegistry : ManifestOperator.ManifestOperatorBase
    {
        public override Task<ProviderReply> ApplyProvider(ApplyProviderRequest request, ServerCallContext context)
        {
            Assert.Equal("{}", request.Declaration.ToStringUtf8());
            Assert.Contains("[redacted]", request.ToString());
            Assert.Equal(request.ConfirmLogicalIdentity, request.HasExpectedPreviousRevision);
            if (request.HasEnvironment) return Task.FromResult(new ProviderReply { Status = ProviderStatus.EnvironmentMismatch });
            return Task.FromResult(new ProviderReply { Status = ProviderStatus.ExistingEquivalent, Provider = Metadata() });
        }
        public override Task<ProviderReply> GetProvider(GetProviderRequest request, ServerCallContext context)
        {
            Assert.Equal("pilot", request.Environment);
            if (request.Name == "missing") return Task.FromResult(new ProviderReply { Status = ProviderStatus.NotFound });
            if (request.Name == "bad-status") return Task.FromResult(new ProviderReply { Status = ProviderStatus.Listed });
            var metadata = Metadata();
            if (request.Name == "bad-id") metadata.InstanceId = "invalid";
            return Task.FromResult(new ProviderReply { Status = ProviderStatus.Found, Provider = metadata });
        }
        public override Task<ListProvidersReply> ListProviders(ListProvidersRequest request, ServerCallContext context)
        {
            Assert.Equal("pilot", request.Environment);
            return Task.FromResult(new ListProvidersReply { Status = ProviderStatus.Listed, Items = { Metadata() } });
        }
        public override Task<ProviderAccessReply> CheckProvider(CheckProviderRequest request, ServerCallContext context)
        {
            Assert.Equal("pilot", request.Environment);
            Assert.Equal(1, request.Revision);
            return Task.FromResult(request.Name switch
            {
                "bad-status" => new ProviderAccessReply { Status = ProviderAccessStatus.Found },
                "bad-preflight" => new ProviderAccessReply { Status = ProviderAccessStatus.Ready },
                "offline" => new ProviderAccessReply { Status = ProviderAccessStatus.PreflightFailed, Preflight = ProviderPreflightStatus.ServerUnavailable },
                _ => new ProviderAccessReply { Status = ProviderAccessStatus.Ready, Preflight = ProviderPreflightStatus.Ready }
            });
        }
        public override Task<ProviderAccessReply> GetProviderAlias(GetProviderAliasRequest request, ServerCallContext context)
        {
            Assert.Equal("pilot", request.Environment);
            return Task.FromResult(new ProviderAccessReply { Status = ProviderAccessStatus.Found, Binding = Binding() });
        }
        public override Task<ProviderAccessReply> SetProviderAlias(SetProviderAliasRequest request, ServerCallContext context)
        {
            Assert.Equal("pilot", request.Environment);
            Assert.NotNull(request.Target);
            Assert.Equal(1, request.Target.Revision);
            Assert.NotEqual(SetProviderAliasRequest.ExpectationOneofCase.None, request.ExpectationCase);
            return Task.FromResult(new ProviderAccessReply { Status = ProviderAccessStatus.Changed, Binding = Binding() });
        }
        private static ProviderAliasMetadata Binding() => new()
        { Alias = "postgresql.primary", InstanceId = "019d03cf-a000-7000-8000-000000000002", Revision = 1 };
        private static ProviderMetadata Metadata() => new()
        {
            EnvironmentRecordId = "019d03cf-a000-7000-8000-000000000001",
            InstanceId = "019d03cf-a000-7000-8000-000000000002",
            Name = "pg-main",
            ProviderKind = "postgresql/v1",
            Revision = new ProviderRevisionMetadata
            { Revision = 1, ConfigurationContract = "haby.postgresql.connection/v1", ImplementationId = "haby.postgresql.database/v1alpha1" }
        };
    }

    [Fact]
    public async Task Provider_commands_preserve_generated_transport_AOT_and_safe_outcomes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var host = builder.Build();
        host.MapGrpcService<ProviderRegistry>();
        await host.StartAsync();
        var file = Path.Combine(Path.GetTempPath(), "haby-provider-" + Guid.NewGuid().ToString("N") + ".json");
        var config = file + ".yaml";
        try
        {
            await File.WriteAllTextAsync(file, "{}");
            await File.WriteAllTextAsync(config, $"formatVersion: 1\ncurrentContext: wrong\ninstances:\n  local:\n    endpoint: {host.Urls.Single()}\ncontexts:\n  wrong:\n    instance: local\n    environmentId: other\n");
            var cases = new (string[] Args, int Exit)[]
            {
                (["provider", "apply", "--file", file], 0),
                (["provider", "apply", "--file", file, "--confirm-logical-identity", "--expected-previous-revision", "1"], 0),
                (["provider", "apply", "--file", file, "--environment", "other"], 1),
                (["provider", "apply", "--file", file, "--confirm-logical-identity"], CliApplication.UsageError),
                (["provider", "apply", "--file", file, "--expected-previous-revision", "1"], CliApplication.UsageError),
                (["provider", "get", "pg-main", "--environment", "pilot"], 0),
                (["provider", "revision", "get", "pg-main", "--revision", "1", "--environment", "pilot"], 0),
                (["provider", "list", "--environment", "pilot"], 0),
                (["provider", "check", "pg-main", "--revision", "1", "--environment", "pilot"], 0),
                (["provider", "check", "offline", "--revision", "1", "--environment", "pilot"], 1),
                (["provider", "check", "bad-status", "--revision", "1", "--environment", "pilot"], CliApplication.UsageError),
                (["provider", "check", "bad-preflight", "--revision", "1", "--environment", "pilot"], CliApplication.UsageError),
                (["provider", "alias", "get", "postgresql.primary", "--environment", "pilot"], 0),
                (["provider", "alias", "set", "postgresql.primary", "--provider", "pg-main", "--revision", "1", "--expect-absent", "--environment", "pilot"], 0),
                (["provider", "alias", "set", "postgresql.primary", "--provider", "pg-main", "--revision", "1", "--expected-provider", "pg-main", "--expected-revision", "1", "--environment", "pilot"], 0),
                (["provider", "alias", "set", "postgresql.primary", "--provider-instance-id", "019d03cf-a000-7000-8000-000000000002", "--revision", "1", "--expected-instance-id", "019d03cf-a000-7000-8000-000000000002", "--expected-revision", "1", "--environment", "pilot"], 0),
                (["provider", "alias", "set", "postgresql.primary", "--provider", "pg-main", "--revision", "1", "--environment", "pilot"], CliApplication.UsageError),
                (["provider", "alias", "set", "postgresql.primary", "--provider", "pg-main", "--revision", "1", "--expect-absent", "--expected-provider", "pg-main", "--expected-revision", "1", "--environment", "pilot"], CliApplication.UsageError),
                (["provider", "get", "missing", "--environment", "pilot"], 1),
                (["provider", "get", "bad-status", "--environment", "pilot"], CliApplication.UsageError),
                (["provider", "get", "bad-id", "--environment", "pilot"], CliApplication.UsageError)
            };
            var binary = AotBinary();
            foreach (var format in new[] { "text", "json" })
                foreach (var test in cases)
                {
                    var arguments = test.Args.Concat(["--config", config, "-o", format]).ToArray();
                    using var output = new StringWriter();
                    using var error = new StringWriter();
                    var cli = new CliApplication(new FixedEndpoint(host.Urls.Single()), new GrpcRegistryClientFactory(), output, error);
                    Check(await cli.RunAsync(arguments), output.ToString(), error.ToString());
                    if (binary is not null)
                    {
                        var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                        foreach (var argument in arguments) start.ArgumentList.Add(argument);
                        using var process = Process.Start(start)!;
                        var stdout = process.StandardOutput.ReadToEndAsync();
                        var stderr = process.StandardError.ReadToEndAsync();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try { await process.WaitForExitAsync(timeout.Token); }
                        catch { process.Kill(entireProcessTree: true); throw; }
                        Check(process.ExitCode, await stdout, await stderr);
                    }
                    void Check(int exit, string stdout, string stderr)
                    {
                        Assert.Equal(test.Exit, exit);
                        if (exit is 0 or 1)
                        {
                            Assert.Empty(stderr);
                            if (format == "json")
                            {
                                using var json = JsonDocument.Parse(stdout);
                                Assert.Equal(1, json.RootElement.GetProperty("formatVersion").GetInt32());
                            }
                        }
                        else Assert.NotEmpty(stderr);
                    }
                }
        }
        finally { File.Delete(file); File.Delete(config); await host.StopAsync(); }
    }
}
