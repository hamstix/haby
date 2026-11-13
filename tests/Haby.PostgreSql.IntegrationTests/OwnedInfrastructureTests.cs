using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

[Collection("Owned infrastructure controls")]
public sealed class OwnedInfrastructureTests
{
    public OwnedInfrastructureTests()
    {
        if (Environment.GetEnvironmentVariable("HABY_POSTGRESQL_EPHEMERAL_EXTERNAL") == "1")
            throw new InvalidOperationException("Owned resource controls require the Linux Docker entrypoint; external shared-server mode cannot prove isolation.");
    }
    [Fact]
    public async Task Real_servers_enforce_capacity_and_isolate_global_refusal_from_unrelated_role()
    {
        Assert.Equal(0, PostgreSqlResources.Active);
        var servers = Enumerable.Range(0, PostgreSqlResources.Limit).Select(_ => new PostgreSqlFixture(control: true)).ToArray();
        var overflow = new PostgreSqlFixture(control: true);
        await RunOwned(async () =>
        {
            await Task.WhenAll(servers.Select(s => s.InitializeAsync()));
            Assert.Equal(PostgreSqlResources.Limit, PostgreSqlResources.Active);
            var identities = await Task.WhenAll(servers.Select(s => Scalar(s.ConnectionString, "SELECT system_identifier::text FROM pg_control_system()")));
            Assert.Equal(servers.Length, identities.Distinct().Count());
            var capacity = await Assert.ThrowsAsync<InvalidOperationException>(() => overflow.InitializeAsync());
            Assert.Equal("Docker fixture capacity exceeded; check collection ownership.", capacity.Message);

            await Scalar(servers[0].ConnectionString, "CREATE ROLE haby_a_unrelated_sentinel NOLOGIN");
            // Same strict global absence oracle as PairPersistence: database names
            // alone would not isolate the unrelated role on a shared server.
            async Task RefusalAbsence(string connection) => Assert.Equal(0L,
                await Scalar(connection, "SELECT count(*) FROM pg_roles WHERE rolname LIKE 'haby_a_%'"));
            await RefusalAbsence(servers[1].ConnectionString);
            var brokenIsolation = await Record.ExceptionAsync(() => RefusalAbsence(servers[0].ConnectionString));
            Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(brokenIsolation);
        }, () => DisposeEvery([.. servers, overflow]));
        Assert.Equal(0, PostgreSqlResources.Active);
    }

    [Fact]
    public async Task Real_shared_server_uses_distinct_case_databases_and_removes_each_after_sessions_close()
    {
        var server = new CatalogServerFixture(control: true);
        await server.InitializeAsync();
        await RunOwned(async () =>
        {
            var first = new CatalogTestCase(server); var second = new CatalogTestCase(server);
            string left = "", right = "";
            await RunOwned(async () =>
            {
                left = await first.CreateDatabaseAsync("haby_case_");
                right = await second.CreateDatabaseAsync("haby_case_");
                Assert.NotEqual(new NpgsqlConnectionStringBuilder(left).Database, new NpgsqlConnectionStringBuilder(right).Database);
                await Scalar(left, "CREATE TABLE isolation_control(value integer); INSERT INTO isolation_control VALUES (1)");
                await Scalar(right, "CREATE TABLE isolation_control(value integer); INSERT INTO isolation_control VALUES (2)");
                Assert.Equal(1, await Scalar(left, "SELECT value FROM isolation_control"));
                Assert.Equal(2, await Scalar(right, "SELECT value FROM isolation_control"));
                Assert.NotEqual(2, await Scalar(left, "SELECT value FROM isolation_control")); // Broken database routing cannot pass.
            }, () => DisposeEveryCases(first, second));
            foreach (var connection in new[] { left, right })
            {
                var name = new NpgsqlConnectionStringBuilder(connection).Database;
                Assert.Equal(0L, await Scalar(server.ConnectionString, "SELECT count(*) FROM pg_database WHERE datname = $1", name!));
            }
        }, server.DisposeAsync);
    }

    [Fact]
    public async Task Actual_started_container_is_removed_after_startup_fault_and_capacity_is_reusable()
    {
        var primary = new InvalidOperationException("Injected post-start failure.");
        var failed = new PostgreSqlFixture(control: true, () => Task.FromException(primary));
        var error = await Record.ExceptionAsync(() => failed.InitializeAsync());
        Assert.Same(primary, error);
        Assert.Equal(0, PostgreSqlResources.Active);
        await AssertServerClosed(failed.ConnectionString);
        await failed.DisposeAsync(); // Idempotent; cannot release another owner's slot.
        var replacement = new PostgreSqlFixture(control: true);
        await replacement.InitializeAsync();
        await RunOwned(async () => Assert.Equal(1, await Scalar(replacement.ConnectionString, "SELECT 1")), replacement.DisposeAsync);
        Assert.Equal(0, PostgreSqlResources.Active);
    }

    [Fact]
    public async Task Actual_body_failure_keeps_primary_and_removes_owned_server()
    {
        var server = new PostgreSqlFixture(control: true);
        await server.InitializeAsync();
        var primary = new InvalidOperationException("Injected body failure.");
        var error = await Record.ExceptionAsync(() => RunOwned(() => Task.FromException(primary), server.DisposeAsync));
        Assert.Same(primary, error);
        Assert.Equal(0, PostgreSqlResources.Active);
        await AssertServerClosed(server.ConnectionString);
    }

    private static async Task AssertServerClosed(string connection)
    {
        var settings = new NpgsqlConnectionStringBuilder(connection) { Timeout = 1 };
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => Scalar(settings.ConnectionString, "SELECT 1"));
    }

    private static async Task RunOwned(Func<Task> body, Func<Task> cleanup)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? primary = null;
        try { await body(); } catch (Exception error) { primary = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
        try { await cleanup(); }
        catch (Exception error)
        {
            if (primary is not null) throw new AggregateException("Body failed; owned cleanup also failed.", primary.SourceException, error);
            throw;
        }
        primary?.Throw();
    }

    private static async Task<object?> Scalar(string connection, string sql, string? value = null)
    {
        await using var database = new NpgsqlConnection(connection); await database.OpenAsync();
        await using var command = new NpgsqlCommand(sql, database);
        if (value is not null) command.Parameters.AddWithValue(value);
        return await command.ExecuteScalarAsync();
    }
    private static async Task DisposeEvery(params PostgreSqlFixture[] servers)
    {
        var errors = new List<Exception>();
        foreach (var server in servers) { try { await server.DisposeAsync(); } catch (Exception error) { errors.Add(error); } }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    private static async Task DisposeEveryCases(params CatalogTestCase[] cases)
    {
        var errors = new List<Exception>();
        foreach (var item in cases) { try { await item.DisposeAsync(); } catch (Exception error) { errors.Add(error); } }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
}
