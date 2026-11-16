using Haby.Application;
using Xunit;

namespace Haby.Application.Tests;

public sealed class OperatorObservationSettingsTests
{
    [Fact]
    public void Defaults_preserve_configured_limits_and_callers_can_only_narrow()
    {
        var limits = new RolloutOptions(12, 9, 700, 800, 5000);
        Assert.Equal(limits, ObservationSettings.Narrow(limits, null));
        Assert.Equal(new RolloutOptions(1, 1, 100, 1000, 1), ObservationSettings.Narrow(limits, new(1, 1, 100, 1000, 1)));
    }
    [Theory]
    [InlineData(13, 9, 700, 800, 5000)]
    [InlineData(12, 10, 700, 800, 5000)]
    [InlineData(12, 9, 701, 800, 5000)]
    [InlineData(12, 9, 700, 799, 5000)]
    [InlineData(12, 9, 700, 800, 5001)]
    public void Widening_any_Host_limit_is_rejected(int calls, int rounds, int read, int poll, int request) =>
        Assert.Throws<ArgumentException>(() => ObservationSettings.Narrow(new(12, 9, 700, 800, 5000), new(calls, rounds, read, poll, request)));
}
