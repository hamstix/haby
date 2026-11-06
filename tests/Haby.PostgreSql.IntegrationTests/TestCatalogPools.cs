using Npgsql;

namespace Haby.PostgreSql.IntegrationTests;

// One owner per xUnit case, never a fixture-wide or process-wide pool.
// Provider/application authentication connections remain non-pooled.
internal sealed class TestCatalogPools : IDisposable
{
    internal const int MaximumPoolSize = 8;
    internal const int MaximumPools = 4;
    private readonly HashSet<string> _connections = new(StringComparer.Ordinal);
    private bool _disposed;

    public string Register(NpgsqlConnectionStringBuilder builder, int maximumPoolSize = MaximumPoolSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPoolSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumPoolSize, MaximumPoolSize);
        builder.Pooling = true;
        builder.MinPoolSize = 0;
        builder.MaxPoolSize = maximumPoolSize;
        builder.NoResetOnClose = false;
        var connection = builder.ConnectionString;
        if (!_connections.Contains(connection) && _connections.Count == MaximumPools)
            throw new InvalidOperationException("The per-case catalog pool budget was exceeded.");
        _connections.Add(connection);
        return connection;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var value in _connections)
        {
            using var connection = new NpgsqlConnection(value);
            // Closes idle sessions now, and borrowed sessions on return. Other
            // cases' pools must never be cleared by global ClearAllPools.
            NpgsqlConnection.ClearPool(connection);
        }
        _connections.Clear();
    }
}
