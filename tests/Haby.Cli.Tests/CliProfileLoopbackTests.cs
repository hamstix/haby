using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Grpc.Core;
using Google.Protobuf;
using Haby.Operator.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.Cli.Tests;

public sealed partial class CliLoopbackTests
{
    private sealed class ProfileRegistry : ManifestOperator.ManifestOperatorBase
    {
        public override Task<ProfileReply> ValidateProfile(ProfilePayloadRequest request, ServerCallContext context)
        {
            Assert.Equal("source-sensitive-marker", request.Payload.ToStringUtf8());
            return Task.FromResult(new ProfileReply { Status = ProfileStatus.Valid, Reference = Reference() });
        }
        public override Task<ProfileReply> ImportProfile(ProfilePayloadRequest request, ServerCallContext context)
        {
            Assert.Equal("source-sensitive-marker", request.Payload.ToStringUtf8());
            return Task.FromResult(new ProfileReply { Status = ProfileStatus.Created, Reference = Reference(), Metadata = Metadata() });
        }
        public override Task<ProfileReply> GetProfileRevision(ProfileRevisionRequest request, ServerCallContext context)
        {
            Assert.Equal("layout", request.ProfileId);
            // Deliberate request/reply mismatch, separate from malformed bodies.
            if (request.Revision == 10) return Task.FromResult(CliProfilePresentationTests.Found());
            return Task.FromResult(new ProfileReply
            {
                Status = request.Revision == 1 ? ProfileStatus.Found : ProfileStatus.NotFound,
                Reference = request,
                Metadata = request.Revision == 1 ? Metadata() : null,
                DeclarationJson = request.Revision == 1 ? ByteString.CopyFromUtf8(CliProfilePresentationTests.Declaration) : ByteString.Empty
            });
        }
        private static ProfileRevisionRequest Reference() => new() { ProfileId = "layout", Revision = 1 };
        private static ProfileMetadata Metadata() => new()
        {
            Reference = Reference(),
            TechnicalActorId = "trusted-host",
            ImportedAtUtc = "2026-11-05T15:00:00.0000000+00:00"
        };
    }

