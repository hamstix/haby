namespace Haby.Application;

internal sealed record RuntimeAuthenticationOptions(int GlobalConcurrency = 32, int ProviderConcurrency = 16,
    int GlobalRequestsPerSecond = 50, int ProviderRequestsPerSecond = 25, int QueueCapacity = 256,
    int QueueWaitSeconds = 10, int CallTimeoutSeconds = 5)
{
    internal TimeSpan QueueWait => TimeSpan.FromSeconds(QueueWaitSeconds);
    internal TimeSpan CallTimeout => TimeSpan.FromSeconds(CallTimeoutSeconds);
    internal void Validate()
    {
        if (GlobalConcurrency is < 1 or > 256 || ProviderConcurrency < 1 || ProviderConcurrency > GlobalConcurrency ||
            GlobalRequestsPerSecond is < 1 or > 10000 || ProviderRequestsPerSecond < 1 || ProviderRequestsPerSecond > GlobalRequestsPerSecond ||
            QueueCapacity is < 0 or > 4096 || QueueWaitSeconds is < 1 or > 60 || CallTimeoutSeconds is < 1 or > 30)
            throw new ArgumentException("Invalid runtime authentication limits.");
    }
}
internal sealed class RuntimeAdmissionException : Exception;

/// <summary>One Host owns this scheduler. Waiting for a Provider never holds a global permit.</summary>
internal sealed class RuntimeAuthenticationAdmission : IDisposable
{
    internal const int MaximumProviders = 1024;
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly ITimer timer;
    private readonly LinkedList<Ticket> queue = new();
    private readonly Dictionary<Guid, ProviderState> providers = [];
    private int active;
    private int admitted;
    private bool disposed;
    private double globalTokens;
    private long refillAt;
    internal RuntimeAuthenticationOptions Options { get; }
    private sealed class ProviderState(double tokens, long at)
    {
        internal double Tokens = tokens;
        internal long At = at;
        internal int Active;
        internal int Waiting;
    }
    internal RuntimeAuthenticationAdmission(RuntimeAuthenticationOptions options, TimeProvider clock)
    {
        options.Validate(); Options = options; this.clock = clock;
        globalTokens = options.GlobalRequestsPerSecond; refillAt = clock.GetTimestamp();
        timer = clock.CreateTimer(_ => Pump(), null, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
    }
    internal Ticket? Enter(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (disposed || admitted >= Options.GlobalConcurrency + Options.QueueCapacity) return null;
            var ticket = new Ticket(this, clock.GetTimestamp());
            admitted++;
            try { Enqueue(ticket); }
            catch { admitted--; throw; }
            return ticket;
        }
    }
    private void Refill()
    {
        var now = clock.GetTimestamp();
        globalTokens = Math.Min(Options.GlobalRequestsPerSecond, globalTokens + clock.GetElapsedTime(refillAt, now).TotalSeconds * Options.GlobalRequestsPerSecond);
        refillAt = now;
        foreach (var state in providers.Values)
        {
            state.Tokens = Math.Min(Options.ProviderRequestsPerSecond, state.Tokens + clock.GetElapsedTime(state.At, now).TotalSeconds * Options.ProviderRequestsPerSecond);
            state.At = now;
        }
    }
    private bool Eligible(Ticket ticket) => active < Options.GlobalConcurrency && (ticket.Provider is null ||
        (providers[ticket.Provider.Value].Active < Options.ProviderConcurrency && providers[ticket.Provider.Value].Tokens >= 1 && globalTokens >= 1));
    private void Grant(Ticket ticket)
    {
        active++; ticket.Active = true;
        if (ticket.Provider is { } id)
        {
            var provider = providers[id]; provider.Active++; provider.Tokens--; globalTokens--; ticket.Dispatched = true;
        }
        ticket.Signal.TrySetResult();
    }
    private void Enqueue(Ticket ticket)
    {
        Pump(); // Existing eligible requests take precedence over a new arrival.
        if (Eligible(ticket)) { Grant(ticket); return; }
        if (queue.Count >= Options.QueueCapacity) throw new RuntimeAdmissionException();
        ticket.Node = queue.AddLast(ticket);
        if (ticket.Provider is { } id) providers[id].Waiting++;
    }
    private void Pump()
    {
        lock (gate)
        {
            if (disposed) return;
            Refill();
            var node = queue.First;
            while (node is not null)
            {
                var next = node.Next; var ticket = node.Value;
                if (ticket.Remaining <= TimeSpan.Zero) { RemoveQueued(ticket); ticket.Signal.TrySetException(new RuntimeAdmissionException()); }
                else if (Eligible(ticket)) { RemoveQueued(ticket); Grant(ticket); }
                node = next;
            }
        }
    }
    private void RemoveQueued(Ticket ticket)
    {
        if (ticket.Node is null) return;
        queue.Remove(ticket.Node); ticket.Node = null;
        if (ticket.Provider is { } id) providers[id].Waiting--;
    }
    private void Release(Ticket ticket)
    {
        RemoveQueued(ticket);
        if (!ticket.Active) return;
        active--; ticket.Active = false;
        if (ticket.Provider is { } id) providers[id].Active--;
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            while (queue.First is { } node)
            { RemoveQueued(node.Value); node.Value.Signal.TrySetException(new RuntimeAdmissionException()); }
        }
        timer.Dispose();
    }
    internal sealed class Ticket(RuntimeAuthenticationAdmission owner, long started) : IDisposable
    {
        internal Guid? Provider;
        internal bool Active;
        internal bool Dispatched;
        internal LinkedListNode<Ticket>? Node;
        internal TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool completed;
        internal TimeSpan Remaining => owner.Options.QueueWait - owner.clock.GetElapsedTime(started);
        internal async Task LookupAsync(CancellationToken token) => await WaitAsync(token);
        private async Task WaitAsync(CancellationToken token)
        {
            var left = Remaining;
            if (left <= TimeSpan.Zero) throw new RuntimeAdmissionException();
            try { await Signal.Task.WaitAsync(left, owner.clock, token); }
            catch (TimeoutException) { throw new RuntimeAdmissionException(); }
            token.ThrowIfCancellationRequested();
            if (Remaining <= TimeSpan.Zero) throw new RuntimeAdmissionException();
        }
        internal async Task ProviderAsync(Guid id, CancellationToken token)
        {
            lock (owner.gate)
            {
                owner.Release(this);
                owner.Refill();
                if (owner.disposed || id == Guid.Empty) throw new RuntimeAdmissionException();
                if (!owner.providers.ContainsKey(id))
                {
                    if (owner.providers.Count == MaximumProviders)
                    {
                        var removable = owner.providers.FirstOrDefault(x => x.Value.Active == 0 && x.Value.Waiting == 0 && x.Value.Tokens >= owner.Options.ProviderRequestsPerSecond);
                        if (removable.Key == Guid.Empty) throw new RuntimeAdmissionException();
                        owner.providers.Remove(removable.Key);
                    }
                    owner.providers.Add(id, new(owner.Options.ProviderRequestsPerSecond, owner.clock.GetTimestamp()));
                }
                Provider = id; Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                owner.Enqueue(this);
            }
            owner.Pump();
            await WaitAsync(token);
        }
        public void Dispose()
        {
            lock (owner.gate)
            {
                if (completed) return;
                completed = true; owner.Release(this); owner.admitted--;
            }
            owner.Pump();
        }
    }
}
