using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class PlaintextSecretStoreTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Exact_versions_survive_restart_and_reject_other_scopes()
    {
        var connection = await NewDatabaseAsync();
        var first = new SecretRef(SecretId.New(), SecretVersionId.New());
        var second = new SecretRef(first.SecretId, SecretVersionId.New());
        var other = new SecretRef(SecretId.New(), SecretVersionId.New());
        var input = new byte[] { 41, 42, 43 };
        EnvironmentRecordId environment;
        EnvironmentRecordId wrong;
        await using (var services = Services(connection))
        {
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var store = services.GetRequiredService<ISecretStore>();
            environment = await store.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            wrong = await store.CreateEnvironmentAsync(EnvironmentId.Create("other"), default);
            await store.CreateAsync(environment, first, input, default);
            input[0] = 0;
            await store.AddVersionAsync(environment, second, new byte[] { 51 }, default);
            await store.CreateAsync(wrong, other, new byte[] { 61 }, default);
        }
        await using (var services = Services(connection))
        {
            var store = services.GetRequiredService<ISecretStore>();
            var material = await store.ResolveAsync(environment, first, default);
            Assert.NotNull(material);
            var copy = material.CopyBytes();
            Assert.Equal(new byte[] { 41, 42, 43 }, copy);
            copy[0] = 0;
            Assert.Equal(new byte[] { 41, 42, 43 }, material.CopyBytes());
            Assert.DoesNotContain("41", material.ToString());
            Assert.Equal(new byte[] { 51 }, (await store.ResolveAsync(environment, second, default))!.CopyBytes());
            Assert.Null(await store.ResolveAsync(wrong, first, default));
            Assert.Null(await store.ResolveAsync(environment, new SecretRef(other.SecretId, first.SecretVersionId), default));
            Assert.Null(await store.ResolveAsync(environment, new SecretRef(first.SecretId, other.SecretVersionId), default));
            Assert.Null(await store.ResolveAsync(environment, new SecretRef(first.SecretId, SecretVersionId.New()), default));
        }
    }

    [Fact]
    public async Task Failed_first_payload_and_precommit_cancellation_leave_no_secret()
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var store = services.GetRequiredService<ISecretStore>();
        var environment = await store.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await store.CreateAsync(environment, reference, new byte[] { 1 }, cancellation.Token));
        }
        await using (var check = new NpgsqlConnection(connection))
        {
            await check.OpenAsync();
            Assert.Equal(0, await CountAsync(check, "secrets"));
            await using var trigger = new NpgsqlCommand("CREATE FUNCTION reject_payload() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test rejection'; END $$; CREATE TRIGGER reject_payload BEFORE INSERT ON secret_payloads FOR EACH ROW EXECUTE FUNCTION reject_payload()", check);
            await trigger.ExecuteNonQueryAsync();
        }
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CreateAsync(environment, reference, new byte[] { 2 }, default));
        Assert.Equal("Secret storage write failed.", failure.Message);
        Assert.Null(failure.InnerException);
        await using (var check = new NpgsqlConnection(connection))
        {
            await check.OpenAsync();
            Assert.Equal(0, await CountAsync(check, "secrets"));
            Assert.Equal(0, await CountAsync(check, "secret_versions"));
        }
    }

    [Fact]
    public async Task Writes_reject_missing_or_wrong_environment_without_changing_versions()
    {
        var connection = await NewDatabaseAsync();
        SecretRef first;
        EnvironmentRecordId owner;
        EnvironmentRecordId wrong;
        await using (var services = Services(connection))
        {
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var store = services.GetRequiredService<ISecretStore>();
            owner = await store.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            wrong = await store.CreateEnvironmentAsync(EnvironmentId.Create("other"), default);
            first = new SecretRef(SecretId.New(), SecretVersionId.New());
            await store.CreateAsync(owner, first, new byte[] { 10 }, default);
        }

        await using (var services = Services(connection))
        {
            var store = services.GetRequiredService<ISecretStore>();
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.CreateAsync(EnvironmentRecordId.New(),
                    new SecretRef(SecretId.New(), SecretVersionId.New()), new byte[] { 11 }, default));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.AddVersionAsync(wrong,
                    new SecretRef(first.SecretId, SecretVersionId.New()), new byte[] { 12 }, default));
        }

        await using var check = new NpgsqlConnection(connection);
        await check.OpenAsync();
        Assert.Equal(1, await CountAsync(check, "secrets"));
        Assert.Equal(1, await CountAsync(check, "secret_versions"));
        Assert.Equal(1, await CountAsync(check, "secret_payloads"));
        await using var fresh = Services(connection);
        Assert.Equal(new byte[] { 10 }, (await fresh.GetRequiredService<ISecretStore>()
            .ResolveAsync(owner, first, default))!.CopyBytes());
    }

    [Fact]
    public async Task Secret_migration_rolls_back_without_changing_manifest_catalog()
    {
        var connection = await NewDatabaseAsync();
        await using (var services = Services(connection))
        {
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var store = services.GetRequiredService<ISecretStore>();
            var environment = await store.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            await store.CreateAsync(environment, new SecretRef(SecretId.New(), SecretVersionId.New()),
                new byte[] { 13 }, default);
        }

        var options = new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options;
        await using (var context = new ManifestCatalogDbContext(options))
            await context.Database.MigrateAsync("20261021165200_InitialManifestCatalog");

        await using var check = new NpgsqlConnection(connection);
        await check.OpenAsync();
        Assert.Equal(0, await CountAsync(check, "applications"));
        Assert.Equal(0, await CountAsync(check, "manifest_revisions"));
        Assert.Equal(1, await CountAsync(check, "\"__EFMigrationsHistory\""));
        await using var absent = new NpgsqlCommand(
            "SELECT to_regclass('public.environments') IS NULL AND to_regclass('public.secrets') IS NULL " +
            "AND to_regclass('public.secret_versions') IS NULL AND to_regclass('public.secret_payloads') IS NULL " +
            "AND to_regprocedure('public.reject_secret_version_mutation()') IS NULL", check);
        Assert.True((bool)(await absent.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Cancellation_during_first_payload_insert_rolls_back_parent_and_version()
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var store = services.GetRequiredService<ISecretStore>();
        var environment = await store.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        await using (var check = new NpgsqlConnection(connection))
        {
            await check.OpenAsync();
            await using var trigger = new NpgsqlCommand("CREATE FUNCTION delay_payload() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_sleep(3); RETURN NEW; END $$; CREATE TRIGGER delay_payload BEFORE INSERT ON secret_payloads FOR EACH ROW EXECUTE FUNCTION delay_payload()", check);
            await trigger.ExecuteNonQueryAsync();
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.CreateAsync(environment,
            new SecretRef(SecretId.New(), SecretVersionId.New()), new byte[] { 99 }, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        await using var verify = new NpgsqlConnection(connection);
        await verify.OpenAsync();
        Assert.Equal(0, await CountAsync(verify, "secrets"));
        Assert.Equal(0, await CountAsync(verify, "secret_versions"));
        Assert.Equal(0, await CountAsync(verify, "secret_payloads"));
    }

    [Fact]
    public async Task Concurrent_duplicate_version_and_unsupported_format_fail_closed()
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var store = services.GetRequiredService<ISecretStore>();
        var environment = await store.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var first = new SecretRef(SecretId.New(), SecretVersionId.New());
        await store.CreateAsync(environment, first, new byte[] { 7 }, default);
        var next = new SecretRef(first.SecretId, SecretVersionId.New());
        var attempts = await Task.WhenAll(
            Attempt(() => store.AddVersionAsync(environment, next, new byte[] { 8 }, default)),
            Attempt(() => store.AddVersionAsync(environment, next, new byte[] { 9 }, default)));
        Assert.Single(attempts, error => error is null);
        Assert.Single(attempts, error => error is InvalidOperationException);
        await using (var check = new NpgsqlConnection(connection))
        {
            await check.OpenAsync();
            Assert.Equal(2, await CountAsync(check, "secret_versions"));
            await using var disable = new NpgsqlCommand("ALTER TABLE secret_versions DISABLE TRIGGER secret_versions_immutable", check);
            await disable.ExecuteNonQueryAsync();
            await using var corrupt = new NpgsqlCommand("UPDATE secret_versions SET protection_format = 'Unknown' WHERE id = @id", check);
            corrupt.Parameters.AddWithValue("id", Guid.Parse(first.SecretVersionId.ToString()));
            await corrupt.ExecuteNonQueryAsync();
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.ResolveAsync(environment, first, default));
        Assert.Equal("Unsupported stored secret protection format.", error.Message);
        await using (var check = new NpgsqlConnection(connection))
        {
            await check.OpenAsync();
            await using var change = new NpgsqlCommand("UPDATE secret_versions SET format_version = 2 WHERE id = @id", check);
            change.Parameters.AddWithValue("id", Guid.Parse(first.SecretVersionId.ToString()));
            await change.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.ResolveAsync(environment, first, default));
    }

    [Fact]
    public async Task Environment_uniqueness_and_version_immutability_are_database_enforced()
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var store = services.GetRequiredService<ISecretStore>();
        var environment = await store.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        Assert.Equal(environment, await store.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default));
        await using (var duplicateContext = new ManifestCatalogDbContext(
            new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options))
        {
            var duplicateId = Guid.CreateVersion7();
            var duplicateName = "pilot";
            var error = await Assert.ThrowsAsync<PostgresException>(async () =>
                await duplicateContext.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO environments (id, environment_id) VALUES ({duplicateId}, {duplicateName})"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        }
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        await store.CreateAsync(environment, reference, new byte[] { 83 }, default);
        await using var check = new NpgsqlConnection(connection);
        await check.OpenAsync();
        await using var overwrite = new NpgsqlCommand("UPDATE secret_payloads SET bytes = @bytes WHERE secret_version_id = @id", check);
        overwrite.Parameters.AddWithValue("bytes", new byte[] { 84 });
        overwrite.Parameters.AddWithValue("id", Guid.Parse(reference.SecretVersionId.ToString()));
        await Assert.ThrowsAsync<PostgresException>(async () => await overwrite.ExecuteNonQueryAsync());
        await using var remove = new NpgsqlCommand("DELETE FROM secret_versions WHERE id = @id", check);
        remove.Parameters.AddWithValue("id", Guid.Parse(reference.SecretVersionId.ToString()));
        await Assert.ThrowsAsync<PostgresException>(async () => await remove.ExecuteNonQueryAsync());
        Assert.Equal(new byte[] { 83 }, (await store.ResolveAsync(environment, reference, default))!.CopyBytes());
    }

    [Fact]
    public async Task Backup_restore_retains_exact_secret_bytes()
    {
        var source = await NewDatabaseAsync();
        var target = await NewDatabaseAsync();
        var dump = "/tmp/haby-secret-" + Guid.NewGuid().ToString("N") + ".dump";
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        var expected = new byte[] { 113, 17, 203, 9 };
        EnvironmentRecordId environment;
        try
        {
            await using (var services = Services(source))
            {
                await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
                var store = services.GetRequiredService<ISecretStore>();
                environment = await store.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
                await store.CreateAsync(environment, reference, expected, default);
            }
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump,
                "--dbname=" + new NpgsqlConnectionStringBuilder(source).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner",
                "--no-privileges", "--dbname=" + new NpgsqlConnectionStringBuilder(target).Database, dump);
            await using var fresh = Services(target);
            Assert.Equal(expected, (await fresh.GetRequiredService<ISecretStore>()
                .ResolveAsync(environment, reference, default))!.CopyBytes());
        }
        finally
        {
            await fixture.RunClientUtilityAsync("rm", "-f", dump);
        }
    }

    private static async Task<Exception?> Attempt(Func<ValueTask> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand("SELECT count(*) FROM " + table, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> NewDatabaseAsync()
    {
        var name = "haby_secret_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    private static ServiceProvider Services(string connection) => new ServiceCollection()
        .AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore().BuildServiceProvider();
}
