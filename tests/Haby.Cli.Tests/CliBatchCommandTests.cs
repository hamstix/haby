using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliBatchCommandTests
{
    [Fact]
    public async Task Exact_generation_batch_wait_is_exposed_as_one_file_command()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var cli = new CliApplication(null!, null!, output, error);
        Assert.Equal(0, await cli.RunAsync(["deployment", "generation", "wait-batch", "--help"]));
        Assert.Contains("--file", output.ToString());
        Assert.Contains("--request-id", output.ToString());
    }
}