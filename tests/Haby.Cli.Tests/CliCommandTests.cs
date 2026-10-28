using Grpc.Core;
using Haby.Operator.Contracts;
using System.CommandLine;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliCommandTests
{
    private sealed class Endpoint(string? address) : IEndpointSource
    {
        public string? GetAddress() => address;
        public string? GetSkipTlsValidation() => null;
    }

    private sealed class FakeFactory : IRegistryClientFactory
    {
        public int Created { get; private set; }
        public FakeClient Client { get; } = new();
        public IRegistryClient Create(Uri address, bool skipTlsValidation)
        {
            Assert.Equal("localhost", address.Host);
            Assert.False(skipTlsValidation);
            Created++;
            return Client;
        }
    }

    private sealed class FakeClient : IRegistryClient
    {
        public string? Called { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }
        public GetRevisionRequest? GetRequest { get; private set; }
        public PreviewDatabaseRequest? PreviewRequest { get; private set; }
        public bool ThrowRpc { get; set; }
        public bool WaitForCancellation { get; set; }
        public bool ReturnSuccess { get; set; }
        public bool ReturnUnsetOutcome { get; set; }
        public void Dispose() { }

        public Task<ValidateReply> ValidateAsync(EnvelopeRequest request, CancellationToken cancellationToken)
        {
            Record("validate", cancellationToken);
            Assert.Equal("{}", request.OriginalEnvelope.ToStringUtf8());
            if (ReturnUnsetOutcome) return Task.FromResult(new ValidateReply());
            return Task.FromResult(ReturnSuccess
                ? new ValidateReply
                {
                    Valid = new ValidManifest
                    { ApplicationId = "app", ApplicationVersion = "1", Warnings = new DiagnosticReport() }
                }
                : new ValidateReply { InvalidManifest = new InvalidManifest { Report = new DiagnosticReport() } });
        }

        public Task<ImportReply> ImportAsync(EnvelopeRequest request, CancellationToken cancellationToken)
        {
            Record("import", cancellationToken);
            Assert.Equal("{}", request.OriginalEnvelope.ToStringUtf8());
            return Task.FromResult(new ImportReply { InvalidRequest = new InvalidRequest { Report = new DiagnosticReport() } });
        }

        public async Task<GetRevisionReply> GetRevisionAsync(GetRevisionRequest request, CancellationToken cancellationToken)
        {
            Record("get", cancellationToken);
            GetRequest = request;
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (ThrowRpc) throw new RpcException(new Status(StatusCode.Unavailable, "secret-server-detail"));
            return new GetRevisionReply { NotFound = request.Reference.Clone() };
        }

        public Task<PreviewDatabaseReply> PreviewDatabaseAsync(PreviewDatabaseRequest request, CancellationToken cancellationToken)
        {
            Record("preview", cancellationToken);
            PreviewRequest = request;
            return Task.FromResult(new PreviewDatabaseReply { RevisionNotFound = true });
        }

        private void Record(string command, CancellationToken token)
        {
            Called = command;
            ReceivedToken = token;
        }
    }

    private static CliApplication Create(FakeFactory factory, StringWriter output, StringWriter error,
        string? address = "http://localhost:7443") => new(new Endpoint(address), factory, output, error);

    [Fact]
    public async Task Build_time_registration_adds_command_without_registry_connection()
    {
        var factory = new FakeFactory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = new CliApplication(new Endpoint(null), factory, output, error, root =>
        {
            var command = new Command("local-check", "Run a local check.");
            command.SetAction(_ => { output.WriteLine("ready"); return 0; });
            root.Subcommands.Add(command);
        });

        Assert.Equal(CliApplication.Success, await cli.RunAsync(["local-check"]));
        Assert.Equal("ready" + Environment.NewLine, output.ToString());
        Assert.Equal(0, factory.Created);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("manifest --help")]
    [InlineData("manifest revision --help")]
    [InlineData("manifest revision get --help")]
    public async Task Help_needs_no_endpoint_or_client(string command)
    {
        var factory = new FakeFactory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(CliApplication.Success, await Create(factory, output, error, null).RunAsync(command.Split(' ')));
        Assert.Contains("--help", output.ToString());
        Assert.Equal(0, factory.Created);
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("manifest validate")]
    [InlineData("manifest")]
    [InlineData("manifest revision")]
    [InlineData("manifest revision get id")]
    [InlineData("manifest revision get id invalid")]
    [InlineData("manifest revision get id 0")]
    [InlineData("manifest revision preview id 1")]
    [InlineData("manifest unknown")]
    public async Task Invalid_syntax_does_not_create_client(string command)
    {
        var factory = new FakeFactory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(CliApplication.UsageError, await Create(factory, output, error).RunAsync(command.Split(' ')));
        Assert.Equal(0, factory.Created);
        Assert.NotEqual("", error.ToString());
    }

    [Fact]
    public async Task Four_commands_dispatch_and_preserve_rejected_outcomes()
    {
        var path = Path.Combine(Path.GetTempPath(), "haby-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "{}");
            var factory = new FakeFactory();
            using var output = new StringWriter();
            using var error = new StringWriter();
            var cli = Create(factory, output, error);
            Assert.Equal(CliApplication.Rejected, await cli.RunAsync(["manifest", "validate", path]));
            Assert.Equal("validate", factory.Client.Called);
            Assert.Equal(CliApplication.Rejected, await cli.RunAsync(["manifest", "import", path]));
            Assert.Equal("import", factory.Client.Called);
            Assert.Equal(CliApplication.Rejected, await cli.RunAsync(["manifest", "revision", "get", "record", "7"]));
            Assert.Equal("get", factory.Client.Called);
            Assert.Equal("record", factory.Client.GetRequest!.Reference.ApplicationRecordId);
            Assert.Equal(7, factory.Client.GetRequest.Reference.RevisionNumber);
            await File.WriteAllTextAsync(path, """{"preview":true,"environmentId":"test","providers":[{"alias":"main","instanceId":"local","configurationRevision":1,"implementationId":"database"}]}""");
            Assert.Equal(CliApplication.Rejected, await cli.RunAsync(["manifest", "revision", "preview", "record", "7", path]));
            Assert.Equal("preview", factory.Client.Called);
            Assert.Equal("test", factory.Client.PreviewRequest!.EnvironmentId);
            Assert.Equal(4, factory.Created);
            Assert.Contains("InvalidManifest", output.ToString());
            Assert.Contains("InvalidRequest", output.ToString());
            Assert.Contains("NotFound", output.ToString());
            Assert.Contains("RevisionNotFound", output.ToString());
            Assert.Equal("", error.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Cancellation_reaches_client_and_rpc_error_is_redacted()
    {
        var factory = new FakeFactory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var cli = Create(factory, output, error);
        Assert.Equal(CliApplication.Rejected, await cli.RunAsync(["manifest", "revision", "get", "record", "1"], cancellation.Token));
        Assert.True(factory.Client.ReceivedToken.CanBeCanceled);
        factory.Client.WaitForCancellation = true;
        var pending = cli.RunAsync(["manifest", "revision", "get", "record", "1"], cancellation.Token);
        cancellation.Cancel();
        Assert.Equal(CliApplication.TransportError, await pending);
        Assert.True(factory.Client.ReceivedToken.IsCancellationRequested);
        factory.Client.WaitForCancellation = false;
        factory.Client.ThrowRpc = true;
        Assert.Equal(CliApplication.TransportError, await cli.RunAsync(["manifest", "revision", "get", "record", "1"]));
        Assert.Contains("RPC failed: Unavailable", error.ToString());
        Assert.DoesNotContain("secret-server-detail", error.ToString());
    }

    [Fact]
    public async Task Success_and_local_file_failure_have_distinct_exit_codes()
    {
        var path = Path.Combine(Path.GetTempPath(), "haby-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "{}");
            var factory = new FakeFactory();
            factory.Client.ReturnSuccess = true;
            using var output = new StringWriter();
            using var error = new StringWriter();
            var cli = Create(factory, output, error);
            Assert.Equal(CliApplication.Success, await cli.RunAsync(["manifest", "validate", path]));
            Assert.Contains("Valid app 1", output.ToString());
            File.Delete(path);
            Assert.Equal(CliApplication.UsageError, await cli.RunAsync(["manifest", "validate", path]));
            Assert.DoesNotContain(path, error.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Unset_server_outcome_is_a_safe_local_error()
    {
        var path = Path.Combine(Path.GetTempPath(), "haby-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "{}");
            var factory = new FakeFactory();
            factory.Client.ReturnUnsetOutcome = true;
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(CliApplication.UsageError,
                await Create(factory, output, error).RunAsync(["manifest", "validate", path]));
            Assert.Equal("", output.ToString());
            Assert.Contains("Operation failed", error.ToString());
            Assert.DoesNotContain(path, error.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task File_reads_observe_cancellation()
    {
        var path = Path.Combine(Path.GetTempPath(), "haby-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "{}");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => CliApplication.ReadEnvelopeAsync(path, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => CliApplication.ReadPreviewContextAsync(path, new RevisionReference(), cancellation.Token));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Invalid_endpoint_is_usage_error_without_client()
    {
        var factory = new FakeFactory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(CliApplication.UsageError,
            await Create(factory, output, error, "https://remote.example").RunAsync(["manifest", "revision", "get", "id", "1"]));
        Assert.Equal(0, factory.Created);
        Assert.Contains("loopback", error.ToString());
    }
}
