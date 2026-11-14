namespace Haby.Operator.Host;

internal sealed record RuntimeDeliveryOptions(int RequestTimeoutSeconds = 30, int MaxConcurrentRequests = 32)
{
    internal void Validate()
    {
        if (RequestTimeoutSeconds is < 1 or > 30 || MaxConcurrentRequests is < 1 or > 256)
            throw new ArgumentException("Invalid runtime delivery limits.");
    }
    internal static RuntimeDeliveryOptions Read(IConfiguration configuration)
    {
        int Setting(string name, int fallback)
        {
            var value = configuration["Haby:RuntimeDelivery:" + name];
            if (value is null) return fallback;
            if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                throw new ArgumentException("Invalid runtime delivery limits.");
            return parsed;
        }
        var options = new RuntimeDeliveryOptions(Setting(nameof(RequestTimeoutSeconds), 30), Setting(nameof(MaxConcurrentRequests), 32));
        options.Validate(); return options;
    }
}

/// <summary>Nonwaiting request cap, held until the complete protobuf copy has been prepared.</summary>
internal sealed class RuntimeDeliveryLifetime : IDisposable
{
    private readonly SemaphoreSlim permits;
    internal RuntimeDeliveryOptions Options { get; }
    internal RuntimeDeliveryLifetime(RuntimeDeliveryOptions options)
    {
        options.Validate(); Options = options;
        permits = new(options.MaxConcurrentRequests, options.MaxConcurrentRequests);
    }
    internal IDisposable? Enter() => permits.Wait(0) ? new Lease(permits) : null;
    private sealed class Lease(SemaphoreSlim permits) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) permits.Release(); }
    }
    public void Dispose() => permits.Dispose();
}
