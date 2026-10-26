using System.Diagnostics;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

[CollectionDefinition("Provider alias isolated", DisableParallelization = true)]
public sealed class ProviderAliasCollection;

[Collection("Provider alias isolated")]
public sealed class ProviderAliasTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Implementation = "haby.postgresql.database/v1alpha1";

    [Fact]
    public async Task Migrated_alias_switch_revalidates_and_resolves_exact_revision_after_restart()
    {
        var connection = await NewDatabaseAsync();
        var administrator = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        EnvironmentRecordId environment;
        ProviderAliasBinding first;
        ProviderAliasBinding second;
        var alias = ProviderAlias.Create("postgresql.primary");
        await using (var services = Services(connection))
        {
            var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            await using (var context = await factory.CreateDbContextAsync())
                await context.GetService<IMigrator>().MigrateAsync("20261026113715_AddProviderConfigurationRevisions");
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var secretStore = services.GetRequiredService<ISecretStore>();
            environment = await secretStore.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
            await secretStore.CreateAsync(environment, secret, Encoding.UTF8.GetBytes(administrator.Password!), default);
            var revisions = services.GetRequiredService<IProviderConfigurationStore>();
            var instance = await revisions.CreateInstanceAsync(environment, Config(administrator, secret), default);
            await revisions.AddRevisionAsync(environment, instance, Number(1), Config(administrator, secret), default);
            await revisions.AddRevisionAsync(environment, instance, Number(2), Config(administrator, secret), default);
            first = new(instance, Number(1));
            second = new(instance, Number(2));
            var check = services.GetRequiredService<IProviderRevisionPreflight>();
            var before = await DatabaseCountAsync(fixture.ConnectionString);
            Assert.Equal(ProviderPreflightOutcome.Ready, (await check.CheckAsync(environment, first, default)).Outcome);
            var aliases = services.GetRequiredService<IProviderAliasStore>();
            Assert.Equal(ProviderAliasChangeOutcome.Changed,
                (await aliases.ChangeAsync(environment, alias, null, first, "test-runner", default)).Outcome);
            Assert.Equal(ProviderAliasChangeOutcome.Changed,
                (await aliases.ChangeAsync(environment, alias, first, second, "test-runner", default)).Outcome);
            Assert.Equal(ProviderAliasChangeOutcome.Unchanged,
                (await aliases.ChangeAsync(environment, alias, second, second, "test-runner", default)).Outcome);
            Assert.Equal(ProviderAliasChangeOutcome.StaleExpectation,
                (await aliases.ChangeAsync(environment, alias, first, first, "test-runner", default)).Outcome);
            Assert.Equal(ProviderAliasChangeOutcome.InstanceAlreadyBound,
                (await aliases.ChangeAsync(environment, ProviderAlias.Create("postgresql.other"), null,
                    first, "test-runner", default)).Outcome);
            Assert.Equal(ProviderAliasChangeOutcome.TargetUnavailable,
                (await aliases.ChangeAsync(environment, alias, second,
                    new(instance, Number(99)), "test-runner", default)).Outcome);
            Assert.Equal(before, await DatabaseCountAsync(fixture.ConnectionString));
        }
        await using (var restarted = Services(connection))
        {
            var resolved = await restarted.GetRequiredService<IProviderAliasResolver>()
                .ResolveAsync(environment, alias, default);
            Assert.Equal(second, resolved!.Binding);
            Assert.Equal(Implementation, resolved.ImplementationId);
            Assert.DoesNotContain("Configuration", resolved.GetType().GetProperties().Select(p => p.Name));
        }
        Assert.Equal(2L, await CountAsync(connection, "provider_alias_audit"));
        Assert.Equal(1L, await CountAsync(connection, "provider_aliases"));
    }

    [Fact]
    public async Task Preflight_fails_closed_for_scoped_secret_authentication_privilege_and_server_errors()
    {
        var connection = await NewDatabaseAsync();
        var administrator = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = services.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var other = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("other"), default);
        var revisions = services.GetRequiredService<IProviderConfigurationStore>();
        var check = services.GetRequiredService<IProviderRevisionPreflight>();
        var before = await DatabaseCountAsync(fixture.ConnectionString);

        var good = await CreateAsync(Encoding.UTF8.GetBytes(administrator.Password!), administrator, environment);
        Assert.Equal(ProviderPreflightOutcome.Ready, (await check.CheckAsync(environment, good, default)).Outcome);
        Assert.Equal(ProviderPreflightOutcome.RevisionUnavailable, (await check.CheckAsync(other, good, default)).Outcome);
        await using (var catalog = new NpgsqlConnection(connection))
        {
            await catalog.OpenAsync();
            await using var crossEnvironment = new NpgsqlCommand("""
                INSERT INTO provider_aliases (environment_record_id, alias, provider_instance_id, revision_number)
                VALUES (@environment, 'postgresql.cross', @instance, @revision)
                """, catalog);
            crossEnvironment.Parameters.AddWithValue("environment", Guid.Parse(other.ToString()));
            crossEnvironment.Parameters.AddWithValue("instance", Guid.Parse(good.InstanceId.ToString()));
            crossEnvironment.Parameters.AddWithValue("revision", good.RevisionNumber.Value);
            var rejected = await Assert.ThrowsAsync<PostgresException>(async () =>
                await crossEnvironment.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, rejected.SqlState);
        }
        var bad = await CreateAsync(Encoding.UTF8.GetBytes("incorrect"), administrator, environment);
        Assert.Equal(ProviderPreflightOutcome.AuthenticationFailed, (await check.CheckAsync(environment, bad, default)).Outcome);
        var unavailable = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Port = 1 };
        var offline = await CreateAsync(Encoding.UTF8.GetBytes(administrator.Password!), unavailable, environment);
        Assert.Equal(ProviderPreflightOutcome.ServerUnavailable,
            (await check.CheckAsync(environment, offline, default)).Outcome);

        var role = "preflight_" + Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE ROLE \"{role}\" LOGIN PASSWORD '{password}'", admin).ExecuteNonQueryAsync();
        }
        var limited = new NpgsqlConnectionStringBuilder(administrator.ConnectionString)
        { Username = role, Password = password };
        var restricted = await CreateAsync(Encoding.UTF8.GetBytes(password), limited, environment);
        Assert.Equal(ProviderPreflightOutcome.InsufficientPrivilege,
            (await check.CheckAsync(environment, restricted, default)).Outcome);
        var missingSecret = new SecretRef(SecretId.New(), SecretVersionId.New());
        // Immutable versions and revision associations make a stored missing payload unreachable through supported APIs.
        var missingInstance = await revisions.CreateInstanceAsync(environment, Config(administrator, missingSecret), default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await revisions.AddRevisionAsync(environment, missingInstance, Number(1),
                Config(administrator, missingSecret), default));
        var otherSecret = new SecretRef(SecretId.New(), SecretVersionId.New());
        await secrets.CreateAsync(other, otherSecret, Encoding.UTF8.GetBytes(administrator.Password!), default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await revisions.AddRevisionAsync(environment, missingInstance, Number(1),
                Config(administrator, otherSecret), default));
        Assert.Equal(ProviderPreflightOutcome.RevisionUnavailable,
            (await check.CheckAsync(environment, new(missingInstance, Number(1)), default)).Outcome);
        Assert.Equal(before, await DatabaseCountAsync(fixture.ConnectionString));

        async Task<ProviderAliasBinding> CreateAsync(byte[] passwordBytes, NpgsqlConnectionStringBuilder target,
            EnvironmentRecordId scope)
        {
            var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
            await secrets.CreateAsync(scope, reference, passwordBytes, default);
            var instance = await revisions.CreateInstanceAsync(scope, Config(target, reference), default);
            await revisions.AddRevisionAsync(scope, instance, Number(1), Config(target, reference), default);
            return new(instance, Number(1));
        }
    }

    [Fact]
    public async Task Concurrent_CAS_has_one_winner_and_failed_preflight_does_not_audit()
    {
        var connection = await NewDatabaseAsync();
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = services.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var good = new SecretRef(SecretId.New(), SecretVersionId.New());
        var bad = new SecretRef(SecretId.New(), SecretVersionId.New());
        await secrets.CreateAsync(environment, good, Encoding.UTF8.GetBytes(admin.Password!), default);
        await secrets.CreateAsync(environment, bad, Encoding.UTF8.GetBytes("incorrect"), default);
        var revisions = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await revisions.CreateInstanceAsync(environment, Config(admin, good), default);
        await revisions.AddRevisionAsync(environment, instance, Number(1), Config(admin, good), default);
        await revisions.AddRevisionAsync(environment, instance, Number(2), Config(admin, good), default);
        await revisions.AddRevisionAsync(environment, instance, Number(3), Config(admin, good), default);
        await revisions.AddRevisionAsync(environment, instance, Number(4), Config(admin, bad), default);
        var first = new ProviderAliasBinding(instance, Number(1));
        var alias = ProviderAlias.Create("postgresql.primary");
        var store = services.GetRequiredService<IProviderAliasStore>();
        Assert.Equal(ProviderAliasChangeOutcome.Changed,
            (await store.ChangeAsync(environment, alias, null, first, "test-runner", default)).Outcome);
        Assert.Equal(ProviderAliasChangeOutcome.PreflightFailed,
            (await store.ChangeAsync(environment, alias, first, new(instance, Number(4)), "test-runner", default)).Outcome);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await store.ChangeAsync(environment, alias, first, new(instance, Number(2)), "test-runner", canceled.Token));
        }
        var outcomes = await Task.WhenAll(
            store.ChangeAsync(environment, alias, first, new(instance, Number(2)), "test-runner", default).AsTask(),
            store.ChangeAsync(environment, alias, first, new(instance, Number(3)), "test-runner", default).AsTask());
        Assert.Single(outcomes, result => result.Outcome == ProviderAliasChangeOutcome.Changed);
        Assert.Single(outcomes, result => result.Outcome == ProviderAliasChangeOutcome.StaleExpectation);
        Assert.Equal(2L, await CountAsync(connection, "provider_alias_audit"));
        var winner = (await store.ResolveAsync(environment, alias, default))!.Binding;
        Assert.Contains(winner.RevisionNumber.Value, new long[] { 2, 3 });
        await using (var catalog = new NpgsqlConnection(connection))
        {
            await catalog.OpenAsync();
            await new NpgsqlCommand("""
                CREATE FUNCTION reject_alias_audit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'audit blocked'; END $$;
                CREATE TRIGGER reject_alias_audit BEFORE INSERT ON provider_alias_audit
                FOR EACH ROW EXECUTE FUNCTION reject_alias_audit();
                """, catalog).ExecuteNonQueryAsync();
        }
        var alternate = new ProviderAliasBinding(instance, Number(winner.RevisionNumber.Value == 2 ? 3 : 2));
        Assert.Equal(ProviderAliasChangeOutcome.StoreUnavailable,
            (await store.ChangeAsync(environment, alias, winner, alternate, "test-runner", default)).Outcome);
        Assert.Equal(winner, (await store.ResolveAsync(environment, alias, default))!.Binding);
        Assert.Equal(2L, await CountAsync(connection, "provider_alias_audit"));
    }

    [Fact]
    public async Task Concurrent_expected_absence_has_one_winner_and_audits_once()
    {
        var connection = await NewDatabaseAsync();
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = services.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        await secrets.CreateAsync(environment, reference, Encoding.UTF8.GetBytes(admin.Password!), default);
        var revisions = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await revisions.CreateInstanceAsync(environment, Config(admin, reference), default);
        await revisions.AddRevisionAsync(environment, instance, Number(1), Config(admin, reference), default);
        var proposed = new ProviderAliasBinding(instance, Number(1));
        var alias = ProviderAlias.Create("postgresql.primary");
        var store = services.GetRequiredService<IProviderAliasStore>();

        var results = await Task.WhenAll(
            store.ChangeAsync(environment, alias, null, proposed, "test-runner", default).AsTask(),
            store.ChangeAsync(environment, alias, null, proposed, "test-runner", default).AsTask());

        Assert.Single(results, result => result.Outcome == ProviderAliasChangeOutcome.Changed);
        Assert.Single(results, result => result.Outcome == ProviderAliasChangeOutcome.StaleExpectation);
        Assert.Equal(proposed, (await store.ResolveAsync(environment, alias, default))!.Binding);
        Assert.Equal(1L, await CountAsync(connection, "provider_alias_audit"));
    }

    [Fact]
    public async Task Blocked_CAS_is_bounded_and_leaves_binding_and_audit_unchanged()
    {
        var connection = await NewDatabaseAsync();
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = services.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        await secrets.CreateAsync(environment, reference, Encoding.UTF8.GetBytes(admin.Password!), default);
        var revisions = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await revisions.CreateInstanceAsync(environment, Config(admin, reference), default);
        await revisions.AddRevisionAsync(environment, instance, Number(1), Config(admin, reference), default);
        await revisions.AddRevisionAsync(environment, instance, Number(2), Config(admin, reference), default);
        var first = new ProviderAliasBinding(instance, Number(1));
        var second = new ProviderAliasBinding(instance, Number(2));
        var alias = ProviderAlias.Create("postgresql.primary");
        var store = services.GetRequiredService<IProviderAliasStore>();
        Assert.Equal(ProviderAliasChangeOutcome.Changed,
            (await store.ChangeAsync(environment, alias, null, first, "test-runner", default)).Outcome);
        await using (var blocker = new NpgsqlConnection(connection))
        {
            await blocker.OpenAsync();
            await using var lockTransaction = await blocker.BeginTransactionAsync();
            await using var hold = new NpgsqlCommand("""
                UPDATE provider_aliases SET revision_number = revision_number
                WHERE environment_record_id = @environment AND alias = @alias
                """, blocker, lockTransaction);
            hold.Parameters.AddWithValue("environment", Guid.Parse(environment.ToString()));
            hold.Parameters.AddWithValue("alias", alias.Value);
            Assert.Equal(1, await hold.ExecuteNonQueryAsync());

            var result = await store.ChangeAsync(environment, alias, first, second, "test-runner", default);
            Assert.Equal(ProviderAliasChangeOutcome.StoreUnavailable, result.Outcome);
            await lockTransaction.RollbackAsync();
        }
        Assert.Equal(first, (await store.ResolveAsync(environment, alias, default))!.Binding);
        Assert.Equal(1L, await CountAsync(connection, "provider_alias_audit"));
    }

    [Fact]
    public async Task Deadline_cancels_a_real_PostgreSql_secret_read_without_creating_a_database()
    {
        var connection = await NewDatabaseAsync();
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = services.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        await secrets.CreateAsync(environment, reference, Encoding.UTF8.GetBytes(admin.Password!), default);
        var revisions = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await revisions.CreateInstanceAsync(environment, Config(admin, reference), default);
        await revisions.AddRevisionAsync(environment, instance, Number(1), Config(admin, reference), default);
        var preflight = new PostgreSqlProviderRevisionPreflight(revisions, new SlowSecretStore(connection));
        var before = await DatabaseCountAsync(fixture.ConnectionString);

        var elapsed = Stopwatch.StartNew();
        var result = await preflight.CheckAsync(environment, new(instance, Number(1)), default);
        elapsed.Stop();

        Assert.Equal(ProviderPreflightOutcome.DeadlineExceeded, result.Outcome);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(12));
        Assert.Equal(before, await DatabaseCountAsync(fixture.ConnectionString));
    }

    private sealed class SlowSecretStore(string connectionString) : ISecretStore
    {
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask CreateAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public async ValueTask<SecretMaterial?> ResolveAsync(EnvironmentRecordId environment, SecretRef reference,
            CancellationToken cancellationToken)
        {
            await using var database = new NpgsqlConnection(connectionString);
            await database.OpenAsync(cancellationToken);
            await using var sleep = new NpgsqlCommand("SELECT pg_sleep(12)", database) { CommandTimeout = 20 };
            await sleep.ExecuteNonQueryAsync(cancellationToken);
            return null;
        }
    }

    private static ProviderConfigurationRevisionNumber Number(long value) => ProviderConfigurationRevisionNumber.Create(value);
    private static PostgreSqlProviderConfiguration Config(NpgsqlConnectionStringBuilder target, SecretRef secret) =>
        new(1, target.Host!, target.Port, target.Database!, target.Username!, secret, Implementation);

    private async Task<string> NewDatabaseAsync()
    {
        var name = "haby_alias_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    private static async Task<long> DatabaseCountAsync(string connection)
    {
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        return (long)(await new NpgsqlCommand("SELECT count(*) FROM pg_database", database).ExecuteScalarAsync())!;
    }

    private static async Task<long> CountAsync(string connection, string table)
    {
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        return (long)(await new NpgsqlCommand($"SELECT count(*) FROM {table}", database).ExecuteScalarAsync())!;
    }

    private static ServiceProvider Services(string connection) => new ServiceCollection()
        .AddPostgreSqlManifestCatalog(connection)
        .AddPostgreSqlPlaintextSecretStore()
        .AddPostgreSqlProviderConfigurationStore()
        .AddPostgreSqlProviderAliasStore()
        .AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
        .AddPostgreSqlProviderConfigurationCodec()
        .AddPostgreSqlProviderRevisionPreflight()
        .BuildServiceProvider();
}
