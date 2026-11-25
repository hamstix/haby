using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliGroupHelpTests
{
    [Theory]
    [InlineData("manifest")]
    [InlineData("config")]
    public async Task Bare_group_prints_contextual_help_without_resolving_endpoint(string group)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var cli = new CliApplication(new NoEndpoint(), new NoClient(), output, error);
        Assert.Equal(CliApplication.UsageError, await cli.RunAsync([group, "--output", "json"]));
        Assert.Contains("Usage:", error.ToString());
        Assert.Contains(group, error.ToString());
        Assert.Contains("Commands:", error.ToString());
        Assert.Equal("", output.ToString());
    }
    private sealed class NoEndpoint : IEndpointSource
    {
        public string? GetAddress() => throw new InvalidOperationException("Help resolved endpoint");
        public string? GetSkipTlsValidation() => throw new InvalidOperationException();
    }
    private sealed class NoClient : IRegistryClientFactory
    {
        public IRegistryClient Create(Uri address, bool skipTlsValidation, string? credential = null) => throw new InvalidOperationException();
    }
}
