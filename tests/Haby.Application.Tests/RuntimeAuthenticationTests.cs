using Haby.Application;
using Xunit;

namespace Haby.Application.Tests;

public sealed class RuntimeAuthenticationTests
{
    [Fact]
    public async Task Provider_wait_releases_global_capacity_and_oldest_eligible_provider_progresses()
    {
        var clock = new Clock(); using var admission = new RuntimeAuthenticationAdmission(new(2, 1, 4, 2, 2), clock);
        var firstProvider = Guid.NewGuid(); var secondProvider = Guid.NewGuid();
        using var first = admission.Enter(default)!; await first.LookupAsync(default); await first.ProviderAsync(firstProvider, default);
        using var second = admission.Enter(default)!; await second.LookupAsync(default);
        var blocked = second.ProviderAsync(firstProvider, default); Assert.False(blocked.IsCompleted);
        using var third = admission.Enter(default)!; await third.LookupAsync(default); await third.ProviderAsync(secondProvider, default);
        using var fourth = admission.Enter(default)!; var waitingGlobal = fourth.LookupAsync(default);
        Assert.False(waitingGlobal.IsCompleted); Assert.Null(admission.Enter(default));
        first.Dispose(); await blocked; Assert.False(waitingGlobal.IsCompleted);
        third.Dispose(); await waitingGlobal;
    }

    [Fact]
    public async Task Combined_rate_buckets_keep_spent_budget_across_bindings_and_release_concurrency()
    {
        var clock = new Clock(); using var admission = new RuntimeAuthenticationAdmission(new(2, 2, 2, 1, 4), clock);
        var provider = Guid.NewGuid();
        using (var a = admission.Enter(default)!) { await a.LookupAsync(default); await a.ProviderAsync(provider, default); }
        using var b = admission.Enter(default)!; await b.LookupAsync(default); var perProvider = b.ProviderAsync(provider, default);
        Assert.False(perProvider.IsCompleted);
        using (var c = admission.Enter(default)!) { await c.LookupAsync(default); await c.ProviderAsync(Guid.NewGuid(), default); }
        using var d = admission.Enter(default)!; await d.LookupAsync(default); var global = d.ProviderAsync(Guid.NewGuid(), default);
        Assert.False(global.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(500)); await global; Assert.False(perProvider.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(500)); await perProvider;
    }

    [Fact]
    public async Task Queued_cancellation_and_queue_expiry_do_not_leak_capacity()
    {
        var clock = new Clock(); using var admission = new RuntimeAuthenticationAdmission(new(1, 1, 1, 1, 1, 1), clock);
        using var active = admission.Enter(default)!; await active.LookupAsync(default); await active.ProviderAsync(Guid.NewGuid(), default);
        using (var queued = admission.Enter(default)!)
        {
            using var cancel = new CancellationTokenSource(); var wait = queued.LookupAsync(cancel.Token); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        }
        using (var expired = admission.Enter(default)!)
        {
            var wait = expired.LookupAsync(default); clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<RuntimeAdmissionException>(() => wait);
        }
        active.Dispose(); using var available = admission.Enter(default)!; await available.LookupAsync(default);
    }

