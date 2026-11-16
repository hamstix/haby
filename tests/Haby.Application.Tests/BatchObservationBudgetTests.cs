using Haby.Application;
using Xunit;

namespace Haby.Application.Tests;

public sealed class BatchObservationBudgetTests
{
    [Fact]
    public void Settings_preserve_defaults_allow_narrowing_and_reject_each_widened_dimension()
    {
        var limits = new BatchObservationOptions();
        Assert.Equal(limits, limits.Narrow(null));
        Assert.Equal(new(200, 1, 1, 1, 1), limits.Narrow(new(1, 1, 1, 1)));
        foreach (var settings in new BatchObservationSettings[] { new(5), new(MaximumReads: 257), new(RequestMilliseconds: 30001), new(StartsPerSecond: 11) })
            Assert.Throws<ArgumentException>(() => limits.Narrow(settings));
        new BatchObservationOptions(200, 8, 1024, 300000, 100).Validate();
        foreach (var limitsOutsideRange in new BatchObservationOptions[] { new(0), new(201), new(Concurrency: 0), new(Concurrency: 9),
            new(MaximumReads: 0), new(MaximumReads: 1025), new(RequestMilliseconds: 0), new(RequestMilliseconds: 300001),
            new(StartsPerSecond: 0), new(StartsPerSecond: 101) }) Assert.Throws<ArgumentException>(limitsOutsideRange.Validate);
    }
    [Theory]
    [InlineData(4, 256)]
    [InlineData(8, 1024)]
    public async Task Exactly_shared_count_dispatches_and_charges_failed_reads(int concurrency, int maximum)
    {
        var time = new TestClock(); var budget = new BatchReadBudget(new(Concurrency: concurrency, MaximumReads: maximum, StartsPerSecond: 100), time, 0, TimeSpan.FromSeconds(300));
        var dispatched = 0;
        var admission = budget.Admission(_ => ValueTask.CompletedTask);
        for (var i = 0; i < maximum; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
            await Assert.ThrowsAsync<IOException>(async () => await admission.RunAsync(_ => { dispatched++; throw new IOException("safe fixture"); }, default));
        }
        Assert.False(budget.CanStart);
        await Assert.ThrowsAsync<RolloutBatchExhaustedException>(async () => await admission.RunAsync(_ => throw new InvalidOperationException(), default));
        Assert.Equal(maximum, dispatched);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public async Task Rate_waits_reauthorize_and_cancel_without_dispatch_or_leaked_delay(int rate)
    {
        var time = new TestClock(); var budget = new BatchReadBudget(new(Concurrency: 1, StartsPerSecond: rate), time, 0, TimeSpan.FromSeconds(30));
        var reads = 0; var authorizations = 0;
        var admission = budget.Admission(_ => { authorizations++; return ValueTask.CompletedTask; });
        ValueTask<RolloutObservation> Read(CancellationToken _) { reads++; return ValueTask.FromResult(new RolloutObservation(RolloutOutcome.Ready, "ready")); }
        await admission.RunAsync(Read, default);
        using var canceled = new CancellationTokenSource();
        var waiting = admission.RunAsync(Read, canceled.Token).AsTask();
        Assert.False(waiting.IsCompleted); Assert.Equal(1, reads); Assert.Equal(1, authorizations);
        canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        Assert.Equal(0, time.Pending);
        var next = admission.RunAsync(Read, default).AsTask();
        time.Advance(TimeSpan.FromSeconds(1.0 / rate));
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, reads); Assert.Equal(2, authorizations); Assert.Equal(0, time.Pending);
    }
    [Fact]
    public async Task Authorization_after_wait_can_refuse_and_spends_no_read_count()
    {
        var time = new TestClock(); var budget = new BatchReadBudget(new(MaximumReads: 1), time, 0, TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<RolloutReadRefusedException>(async () => await budget.Admission(_ => throw new RolloutReadRefusedException())
            .RunAsync(_ => throw new InvalidOperationException(), default));
        Assert.True(budget.CanStart);
        await budget.Admission(_ => ValueTask.CompletedTask).RunAsync(_ => ValueTask.FromResult(new RolloutObservation(RolloutOutcome.Ready, "ready")), default);
        Assert.False(budget.CanStart);
    }
    [Fact]
    public async Task Deadline_including_authorization_prevents_dispatch_and_new_member_admission()
    {
        var time = new TestClock(); var budget = new BatchReadBudget(new(), time, 0, TimeSpan.FromSeconds(1));
        var admission = budget.Admission(_ => { time.Advance(TimeSpan.FromSeconds(1)); return ValueTask.CompletedTask; });
        await Assert.ThrowsAsync<RolloutBatchExhaustedException>(async () => await admission.RunAsync(_ => throw new InvalidOperationException(), default));
        var member = new BatchMemberObservation(budget);
        Assert.Throws<RolloutBatchExhaustedException>(member.Start); Assert.False(member.Started);
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        private readonly List<DelayTimer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        internal int Pending => timers.Count(x => !x.Disposed);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DelayTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        internal void Advance(TimeSpan duration)
        {
            ticks += duration.Ticks;
            foreach (var timer in timers.ToArray().Where(x => !x.Disposed && x.At <= ticks)) timer.Fire();
        }
        private sealed class DelayTimer(TestClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal bool Disposed;
            internal long At;
            public bool Change(TimeSpan dueTime, TimeSpan period) { At = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + dueTime.Ticks; return !Disposed; }
            internal void Fire() { At = long.MaxValue; callback(state); }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
