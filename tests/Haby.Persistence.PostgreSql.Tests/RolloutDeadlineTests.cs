using Haby.Persistence.PostgreSql;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class RolloutDeadlineTests
{
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Utc = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        internal long Timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reservation_time_cannot_refresh_the_active_read_budget(bool monotonicOnly)
    {
        var clock = new Clock(); var deadline = clock.Utc.AddSeconds(2); var started = clock.GetTimestamp();
        Assert.True(RolloutDeadlines.CanReadActive(clock, started, TimeSpan.FromSeconds(2), deadline));
        if (monotonicOnly) clock.Timestamp += TimeSpan.FromSeconds(2).Ticks;
        else clock.Utc = deadline;
        Assert.False(RolloutDeadlines.CanReadActive(clock, started, TimeSpan.FromSeconds(2), deadline));
    }
    [Fact]
    public void Retained_expiry_cannot_be_reopened_by_UTC_reversal()
    {
        var now = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero); var deadline = now.AddSeconds(2);
        Assert.True(RolloutDeadlines.Expired(deadline, deadline, false));
        Assert.False(RolloutDeadlines.Expired(now, deadline, false));
        Assert.True(RolloutDeadlines.Expired(now, deadline, true));
    }
}
