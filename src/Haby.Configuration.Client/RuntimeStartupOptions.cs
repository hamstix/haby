namespace Haby.Configuration.Client;

/// <summary>Validated startup snapshot; all documents and attempts share TotalTimeout.</summary>
public sealed record RuntimeStartupOptions
{
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(300);
    public TimeSpan RpcTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaximumBackoff { get; init; } = TimeSpan.FromSeconds(5);
    public int MaximumAggregateBytes { get; init; } = 16 * 1024 * 1024;

    internal void Validate()
    {
        if (TotalTimeout < TimeSpan.FromSeconds(1) || TotalTimeout > TimeSpan.FromHours(1) ||
            RpcTimeout < TimeSpan.FromMilliseconds(100) || RpcTimeout > TimeSpan.FromSeconds(30) || RpcTimeout > TotalTimeout ||
            InitialBackoff < TimeSpan.FromMilliseconds(1) || InitialBackoff > TimeSpan.FromSeconds(5) ||
            MaximumBackoff < InitialBackoff || MaximumBackoff > TimeSpan.FromSeconds(30) ||
            MaximumAggregateBytes is < 1 or > 64 * 1024 * 1024)
            throw new RuntimeStartupException(RuntimeStartupFailure.InvalidBootstrap);
    }
}
