using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
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
    private const string ImportEnvironment = "019d03cf-a000-7000-8000-000000000001";
    private const string ImportApplication = "019d03cf-a000-7000-8000-000000000002";
    private static readonly byte[] ImportPassword = Encoding.UTF8.GetBytes("\ufeffprotected-input\r\n");

    private sealed class ImportRegistry : ManifestOperator.ManifestOperatorBase
    {
        internal int Creates;
        internal int Environments;
        internal EnvironmentStatus LookupStatus = EnvironmentStatus.Found;
        internal string? LastRequest;
        internal ResourceImportRequestReply? LastReply;
        public override Task<EnvironmentReply> GetEnvironment(EnvironmentNameRequest request, ServerCallContext context)
        {
            Assert.Equal("pilot", request.Name); Environments++;
            if (LookupStatus != EnvironmentStatus.Found) return Task.FromResult(new EnvironmentReply { Status = LookupStatus });
            return Task.FromResult(new EnvironmentReply { Status = EnvironmentStatus.Found, Environment = new() { RecordId = ImportEnvironment, Name = "pilot" } });
        }
        public override Task<ResourceImportRequestReply> CreateResourceImportChangeSet(CreateResourceImportChangeSetRequest request, ServerCallContext context)
        {
            Creates++; LastRequest = request.RequestId;
            Assert.True(Guid.TryParse(request.RequestId, out var id) && id != Guid.Empty);
            Assert.Equal(ImportEnvironment, request.EnvironmentRecordId); Assert.Equal(ImportApplication, request.Reference.ApplicationRecordId);
            Assert.Equal("haby.postgresql.import-names/v1", request.InputFormat);
            Assert.True(request.CredentialMaterial.Span.SequenceEqual(ImportPassword), "Password bytes changed in transport.");
            using var input = JsonDocument.Parse(request.InputJson.Memory);
            Assert.Equal("legacy_db", input.RootElement.GetProperty("databaseName").GetString());
            LastReply = new()
            {
                RequestId = request.RequestId,
                Status = ResourceImportRequestStatus.Created,
                Request = new()
                {
                    RequestId = request.RequestId,
                    EnvironmentRecordId = ImportEnvironment,
                    Reference = request.Reference.Clone(),
                    CredentialResourceId = "login",
                    CreatedAtUtc = "2026-11-22T00:00:00.0000000+00:00",
                    CreatedBy = "original-actor",
                    ChangeSetId = "019d03cf-a000-7000-8000-000000000003",
                    CompletedAtUtc = "2026-11-22T00:00:01.0000000+00:00",
                    CompletedBy = "linking-actor",
                    Correction = request.Correction?.Clone()
                }
            };
            if (request.Reference.RevisionNumber == 2) throw new RpcException(new Status(StatusCode.Unavailable, "unsafe-detail"));
            return Task.FromResult(LastReply);
        }
        public override Task<ResourceImportRequestReply> GetResourceImportRequest(ResourceImportRequestKey request, ServerCallContext context)
        { Assert.Equal(LastRequest, request.RequestId); return Task.FromResult(LastReply!.Clone()); }
    }

    [Fact]
    public async Task Resource_import_environment_resolution_refuses_unknown_status_before_password_or_create()
    {
        var registry = new ImportRegistry { LookupStatus = EnvironmentStatus.Unspecified };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(); builder.Services.AddSingleton(registry);
        await using var host = builder.Build(); host.MapGrpcService<ImportRegistry>(); await host.StartAsync();
        using var output = new StringWriter(); using var error = new StringWriter();
        var cli = new CliApplication(new FixedEndpoint(host.Urls.Single()), new GrpcRegistryClientFactory(), output, error,
            secretTerminal: new SecretInputTests.Terminal([]));
        try
        {
            Assert.Equal(CliApplication.UsageError, await cli.RunAsync(["changeset", "import", "create", ImportApplication, "1", "--input", "unused.json",
                "--environment", "pilot", "--password-stdin", "-o", "json"]));
            Assert.Equal(0, registry.Creates); Assert.Equal(1, registry.Environments);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal("notSubmitted", json.RootElement.GetProperty("outcome").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Resource_import_commands_preserve_bytes_uuid_recovery_and_explicit_steps_in_generated_and_native_transport()
    {
        var registry = new ImportRegistry();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(); builder.Services.AddSingleton(registry);
        await using var host = builder.Build(); host.MapGrpcService<ImportRegistry>(); await host.StartAsync();
        var endpoint = host.Urls.Single(); var binary = AotBinary();
        var path = Path.Combine(Path.GetTempPath(), "haby-import-names-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {"format":"haby.postgresql.import-names/v1","input":{"databaseName":"legacy_db","loginName":"legacy_login","schema":"public"}}
                """);
            foreach (var native in new[] { false, true })
            {
                if (native && binary is null) continue;
                foreach (var json in new[] { false, true })
                {
                    foreach (var lost in new[] { false, true })
                    {
                        var request = Guid.NewGuid().ToString();
                        var args = new List<string> { "changeset", "import", "create", ImportApplication, lost ? "2" : "1", "--input", path,
                            "--environment", "pilot", "--password-stdin", "--request-id", request };
                        if (json) args.AddRange(["-o", "json"]);
                        var before = registry.Creates; var lookups = registry.Environments;
                        var result = await Invoke(args, ImportPassword, native);
                        Assert.Equal(lost ? CliApplication.TransportError : CliApplication.Success, result.Exit);
                        Assert.Equal(before + 1, registry.Creates); Assert.Equal(lookups + 1, registry.Environments);
                        Assert.Equal(request, registry.LastRequest); Check(result, request, json, lost);
                        var get = new List<string> { "changeset", "import", "get", request };
                        if (json) get.AddRange(["-o", "json"]);
                        var found = await Invoke(get, [], native);
                        Assert.Equal(CliApplication.Success, found.Exit); Check(found, request, json, false);
                        Assert.Equal(before + 1, registry.Creates); Assert.Equal(lookups + 1, registry.Environments);
                    }
                }
                // Generation of an optional UUID and an exact Environment selector need no name lookup.
                var exact = await Invoke(["changeset", "import", "create", ImportApplication, "1", "--input", path,
                    "--environment-record-id", ImportEnvironment, "--password-stdin", "-o", "json"], ImportPassword, native);
                Assert.Equal(0, exact.Exit);
                using var generated = JsonDocument.Parse(exact.Output);
                Assert.Equal(registry.LastRequest, generated.RootElement.GetProperty("data").GetProperty("requestId").GetString());
                var count = registry.Creates;
                foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 0 }, new byte[] { 0xc3, 0x28 }, new byte[65537] })
                {
                    var invalid = await Invoke(["changeset", "import", "create", ImportApplication, "1", "--input", path,
                        "--environment-record-id", ImportEnvironment, "--password-stdin", "-o", "json"], bytes, native);
                    Assert.Equal(CliApplication.UsageError, invalid.Exit); Assert.Equal(count, registry.Creates);
                    using var data = JsonDocument.Parse(invalid.Output);
                    Assert.Equal("notSubmitted", data.RootElement.GetProperty("outcome").GetString());
                    Assert.True(Guid.TryParse(data.RootElement.GetProperty("data").GetProperty("requestId").GetString(), out _));
                }
            }

            async Task<(int Exit, string Output, string Error)> Invoke(List<string> arguments, byte[] bytes, bool native)
            {
                if (!native)
                {
                    using var output = new StringWriter(); using var error = new StringWriter();
                    var cli = new CliApplication(new FixedEndpoint(endpoint), new GrpcRegistryClientFactory(), output, error, secretTerminal: new SecretInputTests.Terminal(bytes));
                    var exit = await cli.RunAsync([.. arguments]); return (exit, output.ToString(), error.ToString());
                }
                var start = new ProcessStartInfo(binary!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                start.Environment["HABY_OPERATOR_URL"] = endpoint; start.Environment.Remove("HABYCTL_CONFIG");
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.StandardInput.BaseStream.WriteAsync(bytes); process.StandardInput.Close();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(deadline.Token);
                return (process.ExitCode, await stdout, await stderr);
            }
            static void Check((int Exit, string Output, string Error) result, string id, bool json, bool lost)
            {
                Assert.DoesNotContain("protected-input", result.Output + result.Error); Assert.DoesNotContain("unsafe-detail", result.Output + result.Error);
                Assert.DoesNotContain(Convert.ToBase64String(ImportPassword), result.Output + result.Error);
                Assert.Contains(id, result.Output);
                if (json)
                {
                    using var document = JsonDocument.Parse(result.Output); var root = document.RootElement;
                    Assert.Equal(lost ? "uncertain" : "created", root.GetProperty("outcome").GetString());
                    Assert.Equal(id, root.GetProperty("data").GetProperty("requestId").GetString());
                    if (lost) Assert.Contains(id, root.GetProperty("data").GetProperty("recovery").GetProperty("lookupCommand").GetString());
                    Assert.DoesNotContain("credentialMaterial", result.Output); Assert.DoesNotContain("secretVersion", result.Output);
                }
                else if (lost) Assert.Contains("habyctl changeset import get " + id, result.Output);
                else Assert.Contains("Plan, Commit and Execute remain explicit", result.Output);
            }
        }
        finally { File.Delete(path); await host.StopAsync(); }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing-metadata")]
    [InlineData("partial-link")]
    [InlineData("wrong-uuid")]
    public void Resource_import_presentation_refuses_unknown_or_incomplete_success(string fault)
    {
        var id = Guid.NewGuid().ToString();
        var reply = new ResourceImportRequestReply { RequestId = id, Status = ResourceImportRequestStatus.Created };
        if (fault == "unknown") reply.Status = (ResourceImportRequestStatus)999;
        if (fault is "partial-link" or "wrong-uuid") reply.Request = new()
        {
            RequestId = fault == "wrong-uuid" ? Guid.NewGuid().ToString() : id,
            EnvironmentRecordId = ImportEnvironment,
            Reference = new() { ApplicationRecordId = ImportApplication, RevisionNumber = 1 },
            CredentialResourceId = "login",
            CreatedBy = "operator",
            CreatedAtUtc = "2026-11-22T00:00:00.0000000+00:00",
            ChangeSetId = "019d03cf-a000-7000-8000-000000000003"
        };
        using var output = new StringWriter();
        Assert.Throws<InvalidDataException>(() => new CliJsonPresenter(output).PrintResourceImport("changeset import create", reply));
        Assert.Empty(output.ToString());
    }
}
