using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class ConfiguredLifecycleCallerTests
{
    [Fact]
    public void Post_delivery_drain_does_not_receive_the_expiring_delivery_token()
    {
        var root = Environment.GetEnvironmentVariable("HABY_TEST_SOURCE_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.True(Path.IsPathFullyQualified(root!));
        var source = File.ReadAllText(Path.Combine(root!, "tools/Haby.Kubernetes.Qualification/ConfiguredUpdateRetirement.cs"));
        Assert.DoesNotContain("await drain(delivery.Token)", source, StringComparison.Ordinal);
    }
}