    [Fact]
    public async Task Provider_state_cannot_be_reset_before_refill_and_is_bounded()
    {
        var clock = new Clock(); using var admission = new RuntimeAuthenticationAdmission(new(1, 1, 10000, 1, 1), clock);
        for (var i = 0; i < RuntimeAuthenticationAdmission.MaximumProviders; i++)
        {
            using var ticket = admission.Enter(default)!; await ticket.LookupAsync(default); await ticket.ProviderAsync(Guid.NewGuid(), default);
        }
        using (var refused = admission.Enter(default)!)
        {
            await refused.LookupAsync(default);
            await Assert.ThrowsAsync<RuntimeAdmissionException>(() => refused.ProviderAsync(Guid.NewGuid(), default));
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        using var next = admission.Enter(default)!; await next.LookupAsync(default); await next.ProviderAsync(Guid.NewGuid(), default);
    }

    [Theory]
    [InlineData(0, 1, 50, 25, 256, 10, 5)]
    [InlineData(257, 1, 50, 25, 256, 10, 5)]
    [InlineData(32, 33, 50, 25, 256, 10, 5)]
    [InlineData(32, 16, 0, 25, 256, 10, 5)]
    [InlineData(32, 16, 50, 51, 256, 10, 5)]
    [InlineData(32, 16, 50, 25, 4097, 10, 5)]
    [InlineData(32, 16, 50, 25, 256, 61, 5)]
    [InlineData(32, 16, 50, 25, 256, 10, 31)]
    public void Invalid_limits_fail_validation(int global, int provider, int rps, int providerRps, int queue, int wait, int call) =>
        Assert.Throws<ArgumentException>(() => new RuntimeAuthenticationOptions(global, provider, rps, providerRps, queue, wait, call).Validate());

    [Theory]
    [InlineData("revoked", "PermissionDenied")]
    [InlineData("unknown", "Unauthenticated")]
    [InlineData("unsupported", "Unauthenticated")]
    [InlineData("corrupt", "Unauthenticated")]
    [InlineData("unavailable", "Unavailable")]
    public async Task Refused_metadata_never_invokes_adapter(string fault, string expected)
    {
        var clock = new Clock(); using var admission = new RuntimeAuthenticationAdmission(new(), clock);
        var port = new Ports { Fault = fault }; var service = new RuntimeAuthentication(port, [port], admission, clock);
        var result = await service.AuthenticateAsync(port.Trust.Binding, "transient-credential"u8.ToArray(), default);
        Assert.Equal(expected, result.Outcome.ToString()); Assert.Null(result.Context); Assert.Equal(0, port.Calls);
    }

    [Fact]
    public async Task Success_rechecks_current_authority_and_retains_no_credential_or_publication_path()
    {
        var clock = new Clock(); using var admission = new RuntimeAuthenticationAdmission(new(), clock);
        var port = new Ports(); var service = new RuntimeAuthentication(port, [port], admission, clock);
        var successful = await service.AuthenticateAsync(port.Trust.Binding, "first"u8.ToArray(), default);
        Assert.Equal(RuntimeAuthenticationOutcome.Authenticated, successful.Outcome); Assert.Equal(port.Trust.Binding, successful.Context!.Binding);
        Assert.True(port.Observed.Span.IndexOfAnyExcept((byte)0) < 0);
        port.Current = false;
        Assert.Equal(RuntimeAuthenticationOutcome.PermissionDenied, (await service.AuthenticateAsync(port.Trust.Binding, "rotated"u8.ToArray(), default)).Outcome);
        Assert.Equal(2, port.Calls); Assert.Equal(2, port.Checks);
    }

    [Fact]
    public async Task Call_timeout_and_caller_cancellation_release_capacity_and_clear_owned_credentials()
    {
        var clock = new Clock(); using var admission = new RuntimeAuthenticationAdmission(new(1, 1, 100, 100, 0, 10, 1), clock);
        var port = new Ports { Block = true }; var service = new RuntimeAuthentication(port, [port], admission, clock);
        var timed = service.AuthenticateAsync(port.Trust.Binding, "first"u8.ToArray(), default).AsTask();
        await port.Entered.Task; clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(RuntimeAuthenticationOutcome.Unavailable, (await timed).Outcome); Assert.True(port.Observed.Span.IndexOfAnyExcept((byte)0) < 0);
        port.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var canceled = service.AuthenticateAsync(port.Trust.Binding, "second"u8.ToArray(), cancel.Token).AsTask();
        await port.Entered.Task; cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.True(port.Observed.Span.IndexOfAnyExcept((byte)0) < 0);
        port.Block = false;
        Assert.Equal(RuntimeAuthenticationOutcome.Authenticated, (await service.AuthenticateAsync(port.Trust.Binding, "third"u8.ToArray(), default)).Outcome);
    }

    [Fact]
    public async Task Unsupported_adapter_outcome_is_safe_indeterminate_without_current_authority()
    {
        var clock = new Clock(); using var admission = new RuntimeAuthenticationAdmission(new(), clock);
        var port = new Ports { Outcome = (RuntimeAuthenticationOutcome)999 };
        var result = await new RuntimeAuthentication(port, [port], admission, clock).AuthenticateAsync(port.Trust.Binding, "projected"u8.ToArray(), default);
        Assert.Equal(RuntimeAuthenticationOutcome.Unavailable, result.Outcome);
        Assert.Equal("runtime-authentication-indeterminate", result.Code); Assert.Null(result.Context); Assert.Equal(0, port.Checks);
    }

    [Fact]
    public async Task Final_current_state_check_is_inside_call_deadline_and_cannot_return_late_success()
    {
        var clock = new Clock(); using var admission = new RuntimeAuthenticationAdmission(new(CallTimeoutSeconds: 1), clock);
        var port = new Ports { BlockFinal = true }; var service = new RuntimeAuthentication(port, [port], admission, clock);
        var waiting = service.AuthenticateAsync(port.Trust.Binding, "projected"u8.ToArray(), default).AsTask();
        Assert.Equal(1, port.Checks); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(RuntimeAuthenticationOutcome.Unavailable, (await waiting).Outcome);
        Assert.True(port.Observed.Span.IndexOfAnyExcept((byte)0) < 0);
        port.BlockFinal = false;
        Assert.Equal(RuntimeAuthenticationOutcome.Authenticated, (await service.AuthenticateAsync(port.Trust.Binding, "replacement"u8.ToArray(), default)).Outcome);
    }

    [Fact]
    public async Task Oversized_or_non_ascii_credentials_are_rejected_before_storage_and_duplicate_dispatch_is_rejected()
    {
        var clock = new Clock(); using var admission = new RuntimeAuthenticationAdmission(new(), clock);
        var port = new Ports(); var service = new RuntimeAuthentication(port, [port], admission, clock);
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[16385], new byte[] { 255 }, "raw\ncredential"u8.ToArray() })
            Assert.Equal(RuntimeAuthenticationOutcome.Unauthenticated, (await service.AuthenticateAsync(port.Trust.Binding, bytes, default)).Outcome);
        Assert.Equal(0, port.Reads);
        Assert.Throws<InvalidOperationException>(() => new RuntimeAuthentication(port, [port, port], admission, clock));
    }

    private sealed class Ports : IRuntimeIdentityStore, IRuntimeAuthenticator
    {
        internal RuntimeTrust Trust = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            "implementation", "provider", "configuration", 1, "{}", "authenticator", "audience", "validation", 1, "{}", "inventory", 1, "{}", false);
        internal string Fault = "";
        internal bool Current = true;
        internal bool Block;
        internal bool BlockFinal;
        internal RuntimeAuthenticationOutcome Outcome = RuntimeAuthenticationOutcome.Authenticated;
        internal int Calls, Checks, Reads;
        internal ReadOnlyMemory<byte> Observed;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RuntimeAuthenticatorContract Contract => new("authenticator", "validation", 1, "provider", "configuration", 1, "implementation");
        public bool Validate(RuntimeTrust trust) => Fault != "corrupt";
        public ValueTask<RuntimeTrust?> ReadAsync(Guid binding, CancellationToken token)
        {
            Reads++;
            if (Fault == "unavailable") throw new IOException("private driver detail");
            return ValueTask.FromResult<RuntimeTrust?>(Fault == "unknown" ? null : Trust with
            { Revoked = Fault == "revoked", Authenticator = Fault == "unsupported" ? "other" : Trust.Authenticator });
        }
        public async ValueTask<bool> IsCurrentAsync(RuntimeTrust expected, CancellationToken token)
        { Checks++; if (BlockFinal) await Task.Delay(Timeout.InfiniteTimeSpan, token); return Current; }
        public async ValueTask<RuntimeAuthenticationOutcome> AuthenticateAsync(RuntimeTrust trust, ReadOnlyMemory<byte> credential, CancellationToken token)
        {
            Calls++; Observed = credential; Entered.TrySetResult();
            if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Outcome;
        }
    }

    // A local TimeProvider seam; no external infrastructure or elapsed-time assertions.
    private sealed class Clock : TimeProvider
    {
        private long now;
        private readonly List<Timer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(now);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
        internal void Advance(TimeSpan by)
        {
            now += by.Ticks;
            foreach (var timer in timers.ToArray()) timer.Fire();
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private long due = long.MaxValue, period;
            public bool Change(TimeSpan dueTime, TimeSpan nextPeriod)
            { due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.now + dueTime.Ticks; period = nextPeriod.Ticks; return true; }
            internal void Fire()
            {
                if (due > clock.now) return;
                due = period > 0 ? clock.now + period : long.MaxValue; callback(state);
            }
            public void Dispose() => due = long.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
