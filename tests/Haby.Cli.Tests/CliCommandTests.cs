using Grpc.Core;
using Haby.Operator.Contracts;
using System.CommandLine;
using System.Text.Json;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliCommandTests
{
    private sealed class Endpoint(string? address) : IEndpointSource
    {
        public string? GetAddress() => address;
        public string? GetSkipTlsValidation() => null;
    }

    private sealed class MissingConfig : IConfigEnvironment
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(),
            "haby-cli-missing-" + Guid.NewGuid().ToString("N"), "config.yaml");

        public string? GetConfigPath() => null;
        public string GetDefaultPath() => _path;
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
        public Task<ProviderAccessReply> CheckProviderAsync(CheckProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProviderAccessReply> GetProviderAliasAsync(GetProviderAliasRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProviderAccessReply> SetProviderAliasAsync(SetProviderAliasRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProviderReply> ApplyProviderAsync(ApplyProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProviderReply> GetProviderAsync(GetProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ListProvidersReply> ListProvidersAsync(ListProvidersRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SecretReply> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SecretReply> GetSecretAsync(GetSecretRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ListSecretsReply> ListSecretsAsync(ListSecretsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public string? Called { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }
        public GetRevisionRequest? GetRequest { get; private set; }
        public PreviewDatabaseRequest? PreviewRequest { get; private set; }
        public bool ThrowRpc { get; set; }
        public bool WaitForCancellation { get; set; }
        public bool ReturnSuccess { get; set; }
        public bool ReturnUnsetOutcome { get; set; }
        public ValidateReply? ValidateResult { get; set; }
        public ImportReply? ImportResult { get; set; }
        public GetRevisionReply? GetResult { get; set; }
        public PreviewDatabaseReply? PreviewResult { get; set; }
        public Task<PlanningReply> CreateChangeSetAsync(CreateChangeSetRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<PlanningReply> GetChangeSetAsync(ChangeSetRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<PlanningReply> CreatePlanAsync(ChangeSetRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<PlanningReply> GetPlanAsync(PlanRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<PlanningReply> CheckPlanAsync(PlanRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<PlanningReply> ClosePlanAsync(PlanRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<CommitReply> CommitDesiredStateAsync(CommitDesiredStateRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<CommitReply> GetCommitAsync(ChangeSetRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<CommitReply> GetGenerationAsync(GenerationRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<OperationReply> ExecuteOperationAsync(OperationRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<OperationReply> GetOperationAsync(OperationRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<RecoveryReply> InspectOperationRecoveryAsync(OperationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RecoveryReply> AssignExistingOperationAsync(AssignExistingOperationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentReply> CreateEnvironmentAsync(EnvironmentNameRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentReply> GetEnvironmentAsync(EnvironmentNameRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ListEnvironmentsReply> ListEnvironmentsAsync(ListEnvironmentsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Dispose() { }

        public Task<ValidateReply> ValidateAsync(EnvelopeRequest request, CancellationToken cancellationToken)
        {
            Record("validate", cancellationToken);
            Assert.Equal("{}", request.OriginalEnvelope.ToStringUtf8());
            if (ValidateResult is not null) return Task.FromResult(ValidateResult);
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
            if (ImportResult is not null) return Task.FromResult(ImportResult);
            return Task.FromResult(new ImportReply { InvalidRequest = new InvalidRequest { Report = new DiagnosticReport() } });
        }

        public async Task<GetRevisionReply> GetRevisionAsync(GetRevisionRequest request, CancellationToken cancellationToken)
        {
            Record("get", cancellationToken);
            GetRequest = request;
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (ThrowRpc) throw new RpcException(new Status(StatusCode.Unavailable, "secret-server-detail"));
            if (GetResult is not null) return GetResult;
            return new GetRevisionReply { NotFound = request.Reference.Clone() };
        }

        public Task<PreviewDatabaseReply> PreviewDatabaseAsync(PreviewDatabaseRequest request, CancellationToken cancellationToken)
        {
            Record("preview", cancellationToken);
            PreviewRequest = request;
            if (PreviewResult is not null) return Task.FromResult(PreviewResult);
            return Task.FromResult(new PreviewDatabaseReply { RevisionNotFound = true });
        }

        private void Record(string command, CancellationToken token)
        {
            Called = command;
            ReceivedToken = token;
        }
    }

    private static CliApplication Create(FakeFactory factory, StringWriter output, StringWriter error,
        string? address = "http://localhost:7443") => new(new Endpoint(address), factory, output, error,
            configEnvironment: new MissingConfig());

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
            error.GetStringBuilder().Clear();
            Assert.Equal(CliApplication.UsageError,
                await Create(factory, output, error).RunAsync(["manifest", "validate", path, "-o", "json"]));
            Assert.Equal("", output.ToString());
            Assert.Contains("Operation failed", error.ToString());
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

    [Theory]
    [InlineData("-o nope manifest revision get id 1")]
    [InlineData("manifest -o nope revision get id 1")]
    [InlineData("manifest revision get id 1 --output nope")]
    public async Task Unsupported_output_fails_before_config_or_client(string command)
    {
        var factory = new FakeFactory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = Create(factory, output, error, null);
        Assert.Equal(2, await cli.RunAsync(command.Split(' ')));
        Assert.Equal("", output.ToString());
        Assert.NotEqual("", error.ToString());
        Assert.Equal(0, factory.Created);
    }

    [Theory]
    [InlineData("-o json manifest revision get record 1")]
    [InlineData("manifest -o json revision get record 1")]
    [InlineData("manifest revision get record 1 --output json")]
    public async Task Json_option_is_recursive_and_emits_one_envelope(string command)
    {
        var factory = new FakeFactory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await Create(factory, output, error).RunAsync(command.Split(' ')));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("manifest revision get", document.RootElement.GetProperty("command").GetString());
        Assert.Equal("notFound", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("", error.ToString());
        Assert.Equal(1, factory.Created);
    }

    [Fact]
    public async Task Json_transport_and_local_failures_keep_stdout_empty_and_redact_details()
    {
        var factory = new FakeFactory();
        factory.Client.ThrowRpc = true;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = Create(factory, output, error);
        Assert.Equal(3, await cli.RunAsync(["manifest", "revision", "get", "record", "1", "-o", "json"]));
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain("secret-server-detail", error.ToString());
        Assert.Equal(2, await cli.RunAsync(["manifest", "revision", "get", "record", "0", "-o", "json"]));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task Version_stays_human_readable_with_json_option_and_missing_config()
    {
        var factory = new FakeFactory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await Create(factory, output, error, null).RunAsync(
            ["--version", "--config", "missing.yaml", "-o", "json"]));
        Assert.Equal(0, factory.Created);
        Assert.False(output.ToString().TrimStart().StartsWith('{'));
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task Every_result_command_routes_success_and_rejection_through_json_mode()
    {
        var path = Path.Combine(Path.GetTempPath(), "haby-json-routing-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "{}");
            var factory = new FakeFactory();
            using var output = new StringWriter();
            using var error = new StringWriter();
            var cli = Create(factory, output, error);

            async Task Verify(string[] args, int exit, string command, string outcome)
            {
                output.GetStringBuilder().Clear();
                Assert.Equal(exit, await cli.RunAsync(args));
                var json = output.ToString();
                Assert.Equal(1, json.Count(character => character == '\n'));
                using var document = JsonDocument.Parse(json);
                Assert.Equal(command, document.RootElement.GetProperty("command").GetString());
                Assert.Equal(outcome, document.RootElement.GetProperty("outcome").GetString());
                Assert.Equal(5, document.RootElement.EnumerateObject().Count());
                Assert.Equal("", error.ToString());
            }

            factory.Client.ValidateResult = new ValidateReply
            {
                Valid = new ValidManifest
                { ApplicationId = "app", ApplicationVersion = "v1", Warnings = new DiagnosticReport() }
            };
            await Verify(["manifest", "validate", path, "-o", "json"], 0, "manifest validate", "valid");
            factory.Client.ValidateResult = new ValidateReply
            {
                InvalidManifest = new InvalidManifest
                { Report = new DiagnosticReport() }
            };
            await Verify(["manifest", "validate", path, "-o", "json"], 1, "manifest validate", "invalidManifest");

            factory.Client.ImportResult = new ImportReply
            {
                Created = new ImportAccepted
                { Revision = new RevisionSummary { Reference = new RevisionReference(), HistoricalWarnings = new DiagnosticReport() } }
            };
            await Verify(["manifest", "import", path, "-o", "json"], 0, "manifest import", "created");
            factory.Client.ImportResult = new ImportReply
            {
                InvalidRequest = new InvalidRequest
                { Report = new DiagnosticReport() }
            };
            await Verify(["manifest", "import", path, "-o", "json"], 1, "manifest import", "invalidRequest");

            factory.Client.GetResult = new GetRevisionReply
            {
                Found = new RevisionSummary
                { Reference = new RevisionReference(), HistoricalWarnings = new DiagnosticReport() }
            };
            await Verify(["manifest", "revision", "get", "record", "1", "-o", "json"],
                0, "manifest revision get", "found");
            factory.Client.GetResult = new GetRevisionReply
            {
                NotFound = new RevisionReference
                { ApplicationRecordId = "record", RevisionNumber = 1 }
            };
            await Verify(["manifest", "revision", "get", "record", "1", "-o", "json"],
                1, "manifest revision get", "notFound");

            await File.WriteAllTextAsync(path,
                """{"preview":true,"environmentId":"test","providers":[{"alias":"main","instanceId":"local","configurationRevision":1,"implementationId":"database"}]}""");
            factory.Client.PreviewResult = new PreviewDatabaseReply
            {
                WouldCreate = new PreviewWouldCreate
                { EnvironmentId = "test", ResourceId = "db", DatabaseName = "example" }
            };
            await Verify(["manifest", "revision", "preview", "record", "1", path, "-o", "json"],
                0, "manifest revision preview", "wouldCreate");
            factory.Client.PreviewResult = new PreviewDatabaseReply { RevisionNotFound = true };
            await Verify(["manifest", "revision", "preview", "record", "1", path, "-o", "json"],
                1, "manifest revision preview", "revisionNotFound");

            await Verify(["config", "view", "-o", "json"], 0, "config view", "ok");
            await Verify(["config", "current-context", "-o", "json"], 0, "config current-context", "ok");
        }
        finally { File.Delete(path); }
    }
}
