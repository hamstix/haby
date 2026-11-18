using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class ConfiguredLifecycleTests
{
    [Fact]
    public void Drain_has_its_own_45_seconds_after_elapsed_delivery_and_functional_completion()
    {
        var time = new Clock(); var records = new List<string>();
        using var life = new ConfiguredLifecycle(clock: time, report: records.Add);
        time.Advance(110);
        using var delivery = life.Start(ConfiguredLifecycle.Kind.Delivery);
        time.Advance(30);
        delivery.Complete(); life.Functional.Complete();
        using var drain = life.Start(ConfiguredLifecycle.Kind.Drain);
        time.Advance(30);
        Assert.False(drain.Token.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromSeconds(15), drain.Remaining);
        drain.Complete();
        Assert.Contains(records, x => x == "Q243 configured budget Drain Complete 30000 15000 70000 None");
    }
    [Fact]
    public void Own_phase_cap_expires_and_drain_cannot_fabricate_successful_delivery()
    {
        var time = new Clock(); using var life = new ConfiguredLifecycle(clock: time, report: _ => { });
        Assert.Throws<InvalidOperationException>(() => life.Start(ConfiguredLifecycle.Kind.Functional));
        Assert.Throws<InvalidOperationException>(life.Complete);
        Assert.Throws<InvalidOperationException>(() => life.Start(ConfiguredLifecycle.Kind.Drain));
        using var delivery = life.Start(ConfiguredLifecycle.Kind.Delivery);
        time.Advance(35);
        Assert.True(delivery.Token.IsCancellationRequested); Assert.Equal("Phase", delivery.Cause);
        Assert.ThrowsAny<OperationCanceledException>(delivery.Complete);
        Assert.False(life.Functional.Completed);
        Assert.Throws<InvalidOperationException>(() => life.Start(ConfiguredLifecycle.Kind.Drain));
    }
    [Fact]
    public void Overall_expiry_caps_late_phases_without_renewal()
    {
        var time = new Clock(); using var life = new ConfiguredLifecycle(clock: time, report: _ => { });
        time.Advance(140); life.Functional.Complete(); time.Advance(89);
        using var drain = life.Start(ConfiguredLifecycle.Kind.Drain);
        Assert.Equal(TimeSpan.FromSeconds(11), drain.Remaining);
        time.Advance(11);
        Assert.True(drain.Token.IsCancellationRequested); Assert.Equal("Overall", drain.Cause);
        using var cleanup = life.Start(ConfiguredLifecycle.Kind.NamespaceCleanup);
        Assert.True(cleanup.Token.IsCancellationRequested); Assert.Equal(TimeSpan.Zero, cleanup.Remaining);
        Assert.ThrowsAny<OperationCanceledException>(life.Complete);
    }
    [Fact]
    public void User_cancellation_reaches_functional_and_independent_cleanup()
    {
        var time = new Clock(); using var user = new CancellationTokenSource();
        using var life = new ConfiguredLifecycle(user.Token, time, _ => { });
        using var cleanup = life.Start(ConfiguredLifecycle.Kind.NamespaceCleanup);
        user.Cancel();
        Assert.True(life.Functional.Token.IsCancellationRequested); Assert.True(cleanup.Token.IsCancellationRequested);
        Assert.Equal("User", cleanup.Cause);
    }
    [Fact]
    public void Functional_failure_keeps_primary_and_independent_partial_cleanup_failure()
    {
        var time = new Clock(); var records = new List<string>();
        using var life = new ConfiguredLifecycle(clock: time, report: records.Add);
        time.Advance(150);
        Assert.True(life.Functional.Token.IsCancellationRequested);
        life.Functional.Fail(); life.Functional.Fail();
        var primary = new InvalidOperationException("functional control");
        using (var cleanup = life.Start(ConfiguredLifecycle.Kind.NamespaceCleanup))
        {
            Assert.False(cleanup.Token.IsCancellationRequested);
            time.Advance(20);
            var secondary = Assert.ThrowsAny<OperationCanceledException>(cleanup.Complete);
            var combined = Assert.IsType<AggregateException>(ConfiguredLifecycle.Combine(primary, [secondary]));
            Assert.Same(primary, combined.InnerExceptions[0]); Assert.Same(secondary, combined.InnerExceptions[1]);
        }
        Assert.Contains(records, x => x == "Q243 configured budget NamespaceCleanup Failed 20000 0 70000 Phase");
        Assert.Single(records, x => x == "Q243 configured budget Functional Failed 150000 0 90000 Phase");
        Assert.False(life.Token.IsCancellationRequested);
    }
    [Fact]
    public void Native_cli_keeps_30_second_cap_and_links_functional_expiry()
    {
        var time = new Clock(); using var life = new ConfiguredLifecycle(clock: time, report: _ => { });
        using (var cli = life.Start(ConfiguredLifecycle.Kind.NativeCli))
        {
            time.Advance(30);
            Assert.True(cli.Token.IsCancellationRequested); Assert.Equal("Phase", cli.Cause);
            Assert.False(life.Functional.Token.IsCancellationRequested);
        }
        time.Advance(119);
        using var late = life.Start(ConfiguredLifecycle.Kind.NativeCli);
        Assert.Equal(TimeSpan.FromSeconds(1), late.Remaining);
        time.Advance(1);
        Assert.True(late.Token.IsCancellationRequested); Assert.Equal("Functional", late.Cause);
    }
    [Fact]
    public async Task Owned_cli_cancellation_kills_and_drains_but_never_returns_success()
    {
        var time = new Clock(); using var parent = new CancellationTokenSource();
        var exit = new TaskCompletionSource(); var output = new TaskCompletionSource<string>(); var error = new TaskCompletionSource<string>(); var kills = 0;
        var call = ConfiguredCliProcess.CollectAsync(exit.Task, output.Task, error.Task, () =>
        { kills++; exit.SetResult(); output.SetResult("complete"); error.SetResult(""); }, parent.Token, time);
        parent.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
        Assert.Equal(1, kills); Assert.True(exit.Task.IsCompleted); Assert.True(output.Task.IsCompleted);
    }
    [Fact]
    public async Task Owned_cli_incomplete_output_and_failed_termination_are_bounded_failures()
    {
        var time = new Clock(); var output = new TaskCompletionSource<string>();
        var call = ConfiguredCliProcess.CollectAsync(Task.CompletedTask, output.Task, Task.FromResult(""), () => { }, default, time);
        time.Advance(5);
        var incomplete = await Assert.ThrowsAsync<ConfiguredCliProcess.Incomplete>(async () => await call);
        Assert.Equal("Output", incomplete.Reason);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var stopped = new TaskCompletionSource(); var missing = new TaskCompletionSource<string>();
        var missingTail = ConfiguredCliProcess.CollectAsync(stopped.Task, missing.Task, Task.FromResult(""),
            () => stopped.SetResult(), canceled.Token, time);
        time.Advance(5);
        var lost = await Assert.ThrowsAsync<ConfiguredCliProcess.Incomplete>(async () => await missingTail);
        Assert.Equal("Output", lost.Reason); Assert.True(stopped.Task.IsCompleted);
        Assert.IsAssignableFrom<OperationCanceledException>(lost.InnerException);
        var exit = new TaskCompletionSource();
        var failed = await Assert.ThrowsAsync<ConfiguredCliProcess.Incomplete>(() => ConfiguredCliProcess.CollectAsync(exit.Task,
            Task.FromResult(""), Task.FromResult(""), () => throw new InvalidOperationException("owned control"), canceled.Token, time));
        Assert.Equal("Termination", failed.Reason); Assert.IsAssignableFrom<OperationCanceledException>(failed.InnerException);
        Assert.False(exit.Task.IsCompleted); // Not represented as cleaned or completed.
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        private readonly List<Timer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        internal void Advance(int seconds)
        {
            ticks += TimeSpan.FromSeconds(seconds).Ticks;
            foreach (var timer in timers.ToArray().Where(x => !x.Disposed && x.At <= ticks)) timer.Fire();
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            internal bool Disposed;
            internal long At;
            public bool Change(TimeSpan dueTime, TimeSpan period) { At = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks; return !Disposed; }
            internal void Fire() { At = long.MaxValue; callback(state); }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
