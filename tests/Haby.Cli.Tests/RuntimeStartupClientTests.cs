using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Haby.Configuration.Client;
using Haby.Configuration.Contracts;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class RuntimeStartupClientTests
{
    private static readonly string Binding = Guid.NewGuid().ToString();
    private static string Revision() => Guid.CreateVersion7().ToString();
    private const string Fingerprint = "sha256:configuration-revision-v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static DeliverConfigurationReply Reply(string revision, string json = "{\"number\":123456789012345678901234567890.00001}") =>
        new() { RevisionId = revision, SourceFingerprint = Fingerprint, Json = ByteString.CopyFromUtf8(json) };
    private static RpcException Refused(StatusCode code, params string[] reasons)
    {
        var trailers = new Metadata();
        foreach (var reason in reasons) trailers.Add(RuntimeDeliveryReasons.Header, reason);
        return new(new(code, "private-transport-sentinel"), trailers);
    }

    [Fact]
    public async Task Complete_ordered_set_preserves_raw_numbers_and_owns_clearable_buffers_without_owning_channel()
    {
        var ids = new[] { Revision(), Revision() }; var calls = new List<string>(); var reads = new List<byte[]>();
        var clock = new Clock();
        var client = new RuntimeStartupClient((request, headers, deadline, token) =>
        {
            Assert.Equal(Binding, request.BindingId); Assert.Equal("Bearer projected", Assert.Single(headers).Value);
            Assert.Equal(clock.GetUtcNow().UtcDateTime.AddSeconds(30), deadline);
            calls.Add(request.RevisionId); return Task.FromResult(Reply(request.RevisionId));
        }, clock, () => 0, (_, _) => { var bytes = "projected"u8.ToArray(); reads.Add(bytes); return Task.FromResult(bytes); });
        var result = await client.FetchAsync(Binding, ids, "credential");
        Assert.Equal(ids, calls); Assert.Equal(2, reads.Count); Assert.All(reads, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        var memory = result.Documents.Select(x => x.Json).ToArray();
        Assert.All(memory, bytes => Assert.Equal(Reply(ids[0]).Json.ToByteArray(), bytes.ToArray()));
        Assert.DoesNotContain("number", result.ToString()); Assert.DoesNotContain("number", result.Documents[0].ToString());
        result.Dispose(); result.Dispose();
        Assert.All(memory, bytes => Assert.True(bytes.Span.IndexOfAnyExcept((byte)0) < 0));
        Assert.Throws<ObjectDisposedException>(() => result.Documents);
        using var restarted = await client.FetchAsync(Binding, ids, "credential");
        Assert.Equal(ids.Concat(ids), calls); Assert.Equal(4, reads.Count);
    }

    [Fact]
    public async Task Temporary_statuses_retry_with_fresh_tokens_and_terminal_statuses_never_parse_raw_messages()
    {
        var revision = Revision();
        foreach (var temporary in new[] { Refused(StatusCode.Unavailable), Refused(StatusCode.DeadlineExceeded), Refused(StatusCode.ResourceExhausted, RuntimeDeliveryReasons.AdmissionOverload) })
        {
            var calls = 0; var reads = 0;
            var client = new RuntimeStartupClient((request, headers, _, _) =>
            {
                Assert.Equal("Bearer token" + reads, Assert.Single(headers).Value);
                if (++calls == 1) throw temporary;
                return Task.FromResult(Reply(request.RevisionId));
            }, new Clock(), () => 0, (_, _) => Task.FromResult(Encoding.ASCII.GetBytes("token" + ++reads)));
            using var result = await client.FetchAsync(Binding, [revision], "credential");
            Assert.Equal(2, calls); Assert.Equal(2, reads);
        }
        foreach (var terminal in new[]
        {
            Refused(StatusCode.Unauthenticated), Refused(StatusCode.PermissionDenied), Refused(StatusCode.FailedPrecondition),
            Refused(StatusCode.ResourceExhausted), Refused(StatusCode.ResourceExhausted, "unknown"),
            Refused(StatusCode.ResourceExhausted, RuntimeDeliveryReasons.OutputLimit),
            Refused(StatusCode.ResourceExhausted, RuntimeDeliveryReasons.AdmissionOverload, RuntimeDeliveryReasons.AdmissionOverload),
            Refused(StatusCode.ResourceExhausted, RuntimeDeliveryReasons.AdmissionOverload, RuntimeDeliveryReasons.OutputLimit),
            Refused(StatusCode.Cancelled), Refused(StatusCode.Internal)
        })
        {
            var calls = 0;
            var client = new RuntimeStartupClient((_, _, _, _) => { calls++; throw terminal; }, new Clock(), () => 0,
                (_, _) => Task.FromResult("projected"u8.ToArray()));
            var failure = await Assert.ThrowsAsync<RuntimeStartupException>(() => client.FetchAsync(Binding, [revision], "credential"));
            Assert.Equal(1, calls); Assert.Null(failure.InnerException); Assert.DoesNotContain("sentinel", failure.ToString());
        }
        var binary = new RpcException(new(StatusCode.ResourceExhausted, "withheld"), new Metadata { { RuntimeDeliveryReasons.Header + "-bin", new byte[] { 1 } } });
        Assert.False(RuntimeStartupClient.Retryable(binary));
        var conflicting = new RpcException(new(StatusCode.ResourceExhausted, "withheld"), new Metadata
        { { RuntimeDeliveryReasons.Header, RuntimeDeliveryReasons.AdmissionOverload }, { RuntimeDeliveryReasons.Header + "-bin", new byte[] { 1 } } });
        Assert.False(RuntimeStartupClient.Retryable(conflicting));
    }

    [Fact]
    public async Task Later_refusal_or_cancellation_clears_earlier_owned_bytes_without_a_partial_result()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var caller = new CancellationTokenSource(); var calls = 0;
            var owned = new List<ReadOnlyMemory<byte>>(); var primary = false;
            var client = new RuntimeStartupClient((request, _, _, _) =>
            {
                if (++calls == 2)
                {
                    if (cancel) caller.Cancel();
                    throw Refused(StatusCode.PermissionDenied);
                }
                return Task.FromResult(Reply(request.RevisionId));
            }, new Clock(), () => 0, (_, _) => Task.FromResult("projected"u8.ToArray()))
            { DocumentAccepted = document => owned.Add(document.Json) };
            async Task Startup()
            {
                using var complete = await client.FetchAsync(Binding, [Revision(), Revision()], "credential", cancellationToken: caller.Token);
                primary = true;
            }
            var failure = await Assert.ThrowsAsync<RuntimeStartupException>(Startup);
            Assert.Equal(cancel ? RuntimeStartupFailure.CallerCancelled : RuntimeStartupFailure.AccessDenied, failure.Failure);
            Assert.False(primary); Assert.Single(owned); Assert.True(owned[0].Span.IndexOfAnyExcept((byte)0) < 0);
        }
    }

    [Fact]
    public void Full_jitter_and_saturating_exponential_growth_have_executable_endpoints_and_overflow_bounds()
    {
        var current = TimeSpan.FromMilliseconds(250); var maximum = TimeSpan.FromSeconds(5);
        foreach (var expected in new[] { 250, 500, 1000, 2000, 4000, 5000, 5000 })
        {
            Assert.Equal(TimeSpan.FromMilliseconds(expected), current);
            Assert.Equal(TimeSpan.Zero, RuntimeStartupClient.Jitter(current, 0));
            Assert.Equal(current, RuntimeStartupClient.Jitter(current, 1));
            Assert.Equal(current.Ticks / 2, RuntimeStartupClient.Jitter(current, 0.5).Ticks);
            current = RuntimeStartupClient.Grow(current, maximum);
        }
        Assert.Equal(TimeSpan.MaxValue, RuntimeStartupClient.Grow(TimeSpan.FromTicks(long.MaxValue - 1), TimeSpan.MaxValue));
        Assert.Equal(TimeSpan.FromTicks(4), RuntimeStartupClient.Grow(TimeSpan.FromTicks(2), TimeSpan.FromTicks(5)));
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -0.1, 1.1 })
            Assert.Throws<RuntimeStartupException>(() => RuntimeStartupClient.Jitter(maximum, invalid));
    }

    [Fact]
    public async Task Total_budget_includes_reads_queue_rpc_and_previous_documents_and_caps_next_rpc_remaining_time()
    {
        var clock = new Clock(); var ids = new[] { Revision(), Revision() }; var calls = 0; var reads = 0;
        var client = new RuntimeStartupClient((request, _, deadline, _) =>
        {
            calls++;
            Assert.Equal(clock.GetUtcNow().UtcDateTime.AddSeconds(calls == 1 ? 2 : 1), deadline);
            clock.Advance(TimeSpan.FromMilliseconds(calls == 1 ? 200 : 1000));
            return Task.FromResult(Reply(request.RevisionId));
        }, clock, () => 0, (_, _) =>
        {
            reads++; clock.Advance(TimeSpan.FromMilliseconds(reads == 1 ? 1000 : 800));
            return Task.FromResult("projected"u8.ToArray());
        });
        var failure = await Assert.ThrowsAsync<RuntimeStartupException>(() => client.FetchAsync(Binding, ids, "credential",
            new() { TotalTimeout = TimeSpan.FromSeconds(3), RpcTimeout = TimeSpan.FromSeconds(2) }));
        Assert.Equal(RuntimeStartupFailure.StartupDeadlineExceeded, failure.Failure); Assert.Equal(2, calls); Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Delay_consumes_same_deadline_and_wall_clock_jumps_do_not_reset_monotonic_budget()
    {
        var clock = new Clock(); var calls = 0; var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.TimerCreated = due => { if (due == TimeSpan.FromMilliseconds(250)) delayEntered.TrySetResult(); };
        var client = new RuntimeStartupClient((_, _, _, _) =>
        {
            calls++; clock.UtcOffset = TimeSpan.FromDays(-7); throw Refused(StatusCode.Unavailable);
        }, clock, () => 1, (_, _) => Task.FromResult("projected"u8.ToArray()));
        var fetch = client.FetchAsync(Binding, [Revision()], "credential", new() { TotalTimeout = TimeSpan.FromSeconds(1), RpcTimeout = TimeSpan.FromSeconds(1) });
        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(RuntimeStartupFailure.StartupDeadlineExceeded, (await Assert.ThrowsAsync<RuntimeStartupException>(() => fetch)).Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Expired_file_read_never_dispatches_and_local_file_failures_keep_only_safe_classifications()
    {
        foreach (var kind in new[] { "expired", "missing", "malformed", "unreadable" })
        {
            var clock = new Clock(); var calls = 0;
            var client = new RuntimeStartupClient((request, _, _, _) => { calls++; return Task.FromResult(Reply(request.RevisionId)); }, clock, () => 0, (_, _) =>
            {
                if (kind == "missing") throw new FileNotFoundException("private-path-sentinel");
                if (kind == "malformed") throw new InvalidDataException("private-token-sentinel");
                if (kind == "unreadable") throw new UnauthorizedAccessException("private-path-sentinel");
                clock.Advance(TimeSpan.FromSeconds(1)); return Task.FromResult("projected"u8.ToArray());
            });
            var error = await Assert.ThrowsAsync<RuntimeStartupException>(() => client.FetchAsync(Binding, [Revision()], "credential",
                new() { TotalTimeout = TimeSpan.FromSeconds(1), RpcTimeout = TimeSpan.FromSeconds(1) }));
            Assert.Equal(kind == "expired" ? RuntimeStartupFailure.StartupDeadlineExceeded : kind == "malformed" ? RuntimeStartupFailure.InvalidCredential :
                RuntimeStartupFailure.CredentialUnavailable, error.Failure);
            Assert.Null(error.InnerException); Assert.DoesNotContain("sentinel", error.ToString()); Assert.Equal(0, calls);
        }
    }

    [Fact]
    public async Task Individual_rpc_timeout_retries_but_caller_cancellation_wins_total_deadline_tie()
    {
        var clock = new Clock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var client = new RuntimeStartupClient(async (request, _, _, token) =>
        {
            if (++calls == 1) { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return Reply(request.RevisionId);
        }, clock, () => 0, (_, _) => Task.FromResult("projected"u8.ToArray()));
        var fetch = client.FetchAsync(Binding, [Revision()], "credential", new() { TotalTimeout = TimeSpan.FromSeconds(3), RpcTimeout = TimeSpan.FromSeconds(1) });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(TimeSpan.FromSeconds(1));
        using var result = await fetch.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, calls);
        using var caller = new CancellationTokenSource();
        var tieClock = new Clock();
        var tie = new RuntimeStartupClient((_, _, _, _) =>
        {
            tieClock.Advance(TimeSpan.FromSeconds(1)); caller.Cancel(); throw Refused(StatusCode.DeadlineExceeded);
        }, tieClock, () => 0, (_, _) => Task.FromResult("projected"u8.ToArray()));
        Assert.Equal(RuntimeStartupFailure.CallerCancelled, (await Assert.ThrowsAsync<RuntimeStartupException>(() =>
            tie.FetchAsync(Binding, [Revision()], "credential", new() { TotalTimeout = TimeSpan.FromSeconds(1), RpcTimeout = TimeSpan.FromSeconds(1) }, caller.Token))).Failure);
    }

    [Fact]
    public async Task Owned_total_timer_cancellation_is_deadline_failure_even_before_next_elapsed_timestamp()
    {
        var clock = new Clock(); var calls = 0;
        var client = new RuntimeStartupClient((_, _, _, token) =>
        {
            calls++; clock.CancelOwnedTimers(); throw new OperationCanceledException(token);
        }, clock, () => 0, (_, _) => Task.FromResult("projected"u8.ToArray()));
        var error = await Assert.ThrowsAsync<RuntimeStartupException>(() => client.FetchAsync(Binding, [Revision()], "credential"));
        Assert.Equal(RuntimeStartupFailure.StartupDeadlineExceeded, error.Failure); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cancellation_at_file_rpc_delay_and_later_document_never_enters_primary_work()
    {
        foreach (var stage in new[] { "file", "rpc", "delay", "validation", "second" })
        {
            using var caller = new CancellationTokenSource(); var clock = new Clock(); var calls = 0; var primary = false;
            var client = new RuntimeStartupClient((request, _, _, _) =>
            {
                calls++;
                if (stage == "rpc" || stage == "second" && calls == 2) caller.Cancel();
                if (stage == "delay") throw Refused(StatusCode.Unavailable);
                return Task.FromResult(Reply(request.RevisionId));
            }, clock, () => { caller.Cancel(); return 1; }, (_, _) =>
            {
                if (stage == "file") caller.Cancel();
                return Task.FromResult("projected"u8.ToArray());
            })
            { DocumentAccepted = _ => { if (stage == "validation") caller.Cancel(); } };
            async Task Startup()
            {
                using var complete = await client.FetchAsync(Binding, [Revision(), Revision()], "credential", cancellationToken: caller.Token);
                primary = true;
            }
            Assert.Equal(RuntimeStartupFailure.CallerCancelled, (await Assert.ThrowsAsync<RuntimeStartupException>(Startup)).Failure);
            Assert.False(primary); Assert.Equal(stage == "file" ? 0 : stage == "second" ? 2 : 1, calls);
        }
    }

    [Fact]
    public async Task Unsettled_file_read_has_finite_caller_wait_no_retry_and_late_owned_bytes_are_cleared()
    {
        var clock = new Clock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<byte[]>(); var reads = 0; var calls = 0;
        var client = new RuntimeStartupClient((request, _, _, _) => { calls++; return Task.FromResult(Reply(request.RevisionId)); }, clock, () => 0,
            (_, _) => { reads++; entered.TrySetResult(); return release.Task; });
        var fetch = client.FetchAsync(Binding, [Revision()], "credential", new() { TotalTimeout = TimeSpan.FromSeconds(1), RpcTimeout = TimeSpan.FromSeconds(1) });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(RuntimeStartupFailure.StartupDeadlineExceeded, (await Assert.ThrowsAsync<RuntimeStartupException>(() => fetch)).Failure);
        var bytes = "late-owned-token"u8.ToArray(); release.SetResult(bytes);
        Assert.True(SpinWait.SpinUntil(() => bytes.All(x => x == 0), TimeSpan.FromSeconds(5)));
        Assert.Equal(1, reads); Assert.Equal(0, calls);
    }

    [Fact]
    public void Response_validation_preserves_raw_json_and_refuses_wrong_identity_fingerprint_utf8_depth_sizes_and_trailing_content()
    {
        var revision = Revision();
        RuntimeStartupClient.ValidateReply(Reply(revision), revision, 1024, () => { });
        var invalid = new[]
        {
            Reply(Revision()), new DeliverConfigurationReply { RevisionId = revision, Json = ByteString.CopyFromUtf8("{}") },
            Reply(revision, "{broken}"), Reply(revision, "{}{}"), Reply(revision, ""), Reply(revision, "/*comment*/{}"),
            Reply(revision, new string('[', 65) + "0" + new string(']', 65)),
            new DeliverConfigurationReply { RevisionId = revision, SourceFingerprint = Fingerprint, Json = ByteString.CopyFrom([34, 255, 34]) },
            Reply(revision)
        };
        // Mutate only the last independent reply's safe metadata to exercise the closed fingerprint format.
        invalid[^1].SourceFingerprint = Fingerprint.ToUpperInvariant();
        foreach (var reply in invalid)
            Assert.Equal(RuntimeStartupFailure.InvalidResponse, Assert.Throws<RuntimeStartupException>(() =>
                RuntimeStartupClient.ValidateReply(reply, revision, int.MaxValue, () => { })).Failure);
        Assert.Equal(RuntimeStartupFailure.OutputLimit, Assert.Throws<RuntimeStartupException>(() =>
            RuntimeStartupClient.ValidateReply(Reply(revision), revision, 1, () => { })).Failure);
        var oversized = Reply(revision); oversized.Json = ByteString.CopyFrom(new byte[4 * 1024 * 1024 + 1]);
        Assert.Equal(RuntimeStartupFailure.OutputLimit, Assert.Throws<RuntimeStartupException>(() =>
            RuntimeStartupClient.ValidateReply(oversized, revision, int.MaxValue, () => { })).Failure);
        var protobuf = Reply(revision); protobuf.SourceFingerprint = new string('a', 8 * 1024 * 1024);
        Assert.Equal(RuntimeStartupFailure.OutputLimit, Assert.Throws<RuntimeStartupException>(() =>
            RuntimeStartupClient.ValidateReply(protobuf, revision, int.MaxValue, () => { })).Failure);
    }

    [Fact]
    public async Task Invalid_bootstrap_and_ranges_fail_before_file_or_rpc_and_aggregate_failure_is_terminal()
    {
        var reads = 0; var client = new RuntimeStartupClient((request, _, _, _) => Task.FromResult(Reply(request.RevisionId, "{}")),
            new Clock(), () => 0, (_, _) => { reads++; return Task.FromResult("projected"u8.ToArray()); });
        foreach (var options in new[]
        {
            new RuntimeStartupOptions { TotalTimeout = TimeSpan.Zero }, new() { TotalTimeout = TimeSpan.FromHours(2) },
            new() { RpcTimeout = TimeSpan.FromMilliseconds(99) }, new() { RpcTimeout = TimeSpan.FromSeconds(31) },
            new() { TotalTimeout = TimeSpan.FromSeconds(1) }, new() { InitialBackoff = TimeSpan.Zero },
            new() { MaximumBackoff = TimeSpan.FromMilliseconds(1) }, new() { MaximumBackoff = TimeSpan.FromSeconds(31) },
            new() { MaximumAggregateBytes = 0 }, new() { MaximumAggregateBytes = 64 * 1024 * 1024 + 1 }
        })
            Assert.Equal(RuntimeStartupFailure.InvalidBootstrap, (await Assert.ThrowsAsync<RuntimeStartupException>(() =>
                client.FetchAsync(Binding, [Revision()], "credential", options))).Failure);
        var duplicate = Revision();
        foreach (var ids in new[] { Array.Empty<string>(), new[] { "not-an-id" }, new[] { duplicate, duplicate }, Enumerable.Range(0, 65).Select(_ => Revision()).ToArray() })
            Assert.Equal(RuntimeStartupFailure.InvalidBootstrap, (await Assert.ThrowsAsync<RuntimeStartupException>(() =>
                client.FetchAsync(Binding, ids, "credential"))).Failure);
        Assert.Equal(0, reads);
        Assert.Equal(RuntimeStartupFailure.OutputLimit, (await Assert.ThrowsAsync<RuntimeStartupException>(() =>
            client.FetchAsync(Binding, [Revision(), Revision()], "credential", new() { MaximumAggregateBytes = 3 }))).Failure);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task File_reader_enforces_ASCII_terminator_bounds_and_observes_atomic_and_symlink_replacement()
    {
        var folder = Path.Combine(Path.GetTempPath(), "haby221-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "projected");
        try
        {
            foreach (var text in new[] { "a", "abc\n", "abc\r\n", new string('a', 16384) + "\r\n" })
            {
                await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
                var bytes = await ProjectedCredentialFile.ReadAsync(path, default);
                Assert.Equal(text.TrimEnd('\r', '\n'), Encoding.ASCII.GetString(bytes)); Array.Clear(bytes);
            }
            foreach (var text in new[] { "", "\n", "abc\r", "abc\n\n", "abc\r\n\n", "a b", "a\0b", "ü", "\uFEFFabc", new string('a', 16385), new string('a', 65536) })
            {
                await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
                await Assert.ThrowsAsync<InvalidDataException>(() => ProjectedCredentialFile.ReadAsync(path, default));
            }
            await File.WriteAllTextAsync(path, "first");
            Assert.Equal("first", Encoding.ASCII.GetString(await ProjectedCredentialFile.ReadAsync(path, default)));
            var replacement = Path.Combine(folder, "new"); await File.WriteAllTextAsync(replacement, "second"); File.Move(replacement, path, true);
            Assert.Equal("second", Encoding.ASCII.GetString(await ProjectedCredentialFile.ReadAsync(path, default)));
            if (!OperatingSystem.IsWindows())
            {
                var link = Path.Combine(folder, "link"); File.CreateSymbolicLink(link, path);
                Assert.Equal("second", Encoding.ASCII.GetString(await ProjectedCredentialFile.ReadAsync(link, default)));
                await File.WriteAllTextAsync(replacement, "third"); var newLink = Path.Combine(folder, "new-link"); File.CreateSymbolicLink(newLink, replacement);
                File.Move(newLink, link, true);
                Assert.Equal("third", Encoding.ASCII.GetString(await ProjectedCredentialFile.ReadAsync(link, default)));
            }
            await Assert.ThrowsAsync<FileNotFoundException>(() => ProjectedCredentialFile.ReadAsync(Path.Combine(folder, "absent"), default));
            await Assert.ThrowsAsync<InvalidDataException>(() => ProjectedCredentialFile.ReadAsync(folder, default));
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Clock : TimeProvider
    {
        private long now;
        private readonly object sync = new();
        private readonly List<Timer> timers = [];
        internal TimeSpan UtcOffset;
        internal Action<TimeSpan>? TimerCreated;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref now);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp()).Add(UtcOffset);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            lock (sync) timers.Add(timer);
            timer.Change(dueTime, period); TimerCreated?.Invoke(dueTime); return timer;
        }
        internal void Advance(TimeSpan by)
        {
            Interlocked.Add(ref now, by.Ticks); Timer[] snapshot;
            lock (sync) snapshot = timers.ToArray();
            foreach (var timer in snapshot) timer.Fire();
        }
        internal void CancelOwnedTimers()
        {
            Timer[] snapshot;
            lock (sync) snapshot = timers.ToArray();
            foreach (var timer in snapshot) timer.FireNow();
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private long due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { Interlocked.Exchange(ref due, dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks); return true; }
            internal void Fire()
            {
                var expected = Interlocked.Read(ref due);
                if (expected <= clock.GetTimestamp() && Interlocked.CompareExchange(ref due, long.MaxValue, expected) == expected) callback(state);
            }
            internal void FireNow()
            {
                if (Interlocked.Exchange(ref due, long.MaxValue) != long.MaxValue) callback(state);
            }
            public void Dispose() => Interlocked.Exchange(ref due, long.MaxValue);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