    private sealed class BoundaryProfileRegistry(string declaration) : ManifestOperator.ManifestOperatorBase
    {
        internal string Declaration { get; set; } = declaration;
        public override Task<ProfileReply> GetProfileRevision(ProfileRevisionRequest request, ServerCallContext context)
        {
            var reply = CliProfilePresentationTests.Found(Declaration);
            reply.Reference = request; reply.Metadata.Reference = request;
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task Profile_boundary_declarations_and_malformed_replies_use_published_binary()
    {
        var binary = AotBinary();
        var boundaries = Haby.Tests.ProfileDeclarationBoundarySamples.Sources().Select(body => (Body: body, Revision: 1))
            .Concat(new[] { false, true }.Select(secret => (Haby.Tests.ProfileDeclarationBoundarySamples.IntegerBoundary(1047552, secret), 1000000000)));
        foreach (var (declaration, revision) in boundaries)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc(); builder.Services.AddSingleton(new BoundaryProfileRegistry(declaration));
            await using var host = builder.Build(); host.MapGrpcService<BoundaryProfileRegistry>(); await host.StartAsync();
            foreach (var format in new[] { "json", "text" })
            {
                var arguments = new[] { "profile", "revision", "get", "layout", revision.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", format };
                using var output = new StringWriter(); using var error = new StringWriter();
                var cli = new CliApplication(new FixedEndpoint(host.Urls.Single()), new GrpcRegistryClientFactory(), output, error);
                Assert.Equal(0, await cli.RunAsync(arguments)); Assert.Equal("", error.ToString()); Check(output.ToString());
                if (binary is not null)
                {
                    var result = await RunProfileBinary(binary, host.Urls.Single(), arguments);
                    Assert.Equal(0, result.Exit); Assert.Equal("", result.Error); Check(result.Output);
                }
                void Check(string value)
                {
                    if (format == "text") { Assert.True(value.Length < 256); return; }
                    using var envelope = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 66 });
                    var actual = envelope.RootElement.GetProperty("data").GetProperty("declaration");
                    using var expected = JsonDocument.Parse(declaration);
                    Assert.Equal(declaration, actual.GetRawText());
                    Assert.True(JsonElement.DeepEquals(expected.RootElement, actual));
                }
            }
            await host.StopAsync();
        }
        // Select negative cases independently of the exact requested identity.
        // A valid control on the same service/command proves refusal is body-specific.
        foreach (var body in CliProfilePresentationTests.InvalidDeclarations().Select(x => (string)x[0]))
        {
            var registry = new BoundaryProfileRegistry(body);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc(); builder.Services.AddSingleton(registry);
            await using var host = builder.Build(); host.MapGrpcService<BoundaryProfileRegistry>(); await host.StartAsync();
            foreach (var accepted in new[] { false, true })
            {
                registry.Declaration = accepted ? CliProfilePresentationTests.Declaration : body;
                foreach (var format in new[] { "json", "text" })
                {
                    var arguments = new[] { "profile", "revision", "get", "layout", "1", "-o", format };
                    using var output = new StringWriter(); using var error = new StringWriter();
                    var cli = new CliApplication(new FixedEndpoint(host.Urls.Single()), new GrpcRegistryClientFactory(), output, error);
                    Assert.Equal(accepted ? 0 : 2, await cli.RunAsync(arguments));
                    if (accepted) { Assert.NotEmpty(output.ToString()); Assert.Equal("", error.ToString()); }
                    else { Assert.Equal("", output.ToString()); Assert.NotEmpty(error.ToString()); }
                    if (binary is not null)
                    {
                        var result = await RunProfileBinary(binary, host.Urls.Single(), arguments);
                        Assert.Equal(accepted ? 0 : 2, result.Exit);
                        if (accepted) { Assert.NotEmpty(result.Output); Assert.Equal("", result.Error); }
                        else
                        {
                            Assert.Equal("", result.Output); Assert.NotEmpty(result.Error);
                            Assert.DoesNotContain("ordinary", result.Error);
                        }
                    }
                }
            }
            await host.StopAsync();
        }
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            await using var host = builder.Build(); host.MapGrpcService<ProfileRegistry>(); await host.StartAsync();
            foreach (var format in new[] { "json", "text" })
            {
                var arguments = new[] { "profile", "revision", "get", "layout", "10", "-o", format };
                using var output = new StringWriter(); using var error = new StringWriter();
                var cli = new CliApplication(new FixedEndpoint(host.Urls.Single()), new GrpcRegistryClientFactory(), output, error);
                Assert.Equal(2, await cli.RunAsync(arguments)); Assert.Equal("", output.ToString());
                if (binary is not null)
                {
                    var result = await RunProfileBinary(binary, host.Urls.Single(), arguments);
                    Assert.Equal(2, result.Exit); Assert.Equal("", result.Output);
                }
            }
            await host.StopAsync();
        }
    }

    private static async Task<(int Exit, string Output, string Error)> RunProfileBinary(string binary, string address, string[] arguments)
    {
        var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        start.Environment["HABY_OPERATOR_URL"] = address;
        var config = Path.GetTempFileName();
        await File.WriteAllTextAsync(config, "formatVersion: 1\ninstances: {}\ncontexts: {}");
        start.ArgumentList.Add("--config"); start.ArgumentList.Add(config);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
            return (process.ExitCode, await stdout, await stderr);
        }
        finally { File.Delete(config); }
    }

    [Fact]
    public async Task Profile_commands_use_configured_generated_client_and_published_binary_with_safe_output()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var host = builder.Build(); host.MapGrpcService<ProfileRegistry>(); await host.StartAsync();
        var file = Path.GetTempFileName(); var config = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "source-sensitive-marker");
            await File.WriteAllTextAsync(config, $"""
                formatVersion: 1
                currentContext: absent
                instances:
                  local:
                    endpoint: {host.Urls.Single()}
                contexts:
                  absent:
                    instance: local
                    environmentId: never-created
                """);
            var binary = AotBinary();
            foreach (var (args, outcome, exit) in new (string[], string, int)[]
            {
                (["profile", "validate", file], "valid", 0),
                (["profile", "import", file], "created", 0),
                (["profile", "revision", "get", "layout", "1"], "found", 0),
                (["profile", "revision", "get", "layout", "2"], "notFound", 1)
            })
                foreach (var format in new[] { "text", "json" })
                {
                    var arguments = args.Concat(new[] { "--config", config, "-o", format }).ToArray();
                    using var output = new StringWriter(); using var error = new StringWriter();
                    var cli = new CliApplication(new FixedEndpoint("http://localhost:1"), new GrpcRegistryClientFactory(), output, error);
                    Assert.Equal(exit, await cli.RunAsync(arguments));
                    Assert.Equal("", error.ToString()); Verify(output.ToString());
                    if (binary is not null)
                    {
                        var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
                        foreach (var arg in arguments) start.ArgumentList.Add(arg);
                        using var process = Process.Start(start)!;
                        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
                        Assert.Equal(exit, process.ExitCode); Assert.Equal("", await stderr); Verify(await stdout);
                    }
                    void Verify(string text)
                    {
                        Assert.Contains(outcome, text); Assert.Contains("layout", text); Assert.DoesNotContain("source-sensitive-marker", text);
                        if (format == "json")
                        {
                            using var json = JsonDocument.Parse(text);
                            Assert.Equal(outcome, json.RootElement.GetProperty("outcome"u8).GetString());
                            Assert.Equal(1, json.RootElement.GetProperty("formatVersion"u8).GetInt32());
                            var data = json.RootElement.GetProperty("data");
                            Assert.Equal(outcome == "found", data.TryGetProperty("declaration", out var declaration));
                            if (outcome == "found")
                            {
                                Assert.Equal(JsonValueKind.Object, declaration.ValueKind);
                                Assert.Contains("1e999", declaration.GetRawText());
                            }
                        }
                        if (outcome is "created" or "found") Assert.Contains("trusted-host", text);
                    }
                }
        }
        finally { File.Delete(file); File.Delete(config); await host.StopAsync(); }
    }
}

