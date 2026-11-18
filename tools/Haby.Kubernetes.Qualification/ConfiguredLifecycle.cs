using System.Globalization;

// Fixture-only cooperative phases. The launcher independently caps the whole
// configured child process, including non-token PostgreSQL work, at 240 seconds.
internal sealed class ConfiguredLifecycle : IDisposable
{
    internal enum Kind { Functional, Delivery, Drain, NamespaceCleanup, ReleaseCleanup, NativeCli }
    private readonly TimeProvider clock;
    private readonly Action<string> report;
    private readonly CancellationToken user;
    private readonly CancellationTokenSource cap;
    private readonly CancellationTokenSource overall;
    private readonly long origin;
    private bool complete;
    internal CancellationToken Token => overall.Token;
    internal Phase Functional { get; }
    internal TimeProvider Clock => clock;
    internal TimeSpan Remaining => Positive(TimeSpan.FromSeconds(240) - clock.GetElapsedTime(origin));

    internal ConfiguredLifecycle(CancellationToken user = default, TimeProvider? clock = null, Action<string>? report = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.report = report ?? Console.WriteLine;
        this.user = user;
        origin = this.clock.GetTimestamp();
        cap = new(TimeSpan.FromSeconds(240), this.clock);
        overall = CancellationTokenSource.CreateLinkedTokenSource(user, cap.Token);
        Write("Overall", "Start", TimeSpan.Zero, Remaining, Cause);
        Functional = new(this, Kind.Functional, 150, null);
    }
    private string Cause => user.IsCancellationRequested ? "User" : cap.IsCancellationRequested || Remaining == TimeSpan.Zero ? "Overall" : "None";
    internal Phase Start(Kind kind)
    {
        if (kind == Kind.Functional) throw new InvalidOperationException("Functional budget is established once.");
        if (kind == Kind.Drain && !Functional.Completed) throw new InvalidOperationException("Drain requires successful functional delivery.");
        return new(this, kind, kind switch
        {
            Kind.Delivery => 35,
            Kind.Drain => 45,
            Kind.NamespaceCleanup or Kind.ReleaseCleanup => 20,
            Kind.NativeCli => 30,
            _ => throw new InvalidOperationException("Unknown configured phase.")
        }, kind is Kind.Delivery or Kind.NativeCli ? Functional : null);
    }
    internal void Complete()
    {
        if (!Functional.Completed) throw new InvalidOperationException("Functional delivery was not completed.");
        Token.ThrowIfCancellationRequested();
        if (Remaining == TimeSpan.Zero) throw new OperationCanceledException(Token);
        complete = true;
    }
    internal static Exception Combine(Exception? primary, IReadOnlyCollection<Exception> cleanup) =>
        new AggregateException(primary is null ? cleanup : new[] { primary }.Concat(cleanup));
    private static TimeSpan Positive(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;
    private void Write(string phase, string state, TimeSpan elapsed, TimeSpan remaining, string cause) => report(
        "Q243 configured budget " + phase + " " + state + " " + Milliseconds(elapsed, 3600000) + " " +
        Milliseconds(remaining, 240000) + " " + Milliseconds(Remaining, 240000) + " " + cause);
    private static string Milliseconds(TimeSpan value, long maximum) =>
        Math.Clamp((long)value.TotalMilliseconds, 0, maximum).ToString(CultureInfo.InvariantCulture);
    public void Dispose()
    {
        Functional.Dispose();
        Write("Overall", complete ? "Complete" : "Failed", clock.GetElapsedTime(origin), Remaining, Cause);
        overall.Dispose(); cap.Dispose();
    }

    internal sealed class Phase : IDisposable
    {
        private readonly ConfiguredLifecycle owner;
        private readonly Kind kind;
        private readonly TimeSpan limit;
        private readonly long started;
        private readonly Phase? parent;
        private readonly CancellationTokenSource cap;
        private readonly CancellationTokenSource linked;
        private bool disposed;
        private bool failed;
        private string incomplete = "None";
        internal bool Completed { get; private set; }
        internal CancellationToken Token { get; }
        internal TimeSpan Remaining => Positive(new[] { limit - owner.clock.GetElapsedTime(started), owner.Remaining,
            parent?.Remaining ?? limit }.Min());
        internal string Cause => owner.Cause != "None" ? owner.Cause : parent is not null && parent.Remaining == TimeSpan.Zero ? "Functional" :
            cap.IsCancellationRequested || limit - owner.clock.GetElapsedTime(started) <= TimeSpan.Zero ? "Phase" : incomplete;
        internal Phase(ConfiguredLifecycle owner, Kind kind, int seconds, Phase? parent)
        {
            this.owner = owner; this.kind = kind; this.parent = parent;
            limit = TimeSpan.FromSeconds(seconds); started = owner.clock.GetTimestamp();
            cap = new(limit, owner.clock);
            linked = CancellationTokenSource.CreateLinkedTokenSource(owner.Token, cap.Token, parent?.Token ?? default);
            Token = linked.Token;
            Write("Start");
        }
        internal void Complete()
        {
            Token.ThrowIfCancellationRequested();
            if (Remaining == TimeSpan.Zero) throw new OperationCanceledException(Token);
            Completed = true; Write("Complete"); cap.Dispose();
        }
        internal void NoteIncomplete(string reason) => incomplete = reason is "Output" or "Termination" ? reason : throw new InvalidOperationException("Unknown CLI failure.");
        internal void Fail()
        {
            if (Completed || failed) return;
            failed = true; Write("Failed"); cap.Dispose();
        }
        private void Write(string state) => owner.Write(kind.ToString(), state, owner.clock.GetElapsedTime(started), Remaining, Cause);
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Fail();
            linked.Dispose(); cap.Dispose();
        }
    }
}
