using System.Text.Json;
using System.Text.Json.Serialization;
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

public sealed class ProviderConfigurationStoreTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Migration_and_exact_revisions_survive_restart_without_secret_table_access()
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
            instance = await store.CreateInstanceAsync(environment, Configuration(secret), default);
            await store.AddRevisionAsync(environment, instance, Number(1), Configuration(secret), default);
            await store.AddRevisionAsync(environment, instance, Number(2),
                Configuration(secret, database: "maintenance"), default);
            Assert.Equal(new byte[] { 123, 45, 67 },
                (await secrets.ResolveAsync(environment, secret, default))!.CopyBytes());
        }
        var readerName = "provider_reader_" + Guid.NewGuid().ToString("N");
        var readerPassword = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await using (var check = new NpgsqlConnection(connection))
        {
            await check.OpenAsync();
            await new NpgsqlCommand($"""
                CREATE ROLE "{readerName}" LOGIN PASSWORD '{readerPassword}';
                GRANT USAGE ON SCHEMA public TO "{readerName}";
                GRANT SELECT ON provider_instances, provider_configuration_revisions,
                    provider_revision_secret_refs TO "{readerName}";
                """, check).ExecuteNonQueryAsync();
        }
        var readerConnection = new NpgsqlConnectionStringBuilder(connection)
        { Username = readerName, Password = readerPassword }.ConnectionString;
        await using (var fresh = Services(readerConnection))
        {
            var store = fresh.GetRequiredService<IProviderConfigurationStore>();
            var first = await store.FindRevisionAsync<PostgreSqlProviderConfiguration>(environment, instance, Number(1), default);
            var second = await store.FindRevisionAsync<PostgreSqlProviderConfiguration>(environment, instance, Number(2), default);
            Assert.Equal("postgres", first!.Configuration.MaintenanceDatabase);
            Assert.Equal("maintenance", second!.Configuration.MaintenanceDatabase);
            Assert.Equal(secret, first.Configuration.AdministratorPassword);
            Assert.Equal(secret, second.Configuration.AdministratorPassword);
        }
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        Assert.Equal(2L, (long)(await new NpgsqlCommand("SELECT count(*) FROM provider_revision_secret_refs", database)
            .ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task PostgreSql_target_secret_shape_and_concurrent_write_fail_closed()
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
        var instance = await store.CreateInstanceAsync(environment, Configuration(secret), default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AddRevisionAsync(environment, instance, Number(1), Configuration(otherSecret), default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AddRevisionAsync(environment, instance, Number(1),
                Configuration(new SecretRef(SecretId.New(), SecretVersionId.New())), default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AddRevisionAsync(environment, instance, Number(1), Configuration(secret, host: "other"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AddRevisionAsync(environment, instance, Number(1), Configuration(secret, port: 5433), default));
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
        Assert.Null(await store.FindRevisionAsync<PostgreSqlProviderConfiguration>(wrong, instance, Number(1), default));
        var codec = services.GetServices<IProviderConfigurationCodec>().OfType<PostgreSqlProviderConfigurationCodec>().Single();
        var json = codec.Encode(Configuration(secret)).PayloadJson;
        Assert.Throws<InvalidDataException>(() => codec.DecodeTyped(json.Insert(json.Length - 1, ",\"Unknown\":1")));
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await new NpgsqlCommand("UPDATE provider_instances SET target_identity = 'other'", database).ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await new NpgsqlCommand("UPDATE provider_configuration_revisions SET payload_json = '{}'", database)
                .ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await new NpgsqlCommand("DELETE FROM provider_revision_secret_refs", database).ExecuteNonQueryAsync());
        var unsealed = await Assert.ThrowsAsync<PostgresException>(async () =>
            await new NpgsqlCommand("""
                INSERT INTO provider_configuration_revisions
                    (provider_instance_id, revision_number, environment_record_id, implementation_id,
                     configuration_shape_id, payload_format_version, payload_json, is_sealed)
                SELECT provider_instance_id, 2, environment_record_id, implementation_id,
                    configuration_shape_id, payload_format_version, payload_json, FALSE
                FROM provider_configuration_revisions WHERE revision_number = 1
                """, database).ExecuteNonQueryAsync());
        Assert.Contains("sealed", unsealed.MessageText);
        await new NpgsqlCommand("""
            CREATE FUNCTION reject_provider_test_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test rejection'; END $$;
            CREATE TRIGGER reject_provider_test_insert BEFORE INSERT ON provider_revision_secret_refs
                FOR EACH ROW EXECUTE FUNCTION reject_provider_test_insert();
            """, database).ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AddRevisionAsync(environment, instance, Number(2), Configuration(secret), default));
        Assert.Null(await store.FindRevisionAsync<PostgreSqlProviderConfiguration>(environment, instance, Number(2), default));
        Assert.Equal(1L, (long)(await new NpgsqlCommand("SELECT count(*) FROM provider_configuration_revisions", database)
            .ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Second_shape_uses_same_tables_and_unknown_codec_fails_after_restart()
    {
        var connection = await NewDatabaseAsync();
        EnvironmentRecordId environment;
        ProviderInstanceId instance;
        await using (var services = Services(connection))
        {
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            environment = await services.GetRequiredService<ISecretStore>()
                .CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            var store = services.GetRequiredService<IProviderConfigurationStore>();
            instance = await store.CreateInstanceAsync(environment, new SampleConfiguration("sample-target", "sample.impl/v1"), default);
            await store.AddRevisionAsync(environment, instance, Number(1),
                new SampleConfiguration("sample-target", "sample.impl/v1"), default);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.AddRevisionAsync(environment, instance, Number(2),
                    new SampleConfiguration("different", "sample.impl/v1"), default));
        }
        await using (var fresh = Services(connection))
        {
            var loaded = await fresh.GetRequiredService<IProviderConfigurationStore>()
                .FindRevisionAsync<SampleConfiguration>(environment, instance, Number(1), default);
            Assert.Equal("sample-target", loaded!.Configuration.Target);
        }
        await using (var withoutShape = Services(connection, includeSample: false))
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await withoutShape.GetRequiredService<IProviderConfigurationStore>()
                    .FindRevisionAsync<SampleConfiguration>(environment, instance, Number(1), default));
        }
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM provider_revision_secret_refs", database)
            .ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Unexpected_secret_association_is_rejected_on_exact_read()
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = services.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        await secrets.CreateAsync(environment, secret, new byte[] { 1 }, default);
        var store = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await store.CreateInstanceAsync(environment, Configuration(secret), default);
        await store.AddRevisionAsync(environment, instance, Number(1), Configuration(secret), default);
        await using (var database = new NpgsqlConnection(connection))
        {
            await database.OpenAsync();
            var insert = """
                INSERT INTO provider_revision_secret_refs
                    (provider_instance_id, revision_number, slot, environment_record_id, secret_id, secret_version_id)
                SELECT provider_instance_id, revision_number, 'unexpected', environment_record_id, secret_id, secret_version_id
                FROM provider_revision_secret_refs
                """;
            await Assert.ThrowsAsync<PostgresException>(async () =>
                await new NpgsqlCommand(insert, database).ExecuteNonQueryAsync());
            // Simulate privileged catalog corruption to prove the exact read checks payload associations.
            await new NpgsqlCommand("ALTER TABLE provider_revision_secret_refs DISABLE TRIGGER provider_revision_secret_refs_before_seal",
                database).ExecuteNonQueryAsync();
            await new NpgsqlCommand(insert, database).ExecuteNonQueryAsync();
            await new NpgsqlCommand("ALTER TABLE provider_revision_secret_refs ENABLE TRIGGER provider_revision_secret_refs_before_seal",
                database).ExecuteNonQueryAsync();
        }
        await using var fresh = Services(connection);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fresh.GetRequiredService<IProviderConfigurationStore>()
                .FindRevisionAsync<PostgreSqlProviderConfiguration>(environment, instance, Number(1), default));
    }

    [Fact]
    public async Task Direct_revision_insert_requires_unsealed_object_payload()
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var environment = await services.GetRequiredService<ISecretStore>()
            .CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var instance = await services.GetRequiredService<IProviderConfigurationStore>()
            .CreateInstanceAsync(environment, new SampleConfiguration("sample-target", "sample.impl/v1"), default);
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        foreach (var payload in new[] { "[]", "null", "{bad" })
            await Assert.ThrowsAsync<PostgresException>(async () => await InsertAsync(payload, false));
        await Assert.ThrowsAsync<PostgresException>(async () => await InsertAsync("{}", true));
        Assert.Equal(0L, (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM provider_configuration_revisions", database).ExecuteScalarAsync())!);

        async Task InsertAsync(string payload, bool sealedAtInsert)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO provider_configuration_revisions
                    (provider_instance_id, revision_number, environment_record_id, implementation_id,
                     configuration_shape_id, payload_format_version, payload_json, is_sealed)
                SELECT id, 1, environment_record_id, 'sample.impl/v1', 'sample.configuration/v1', 1,
                    @payload, @sealed_at_insert FROM provider_instances WHERE id = @id
                """, database);
            insert.Parameters.AddWithValue("id", Guid.Parse(instance.ToString()));
            insert.Parameters.AddWithValue("payload", payload);
            insert.Parameters.AddWithValue("sealed_at_insert", sealedAtInsert);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private const string Implementation = "haby.postgresql.database/v1alpha1";
    private static ProviderConfigurationRevisionNumber Number(long value) => ProviderConfigurationRevisionNumber.Create(value);
    private static PostgreSqlProviderConfiguration Configuration(SecretRef secret, string host = "localhost",
        int port = 5432, string database = "postgres", string implementation = Implementation) =>
        new(1, host, port, database, "admin", secret, implementation);
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
    private static ServiceProvider Services(string connection, bool includeSample = true)
    {
        var services = new ServiceCollection().AddPostgreSqlManifestCatalog(connection)
            .AddPostgreSqlPlaintextSecretStore().AddPostgreSqlProviderConfigurationStore()
            .AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
            .AddPostgreSqlProviderConfigurationCodec();
        if (includeSample) services.AddSingleton<IProviderConfigurationCodec, SampleConfigurationCodec>();
        return services.BuildServiceProvider();
    }
}

internal sealed record SampleConfiguration(string Target, string ImplementationId);

internal sealed class SampleConfigurationCodec : ProviderConfigurationCodec<SampleConfiguration>
{
    public override string ProviderKindId => "sample/v1";
    public override string ShapeId => "sample.configuration/v1";
    public override int PayloadFormatVersion => 1;
    public override PreparedProviderConfiguration Encode(SampleConfiguration value)
    {
        if (string.IsNullOrWhiteSpace(value.Target) || value.ImplementationId != "sample.impl/v1")
            throw new InvalidOperationException("Sample configuration is invalid.");
        var json = JsonSerializer.Serialize(value, SampleConfigurationJsonContext.Default.SampleConfiguration);
        return new PreparedProviderConfiguration(ProviderKindId, new ProviderTargetIdentity(1, value.Target),
            value.ImplementationId, ShapeId, PayloadFormatVersion, json, []);
    }
    public override SampleConfiguration DecodeTyped(string payloadJson) => JsonSerializer.Deserialize(payloadJson,
        SampleConfigurationJsonContext.Default.SampleConfiguration)
        ?? throw new InvalidDataException("Sample configuration is invalid.");
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SampleConfiguration))]
internal partial class SampleConfigurationJsonContext : JsonSerializerContext;