public sealed class CliProfilePresentationTests
{
    internal const string Declaration = """{"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"layout","revision":1},"spec":{"documents":{"settings":{"fileName":"app.json","format":"json","template":{"n":1e999,"secretRef":{"name":"ordinary"}}}},"staticContributions":{},"bindingRules":{}}}""";
    internal static ProfileReply Found(string declaration = Declaration) => new()
    {
        Status = ProfileStatus.Found,
        Reference = new() { ProfileId = "layout", Revision = 1 },
        Metadata = new()
        {
            Reference = new() { ProfileId = "layout", Revision = 1 },
            TechnicalActorId = "trusted-host",
            ImportedAtUtc = "2026-11-05T15:00:00.0000000+00:00"
        },
        DeclarationJson = ByteString.CopyFromUtf8(declaration)
    };

    public static IEnumerable<object[]> InvalidDeclarations()
    {
        foreach (var body in new[]
        {
            "", "null", "[]", "{", Declaration[..^1],
            Declaration.Replace("ConfigurationProfile", "Application", StringComparison.Ordinal),
            Declaration.Replace("haby.dev/v1alpha1", "haby.dev/v999", StringComparison.Ordinal),
            Declaration.Replace("\"id\":\"layout\"", "\"id\":\"foreign\"", StringComparison.Ordinal),
            Declaration.Replace("\"revision\":1", "\"revision\":2", StringComparison.Ordinal),
            Declaration.Replace("\"bindingRules\":{}", "\"bindingRules\":null", StringComparison.Ordinal),
            Declaration.Replace("\"spec\":", "\"missingSpec\":", StringComparison.Ordinal),
            Declaration.Replace("\"n\":1e999", "\"n\":1,\"n\":2", StringComparison.Ordinal),
            Declaration.Replace("\"template\":", "\"unknown\":", StringComparison.Ordinal),
            Declaration.Replace("\"format\":\"json\"", "\"format\":\"yaml\"", StringComparison.Ordinal)
        }) yield return [body];
    }

    [Theory]
    [MemberData(nameof(InvalidDeclarations))]
    public void Invalid_declaration_fails_both_presenters_before_output(string body)
    {
        using var output = new StringWriter();
        var reply = Found(body);
        Assert.Throws<InvalidDataException>(() => new CliJsonPresenter(output).PrintProfile("profile revision get", reply));
        Assert.Throws<InvalidDataException>(() => new CliTextPresenter(output).PrintProfile("profile revision get", reply));
        Assert.Equal("", output.ToString());
    }

