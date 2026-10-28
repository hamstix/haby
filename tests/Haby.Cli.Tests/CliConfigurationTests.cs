using Haby.Operator.Contracts;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliConfigurationTests
{
    private const string Valid = """
        formatVersion: 1
        currentContext: first
        instances:
          first:
            endpoint: http://127.0.0.1:7001
          second:
            endpoint: http://127.0.0.1:7002
        contexts:
          first:
            instance: first
            environmentId: dev-one
          second:
            instance: second
            environmentId: dev-two
        """;

    private sealed class Paths(string? selected, string fallback) : IConfigEnvironment
    {
        public string? GetConfigPath() => selected;
        public string GetDefaultPath() => fallback;
    }

    private sealed class Endpoint(string? address) : IEndpointSource
    {
        public string? GetAddress() => address;
        public string? GetSkipTlsValidation() => null;
    }

    private sealed class Factory : IRegistryClientFactory
    {
        public List<Uri> Addresses { get; } = [];
        public IRegistryClient Create(Uri address, bool skipTlsValidation)
        {
            Addresses.Add(address);
            return new Client();
        }
    }

    private sealed class Client : IRegistryClient
    {
        public Task<GetRevisionReply> GetRevisionAsync(GetRevisionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new GetRevisionReply { NotFound = request.Reference.Clone() });
        public Task<ValidateReply> ValidateAsync(EnvelopeRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<ImportReply> ImportAsync(EnvelopeRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
        public Task<PreviewDatabaseReply> PreviewDatabaseAsync(PreviewDatabaseRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
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
        public void Dispose() { }
    }

    [Theory]
    [InlineData("formatVersion: 1\nformatVersion: 1\ninstances: {}\ncontexts: {}")]
    [InlineData("formatVersion: 1\nunknown: hidden\ninstances: {}\ncontexts: {}")]
    [InlineData("formatVersion: 1\ninstances: &saved {}\ncontexts: *saved")]
    [InlineData("formatVersion: 1\ninstances: {}\ncontexts: { <<: {} }")]
    [InlineData("formatVersion: 1\ninstances: !custom {}\ncontexts: {}")]
    [InlineData("formatVersion: 1\ninstances: {}\ncontexts: {}\n---\nformatVersion: 1")]
    [InlineData("formatVersion: true\ninstances: {}\ncontexts: {}")]
    [InlineData("formatVersion: 1\ninstances: []\ncontexts: {}")]
    [InlineData("formatVersion: 1\ncurrentContext: absent\ninstances: {}\ncontexts: {}")]
    [InlineData("formatVersion: 1\ninstances: {}\ncontexts: {bad: {instance: absent, environmentId: dev}}")]
    [InlineData("formatVersion: 1\ninstances: {bad: {endpoint: https://remote.example}}\ncontexts: {}")]
    [InlineData("formatVersion: 1\ninstances: {bad: {endpoint: http://localhost:1, extra: secret}}\ncontexts: {}")]
    [InlineData("formatVersion: 1\ninstances: {}\ncontexts: {bad: {instance: true, environmentId: dev}}")]
    [InlineData("formatVersion: 1\ninstances: {}\ncontexts: {bad: {instance: 42, environmentId: dev}}")]
    [InlineData("formatVersion: 1\ninstances: {x: {endpoint: http://localhost:1}}\ncontexts: {x: {instance: x, environmentId: [nested]}}")]
    public void Rejects_invalid_shape_without_echoing_values(string yaml)
    {
        var error = Assert.Throws<InvalidDataException>(() => CliConfigurationService.Parse(yaml));
        Assert.Equal("Invalid CLI configuration.", error.Message);
    }

    [Fact]
    public void Rejects_excessive_nesting_before_building_representation()
    {
        var yaml = "formatVersion: 1\ninstances: " + new string('[', 9) + "x" + new string(']', 9) + "\ncontexts: {}";
        Assert.Throws<InvalidDataException>(() => CliConfigurationService.Parse(yaml));
    }

    [Fact]
    public async Task Bounded_file_rejects_bad_encoding_oversize_and_cancellation()
    {
        var file = Path.GetTempFileName();
        try
        {
            var service = new CliConfigurationService(new Paths(null, file));
            await File.WriteAllBytesAsync(file, [0xC3, 0x28]);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(file, default));
            await File.WriteAllBytesAsync(file, new byte[CliConfigurationService.MaxBytes + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(file, default));
            await File.WriteAllTextAsync(file, Valid);
            using var source = new CancellationTokenSource();
            source.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync(file, source.Token));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Path_and_context_precedence_are_deterministic_and_offline_commands_are_safe()
    {
        var folder = Path.Combine(Path.GetTempPath(), "haby-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var defaultPath = Path.Combine(folder, "default.yaml");
        var envPath = Path.Combine(folder, "env.yaml");
        var overridePath = Path.Combine(folder, "override.yaml");
        try
        {
            await File.WriteAllTextAsync(defaultPath, Valid.Replace("7001", "7101"));
            await File.WriteAllTextAsync(envPath, Valid.Replace("7001", "7201"));
            await File.WriteAllTextAsync(overridePath, Valid);
            var factory = new Factory();
            using var output = new StringWriter();
            using var error = new StringWriter();
            var command = new CliApplication(new Endpoint("http://localhost:7999"), factory, output, error,
                configEnvironment: new Paths(envPath, defaultPath));
            Assert.Equal(0, await command.RunAsync(["config", "view", "--config", overridePath]));
            Assert.Contains("first: http://127.0.0.1:7001", output.ToString());
            Assert.Empty(factory.Addresses);
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await command.RunAsync(["config", "current-context", "--context", "second"]));
            Assert.Equal("second" + Environment.NewLine, output.ToString());
            Assert.Equal(1, await command.RunAsync(["manifest", "revision", "get", "record", "1"]));
            Assert.Equal(7201, factory.Addresses.Last().Port);
            Assert.Equal(1, await command.RunAsync(["manifest", "revision", "get", "record", "1", "--context", "second"]));
            Assert.Equal(7002, factory.Addresses.Last().Port);
            Assert.Equal(1, await command.RunAsync(["manifest", "revision", "get", "record", "1", "--config", overridePath]));
            Assert.Equal(7001, factory.Addresses.Last().Port);
            Assert.Equal(3, factory.Addresses.Count);
            Assert.Equal(2, await command.RunAsync(["manifest", "revision", "get", "record", "1", "--context", "absent"]));
            Assert.Equal(3, factory.Addresses.Count);
            Assert.Contains("Invalid CLI configuration.", error.ToString());
            Assert.Equal(2, await command.RunAsync(["config", "view", "--config", Path.Combine(folder, "missing.yaml")]));
            Assert.DoesNotContain(folder, error.ToString());

            var defaultFactory = new Factory();
            var defaultCommand = new CliApplication(new Endpoint("http://localhost:7999"), defaultFactory,
                output, error, configEnvironment: new Paths(null, defaultPath));
            Assert.Equal(1, await defaultCommand.RunAsync(["manifest", "revision", "get", "record", "1"]));
            Assert.Equal(7101, defaultFactory.Addresses.Single().Port);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Missing_default_uses_legacy_endpoint_and_help_ignores_bad_config()
    {
        var folder = Path.Combine(Path.GetTempPath(), "haby-config-" + Guid.NewGuid().ToString("N"));
        var factory = new Factory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = new CliApplication(new Endpoint("http://localhost:7999"), factory, output, error,
            configEnvironment: new Paths(null, Path.Combine(folder, "missing.yaml")));
        Assert.Equal(1, await cli.RunAsync(["manifest", "revision", "get", "record", "1"]));
        Assert.Equal(7999, factory.Addresses.Single().Port);
        Assert.Equal(0, await cli.RunAsync(["--help", "--config", Path.Combine(folder, "missing.yaml")]));
        Assert.Equal(0, await cli.RunAsync(["--version", "--config", Path.Combine(folder, "missing.yaml")]));
        Assert.Single(factory.Addresses);
    }
}
