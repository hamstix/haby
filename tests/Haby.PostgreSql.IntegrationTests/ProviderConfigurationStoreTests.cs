using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class ProviderConfigurationStoreTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Migration_and_exact_revisions_survive_restart_without_reading_payload()
    {
        var connection = await NewDatabaseAsync();
        EnvironmentRecordId environment;
        ProviderInstanceId instance;
        SecretRef secret;
        await using (var services = Services(connection))
        {
            var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            await using (var context = await factory.CreateDbContextAsync())
                await context.GetService<IMigrator>().MigrateAsync("20261026094557_AddPlaintextSecrets");
            var secrets = services.GetRequiredService<ISecretStore>();
            environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            secret = new SecretRef(SecretId.New(), SecretVersionId.New());
            await secrets.CreateAsync(environment, secret, new byte[] { 123, 45, 67 }, default);
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var store = services.GetRequiredService<IProviderConfigurationStore>();
            instance = await store.CreateInstanceAsync(environment, "localhost", 5432, default);
            await store.AddRevisionAsync(environment, instance, Number(1), Configuration(secret), default);
            await store.AddRevisionAsync(environment, instance, Number(2),
                Configuration(secret, database: "maintenance"), default);
        }
        var readerName = "provider_reader_" + Guid.NewGuid().ToString("N");
        var readerPassword = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await using (var connectionCheck = new NpgsqlConnection(connection))
        {
            await connectionCheck.OpenAsync();
            await using var command = new NpgsqlCommand($"""
                CREATE ROLE "{readerName}" LOGIN PASSWORD '{readerPassword}';
                GRANT USAGE ON SCHEMA public TO "{readerName}";
                GRANT SELECT ON provider_configuration_revisions TO "{readerName}";
                """, connectionCheck);
            await command.ExecuteNonQueryAsync();
        }
        var readerConnection = new NpgsqlConnectionStringBuilder(connection)
        { Username = readerName, Password = readerPassword }.ConnectionString;
        await using (var fresh = Services(readerConnection))
        {
            var store = fresh.GetRequiredService<IProviderConfigurationStore>();
            var first = await store.FindRevisionAsync(environment, instance, Number(1), default);
            var second = await store.FindRevisionAsync(environment, instance, Number(2), default);
            Assert.Equal("postgres", first!.Configuration.MaintenanceDatabase);
            Assert.Equal("maintenance", second!.Configuration.MaintenanceDatabase);
            Assert.Equal(secret, first.Configuration.AdministratorPassword);
            Assert.Equal(secret, second.Configuration.AdministratorPassword);
        }
    }

    [Fact]
    public async Task Rejections_and_concurrency_leave_immutable_rows()
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = services.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var wrong = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("other"), default);
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        await secrets.CreateAsync(environment, secret, new byte[] { 1 }, default);
        var otherSecret = new SecretRef(SecretId.New(), SecretVersionId.New());
        await secrets.CreateAsync(wrong, otherSecret, new byte[] { 2 }, default);
        var store = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await store.CreateInstanceAsync(environment, "localhost", 5432, default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AddRevisionAsync(environment, instance, Number(1), Configuration(otherSecret), default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AddRevisionAsync(environment, instance, Number(1),
                Configuration(new SecretRef(SecretId.New(), SecretVersionId.New())), default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AddRevisionAsync(environment, instance, Number(1), Configuration(secret, host: "other"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AddRevisionAsync(environment, instance, Number(1), Configuration(secret, implementation: "unknown"), default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlProviderConfiguration(2,
            "localhost", 5432, "postgres", "admin", secret, Implementation));
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await store.AddRevisionAsync(environment, instance, Number(1), Configuration(secret), canceled.Token));
        }
        var writes = await Task.WhenAll(Attempt(() => store.AddRevisionAsync(environment, instance, Number(1),
            Configuration(secret), default)), Attempt(() => store.AddRevisionAsync(environment, instance, Number(1),
            Configuration(secret), default)));
        Assert.Single(writes, result => result is null);
        Assert.Single(writes, result => result is InvalidOperationException);
        Assert.NotNull(await store.FindRevisionAsync(environment, instance, Number(1), default));
        Assert.Null(await store.FindRevisionAsync(wrong, instance, Number(1), default));
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await new NpgsqlCommand("UPDATE provider_instances SET host = 'other'", database).ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await new NpgsqlCommand("UPDATE provider_configuration_revisions SET maintenance_database = 'other'", database)
                .ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await new NpgsqlCommand("DELETE FROM provider_configuration_revisions", database).ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await new NpgsqlCommand("""
                INSERT INTO provider_configuration_revisions
                    (provider_instance_id, revision_number, environment_record_id, host, port, shape_version,
                     maintenance_database, administrator_user_name, administrator_secret_id,
                     administrator_secret_version_id, implementation_id)
                SELECT provider_instance_id, 2, environment_record_id, host, port, 2,
                    maintenance_database, administrator_user_name, administrator_secret_id,
                    administrator_secret_version_id, implementation_id
                FROM provider_configuration_revisions WHERE revision_number = 1
                """, database).ExecuteNonQueryAsync());
        await new NpgsqlCommand("""
            CREATE FUNCTION reject_provider_test_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test rejection'; END $$;
            CREATE TRIGGER reject_provider_test_insert BEFORE INSERT ON provider_configuration_revisions
                FOR EACH ROW EXECUTE FUNCTION reject_provider_test_insert();
            """, database).ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AddRevisionAsync(environment, instance, Number(2), Configuration(secret), default));
        Assert.Null(await store.FindRevisionAsync(environment, instance, Number(2), default));
        Assert.Equal("postgres", (await store.FindRevisionAsync(environment, instance, Number(1), default))!
            .Configuration.MaintenanceDatabase);
    }

    private const string Implementation = "haby.postgresql.database/v1alpha1";
    private static ProviderConfigurationRevisionNumber Number(long value) => ProviderConfigurationRevisionNumber.Create(value);
    private static PostgreSqlProviderConfiguration Configuration(SecretRef secret, string host = "localhost",
        string database = "postgres", string implementation = Implementation) =>
        new(1, host, 5432, database, "admin", secret, implementation);
    private static async Task<Exception?> Attempt(Func<ValueTask> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }
    private async Task<string> NewDatabaseAsync()
    {
        var name = "haby_provider_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }
    private static ServiceProvider Services(string connection) => new ServiceCollection()
        .AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore()
        .AddPostgreSqlProviderConfigurationStore()
        .AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
        .BuildServiceProvider();
}