    [Theory]
    [InlineData("1e9", 1000000000)]
    [InlineData("1.000000000e9", 1000000000)]
    [InlineData("10E+0000000008", 1000000000)]
    [InlineData("2.147483647e9", 2147483647)]
    [InlineData("1000e-3", 1)]
    [InlineData("1e-1", 0)]
    [InlineData("0", 0)]
    [InlineData("-1", 0)]
    [InlineData("2147483648", 0)]
    [InlineData("0.99999999999999999999999", 0)]
    [InlineData("1e99999999999999999999999", 0)]
    [InlineData("1e-99999999999999999999999", 0)]
    [InlineData("\"1e9\"", 0)]
    [InlineData("null", 0)]
    public void Contract_integer_tokens_are_exact_positive_bounded_values(string token, int expected)
    {
        foreach (var secretVersion in new[] { false, true })
        {
            var body = secretVersion ? Declaration.Replace("\"staticContributions\":{}",
                "\"staticContributions\":{\"secret\":{\"documentRef\":\"settings\",\"path\":\"/x\",\"value\":{},\"secretProjections\":[{\"path\":\"/secret\",\"format\":\"string\",\"secretRef\":{\"name\":\"missing\",\"version\":" + token + "}}]}}", StringComparison.Ordinal)
                : Declaration.Replace("\"revision\":1", "\"revision\":" + token, StringComparison.Ordinal);
            var reply = Found(body);
            if (!secretVersion && expected > 0) { reply.Reference.Revision = expected; reply.Metadata.Reference.Revision = expected; }
            using var output = new StringWriter();
            if (expected > 0)
            {
                Assert.Equal(0, new CliJsonPresenter(output).PrintProfile("profile revision get", reply));
                Assert.Equal(0, new CliTextPresenter(output).PrintProfile("profile revision get", reply));
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => new CliJsonPresenter(output).PrintProfile("profile revision get", reply));
                Assert.Throws<InvalidDataException>(() => new CliTextPresenter(output).PrintProfile("profile revision get", reply));
                Assert.Equal("", output.ToString());
            }
        }
    }

    [Theory]
    [InlineData("profile validate", ProfileStatus.Valid)]
    [InlineData("profile import", ProfileStatus.Created)]
    [InlineData("profile import", ProfileStatus.ExistingEquivalent)]
    [InlineData("profile import", ProfileStatus.RevisionContentConflict)]
    [InlineData("profile revision get", ProfileStatus.NotFound)]
    [InlineData("profile validate", ProfileStatus.InvalidDeclaration)]
    public void Declaration_is_forbidden_on_other_outcomes(string command, ProfileStatus status)
    {
        using var output = new StringWriter();
        var reply = Found(); reply.Status = status;
        if (status is not (ProfileStatus.Created or ProfileStatus.ExistingEquivalent)) reply.Metadata = null;
        if (status == ProfileStatus.InvalidDeclaration)
        {
            reply.Reference = null; reply.ErrorCount = 1; reply.Diagnostics.Add(new ProfileDiagnostic { Code = "invalid" });
        }
        Assert.Throws<InvalidDataException>(() => new CliJsonPresenter(output).PrintProfile(command, reply));
        Assert.Throws<InvalidDataException>(() => new CliTextPresenter(output).PrintProfile(command, reply));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void Omitted_documented_defaults_are_complete_normalized_content()
    {
        var reply = Found("""{"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"layout","revision":1},"spec":{}}""");
        using var output = new StringWriter();
        Assert.Equal(0, new CliJsonPresenter(output).PrintProfile("profile revision get", reply));
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Object, result.RootElement.GetProperty("data").GetProperty("declaration").GetProperty("spec").ValueKind);
    }

    [Fact]
    public void Inconsistent_audit_and_invalid_utf8_fail_before_output()
    {
        var missingAudit = Found(); missingAudit.Metadata = null;
        var foreignAudit = Found(); foreignAudit.Metadata.Reference.ProfileId = "foreign";
        var foreignRevision = Found(); foreignRevision.Metadata.Reference.Revision = 2;
        var invalidTime = Found(); invalidTime.Metadata.ImportedAtUtc = "not-a-time";
        var invalidActor = Found(); invalidActor.Metadata.TechnicalActorId = "";
        var invalidEncoding = Found(); invalidEncoding.DeclarationJson = ByteString.CopyFrom([0xFF]);
        using var output = new StringWriter();
        foreach (var reply in new[] { missingAudit, foreignAudit, foreignRevision, invalidTime, invalidActor, invalidEncoding })
        {
            Assert.Throws<InvalidDataException>(() => new CliJsonPresenter(output).PrintProfile("profile revision get", reply));
            Assert.Throws<InvalidDataException>(() => new CliTextPresenter(output).PrintProfile("profile revision get", reply));
        }
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void Malformed_or_wrong_operation_replies_fail_before_any_output()
    {
        using var output = new StringWriter();
        var json = new CliJsonPresenter(output); var text = new CliTextPresenter(output);
        foreach (var reply in new ProfileReply[]
        {
            new(), new() { Status = (ProfileStatus)999 },
            new() { Status = ProfileStatus.Created },
            new() { Status = ProfileStatus.Valid },
            new() { Status = ProfileStatus.NotFound, Reference = new() { ProfileId = "layout" } },
            new() { Status = ProfileStatus.Found, Reference = new() { ProfileId = "layout", Revision = 1 } },
            new() { Status = ProfileStatus.InvalidDeclaration },
            new() { Status = ProfileStatus.RevisionContentConflict, Reference = new() { ProfileId = "layout", Revision = 1 } }
        })
        {
            Assert.Throws<InvalidDataException>(() => json.PrintProfile("profile revision get", reply));
            Assert.Throws<InvalidDataException>(() => text.PrintProfile("profile revision get", reply));
        }
        Assert.Equal("", output.ToString());
    }
}
