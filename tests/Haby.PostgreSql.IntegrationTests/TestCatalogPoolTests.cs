using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class TestCatalogPoolTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Catalog_pool_reuses_reset_sessions_and_bounds_waiters()
    {
        var database = await Database();
        using (var pools = new TestCatalogPools())
        {
            var value = pools.Register(new NpgsqlConnectionStringBuilder(database));
            int pid;
            await using (var first = new NpgsqlConnection(value))
            {
                await first.OpenAsync(); pid = first.ProcessID;
                await new NpgsqlCommand("SET application_name = 'test-only-state'; CREATE TEMP TABLE pool_state(id integer)", first).ExecuteNonQueryAsync();
            }
            await using (var next = new NpgsqlConnection(value))
            {
                await next.OpenAsync();
                Assert.Equal(pid, next.ProcessID);
                Assert.Equal("", await new NpgsqlCommand("SHOW application_name", next).ExecuteScalarAsync());
                Assert.Equal(true, await new NpgsqlCommand("SELECT to_regclass('pg_temp.pool_state') IS NULL", next).ExecuteScalarAsync());
            }
            var held = new List<NpgsqlConnection>();
            try
            {
                for (var i = 0; i < TestCatalogPools.MaximumPoolSize; i++)
                {
                    var session = new NpgsqlConnection(value); held.Add(session); await session.OpenAsync();
                }
                await using var extra = new NpgsqlConnection(value);
                using var cancellation = new CancellationTokenSource();
                var waiting = extra.OpenAsync(cancellation.Token);
                Assert.False(waiting.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            }
            finally { foreach (var session in held) await session.DisposeAsync(); }
        }
        Assert.Equal(0L, await Sessions(database));
    }

    [Fact]
    public async Task Cleanup_closes_owned_idle_and_returned_sessions_without_clearing_another_pool()
    {
        var firstDatabase = await Database(); var secondDatabase = await Database();
        using var firstPools = new TestCatalogPools(); using var secondPools = new TestCatalogPools();
        var firstValue = firstPools.Register(new NpgsqlConnectionStringBuilder(firstDatabase));
        var secondValue = secondPools.Register(new NpgsqlConnectionStringBuilder(secondDatabase));
        int secondPid;
        await using (var first = new NpgsqlConnection(firstValue))
        {
            await first.OpenAsync();
            await using (var second = new NpgsqlConnection(secondValue)) { await second.OpenAsync(); secondPid = second.ProcessID; }
            firstPools.Dispose(); // The borrowed connector closes when returned below.
        }
        Assert.Equal(0L, await Sessions(firstDatabase));
        await using (var second = new NpgsqlConnection(secondValue))
        {
            await second.OpenAsync(); Assert.Equal(secondPid, second.ProcessID);
        }
        secondPools.Dispose();
        Assert.Equal(0L, await Sessions(secondDatabase));
    }

    [Fact]
    public void Pool_budget_and_disposed_owner_refuse_more_work()
    {
        using var pools = new TestCatalogPools();
        var prefix = "pool_budget_" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < TestCatalogPools.MaximumPools; i++) pools.Register(new() { Host = "localhost", Database = prefix + i });
        Assert.Throws<InvalidOperationException>(() => pools.Register(new() { Host = "localhost", Database = prefix + "_over" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => pools.Register(new(), maximumPoolSize: 9));
        pools.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pools.Register(new()));
    }

    private async Task<string> Database()
    {
        var name = "pool_guard_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    private async Task<long> Sessions(string database)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = $1", connection);
        command.Parameters.AddWithValue(new NpgsqlConnectionStringBuilder(database).Database!);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
