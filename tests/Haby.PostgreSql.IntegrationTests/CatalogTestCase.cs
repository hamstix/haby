using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class CatalogServerFixture : PostgreSqlFixture
{
    public CatalogServerFixture() { }
    internal CatalogServerFixture(bool control) : base(control) { }
}

// One xUnit case owns its databases. All services/connections/data sources in the
// test body are disposed before xUnit calls this cleanup; no shared pools exist.
public class CatalogTestCase(CatalogServerFixture server) : IAsyncLifetime
{
    private protected readonly CatalogServerFixture fixture = server;
    private readonly List<string> _databases = [];
    public Task InitializeAsync() => Task.CompletedTask;

    protected internal async Task<string> CreateDatabaseAsync(string prefix)
    {
        if (prefix.Any(c => c is not (>= 'a' and <= 'z') and not '_')) throw new ArgumentException("Use a fixed safe prefix.", nameof(prefix));
        var name = prefix + Guid.NewGuid().ToString("N");
        // Register before dispatch: a lost create response must still be reconciled.
        _databases.Add(name);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        PostgreSqlResources.DatabaseCreated();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        var errors = new List<Exception>();
        foreach (var name in _databases)
        {
            try
            {
                await using var connection = new NpgsqlConnection(fixture.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\"", connection);
                // Do not FORCE/terminate other sessions: leaked sessions must fail cleanup.
                await command.ExecuteNonQueryAsync();
                PostgreSqlResources.DatabaseDropped();
            }
            catch (Exception error) { errors.Add(error); }
        }
        _databases.Clear();
        // xUnit retains test-body failure separately from this cleanup aggregate.
        if (errors.Count > 0) throw new AggregateException("Owned catalog database cleanup failed.", errors);
    }
}
