using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class ManifestCatalogPersistenceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string AnyObject = """{"type":"object"}""";
    private static readonly ManifestContractCatalog Catalog = new(
        [new(ResourceTypeId.Create("postgresql.database/v1alpha1"), AnyObject, AnyObject)],
        [new(WorkloadTypeId.Create("kubernetes.workload/v1alpha1"), AnyObject)],
        [new(OutputContractId.Create("postgresql.connection/v1alpha1"), AnyObject)],
        [new(ConfigurationRendererId.Create("postgresql.connection/v1alpha1"))]);
    private static readonly DateTimeOffset FirstTime = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_and_fresh_provider_preserve_complete_typed_revision_and_original_history()
    {
        var connection = await NewDatabaseAsync();
        var payload = CompleteEnvelope("v1");
        var candidate = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(payload, Catalog)).Candidate;
        ManifestRevisionRef reference;
        await using (var first = Services(connection))
        {
            await first.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var result = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
                await Import(first, FirstTime).ExecuteAsync(payload, "original-actor"));
            Assert.Equal(ImportApplicationManifestResult.ImportDisposition.Created, result.Disposition);
            Assert.Equal(1, result.ManifestRevisionRef.RevisionNumber.Value);
            reference = result.ManifestRevisionRef;
        }
        await using (var fresh = Services(connection))
        {
            await fresh.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var revision = await fresh.GetRequiredService<IManifestRevisionQuery>().FindAsync(reference);
            Assert.NotNull(revision);
            Assert.Equal(reference, revision.Reference);
            Assert.Equal(candidate.Declaration, revision.Declaration);
            Assert.Equal(candidate.Declaration.InputApiVersion, revision.InputApiVersion);
            Assert.Equal(NormalizedApplicationManifest.CurrentModelVersion, revision.NormalizedModelVersion);
            Assert.Equal("original-actor", revision.AuditMetadata.ActorId);
            Assert.Equal(FirstTime, revision.AuditMetadata.ImportedAt);
            Assert.Equal(candidate.Warnings.Diagnostics.Count, revision.HistoricalWarnings.Diagnostics.Count);

            var compact = Encoding.UTF8.GetBytes(JsonNode.Parse(Encoding.UTF8.GetString(payload))!.ToJsonString());
            Assert.NotEqual(payload, compact);
            var repeat = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
                await Import(fresh, FirstTime.AddHours(1)).ExecuteAsync(compact, "later-actor"));
            Assert.Equal(ImportApplicationManifestResult.ImportDisposition.ExistingEquivalent, repeat.Disposition);
            Assert.Equal(reference, repeat.ManifestRevisionRef);
            Assert.Equal("original-actor", repeat.AuditMetadata.ActorId);
            Assert.Equal(FirstTime, repeat.AuditMetadata.ImportedAt);

            var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload)
                .Replace("\"displayName\": \"Example\"", "\"displayName\": \"Changed\"", StringComparison.Ordinal));
            var conflict = Assert.IsType<ImportApplicationManifestResult.ApplicationVersionContentConflict>(
                await Import(fresh, FirstTime).ExecuteAsync(changed, "later-actor"));
            Assert.Equal(reference, conflict.ExistingManifestRevisionRef);
            Assert.Contains("/metadata/displayName", conflict.ChangedPaths);
            Assert.DoesNotContain("Changed", string.Join("", conflict.ChangedPaths));

            var second = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
                await Import(fresh, FirstTime).ExecuteAsync(CompleteEnvelope("v2"), "second-actor"));
            Assert.Equal(2, second.ManifestRevisionRef.RevisionNumber.Value);
            Assert.Equal(reference.ApplicationRecordId, second.ManifestRevisionRef.ApplicationRecordId);
            Assert.IsType<ImportApplicationManifestResult.InvalidRequest>(
                await Import(fresh, FirstTime).ExecuteAsync(Encoding.UTF8.GetBytes("{bad"), "actor"));
        }
        await using var check = new NpgsqlConnection(connection);
        await check.OpenAsync();
        Assert.Equal(1, await ScalarAsync(check, "SELECT count(*) FROM applications"));
        Assert.Equal(2, await ScalarAsync(check, "SELECT count(*) FROM manifest_revisions"));
        Assert.Equal(5, await ScalarAsync(check, "SELECT count(*) FROM \"__EFMigrationsHistory\""));
        Assert.Equal(3, await ScalarAsync(check, "SELECT count(*) FROM pg_indexes WHERE schemaname='public' AND indexname LIKE 'IX_%' AND indexdef LIKE '%UNIQUE INDEX%' AND tablename IN ('applications','manifest_revisions')"));
    }

    [Fact]
    public async Task Invalid_first_import_and_precommit_cancellation_leave_no_application()
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var importer = Import(services, FirstTime);
        Assert.IsType<ImportApplicationManifestResult.InvalidManifest>(
            await importer.ExecuteAsync(Encoding.UTF8.GetBytes("""{"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"Bad_Id"},"spec":{}}}"""), "actor"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await importer.ExecuteAsync(CompleteEnvelope("v1"), "actor", canceled.Token));
        await using var check = new NpgsqlConnection(connection);
        await check.OpenAsync();
        Assert.Equal(0, await ScalarAsync(check, "SELECT count(*) FROM applications"));
        Assert.Equal(0, await ScalarAsync(check, "SELECT count(*) FROM manifest_revisions"));
    }

    [Fact]
    public async Task Fresh_provider_returns_original_historical_warning()
    {
        var connection = await NewDatabaseAsync();
        const string envelope = """{"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"warning-app"},"spec":{"resources":{"db":{"type":"postgresql.database/v1alpha1","providerRef":"postgresql.primary","properties":{}}}}}}""";
        ManifestRevisionRef reference;
        await using (var first = Services(connection))
        {
            await first.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var created = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
                await Import(first, FirstTime).ExecuteAsync(Encoding.UTF8.GetBytes(envelope), "first-actor"));
            Assert.Single(created.HistoricalWarnings.Diagnostics);
            reference = created.ManifestRevisionRef;
        }
        await using var fresh = Services(connection);
        var revision = await fresh.GetRequiredService<IManifestRevisionQuery>().FindAsync(reference);
        Assert.NotNull(revision);
        var warning = Assert.Single(revision.HistoricalWarnings.Diagnostics);
        Assert.Equal("manifest.resource.unused", warning.Code);
        Assert.Equal("first-actor", revision.AuditMetadata.ActorId);
        var repeat = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
            await Import(fresh, FirstTime.AddDays(1)).ExecuteAsync(Encoding.UTF8.GetBytes(envelope), "later-actor"));
        Assert.Equal(reference, repeat.ManifestRevisionRef);
        Assert.Equal("first-actor", repeat.AuditMetadata.ActorId);
        var repeatedWarning = Assert.Single(repeat.HistoricalWarnings.Diagnostics);
        Assert.Equal(warning.Code, repeatedWarning.Code);
        Assert.Equal(warning.Path, repeatedWarning.Path);
        Assert.Equal(warning.Severity, repeatedWarning.Severity);
        Assert.Equal(warning.Parameters.OrderBy(item => item.Key), repeatedWarning.Parameters.OrderBy(item => item.Key));
        Assert.Equal(warning.RelatedPaths, repeatedWarning.RelatedPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_imports_preserve_one_winner(bool different)
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var payload = CompleteEnvelope("v1");
        var alternative = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload)
            .Replace("\"displayName\": \"Example\"", "\"displayName\": \"Changed\"", StringComparison.Ordinal));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 8).Select(async index =>
        {
            await gate.Task;
            return await Import(services, FirstTime.AddMinutes(index)).ExecuteAsync(
                different && index % 2 == 1 ? alternative : payload, $"actor-{index}");
        }).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);
        var created = Assert.Single(results.OfType<ImportApplicationManifestResult.Succeeded>(),
            result => result.Disposition == ImportApplicationManifestResult.ImportDisposition.Created);
        Assert.All(results, result => Assert.True(result is ImportApplicationManifestResult.Succeeded or
            ImportApplicationManifestResult.ApplicationVersionContentConflict));
        await using var check = new NpgsqlConnection(connection);
        await check.OpenAsync();
        Assert.Equal(1, await ScalarAsync(check, "SELECT count(*) FROM applications"));
        Assert.Equal(1, await ScalarAsync(check, "SELECT count(*) FROM manifest_revisions"));
        var winner = await services.GetRequiredService<IManifestRevisionQuery>().FindAsync(created.ManifestRevisionRef);
        Assert.NotNull(winner);
        Assert.Equal(created.AuditMetadata, winner.AuditMetadata);
        var winnerIndex = int.Parse(created.AuditMetadata.ActorId.AsSpan("actor-".Length),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(different && winnerIndex % 2 == 1 ? "Changed" : "Example",
            winner.Declaration.Metadata.DisplayName);
        foreach (var result in results)
        {
            if (result is ImportApplicationManifestResult.Succeeded succeeded)
                Assert.Equal(created.ManifestRevisionRef, succeeded.ManifestRevisionRef);
            else
                Assert.Equal(created.ManifestRevisionRef,
                    ((ImportApplicationManifestResult.ApplicationVersionContentConflict)result).ExistingManifestRevisionRef);
        }
        if (!different) Assert.Equal(7, results.OfType<ImportApplicationManifestResult.Succeeded>()
            .Count(result => result.Disposition == ImportApplicationManifestResult.ImportDisposition.ExistingEquivalent));
    }

    [Fact]
    public async Task Corrupt_and_unsupported_snapshots_fail_visibly()
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var result = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
            await Import(services, FirstTime).ExecuteAsync(CompleteEnvelope("v1"), "actor"));
        var query = services.GetRequiredService<IManifestRevisionQuery>();
        await using var check = new NpgsqlConnection(connection);
        await check.OpenAsync();
        await using (var unsupported = new NpgsqlCommand("UPDATE manifest_revisions SET snapshot_format_version=999", check))
            await unsupported.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await query.FindAsync(result.ManifestRevisionRef));
        await using (var corrupt = new NpgsqlCommand("UPDATE manifest_revisions SET snapshot_format_version=1, snapshot='{bad'", check))
            await corrupt.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await query.FindAsync(result.ManifestRevisionRef));
    }

    [Theory]
    [InlineData("{\"Diagnostics\":{}")]
    [InlineData("{\"Diagnostics\":{},\"Truncated\":false}")]
    public async Task Corrupt_historical_warnings_fail_during_fresh_rehydration(string storedWarnings)
    {
        var connection = await NewDatabaseAsync();
        ManifestRevisionRef reference;
        await using (var first = Services(connection))
        {
            await first.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var created = Assert.IsType<ImportApplicationManifestResult.Succeeded>(
                await Import(first, FirstTime).ExecuteAsync(CompleteEnvelope("v1"), "actor"));
            reference = created.ManifestRevisionRef;
        }

        await using (var check = new NpgsqlConnection(connection))
        {
            await check.OpenAsync();
            await using var corrupt = new NpgsqlCommand(
                "UPDATE manifest_revisions SET historical_warnings=@warnings", check);
            corrupt.Parameters.AddWithValue("warnings", storedWarnings);
            await corrupt.ExecuteNonQueryAsync();
        }

        await using var fresh = Services(connection);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fresh.GetRequiredService<IManifestRevisionQuery>().FindAsync(reference));
    }

    [Fact]
    public async Task Database_constraints_reject_all_three_duplicate_identities()
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        Assert.IsType<ImportApplicationManifestResult.Succeeded>(
            await Import(services, FirstTime).ExecuteAsync(CompleteEnvelope("v1"), "actor"));
        await using var check = new NpgsqlConnection(connection);
        await check.OpenAsync();
        await AssertUniqueViolation(check,
            "INSERT INTO applications (id, application_id) VALUES (@id, 'example.service')");
        const string copy = "INSERT INTO manifest_revisions (id, application_record_id, application_id, application_version, local_revision_number, input_api_version, normalized_model_version, snapshot_format_version, snapshot, historical_warnings, actor_id, imported_at) " +
            "SELECT @id, application_record_id, application_id, {0}, local_revision_number, input_api_version, normalized_model_version, snapshot_format_version, snapshot, historical_warnings, actor_id, imported_at FROM manifest_revisions LIMIT 1";
        await AssertUniqueViolation(check, string.Format(System.Globalization.CultureInfo.InvariantCulture, copy, "application_version"));
        await AssertUniqueViolation(check, string.Format(System.Globalization.CultureInfo.InvariantCulture, copy, "'v2'"));
        Assert.Equal(1, await ScalarAsync(check, "SELECT count(*) FROM applications"));
        Assert.Equal(1, await ScalarAsync(check, "SELECT count(*) FROM manifest_revisions"));
    }

    [Fact]
    public async Task Failed_first_revision_insert_rolls_back_its_application()
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await using var check = new NpgsqlConnection(connection);
        await check.OpenAsync();
        await using (var command = new NpgsqlCommand("CREATE FUNCTION reject_revision() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test rejection'; END $$; CREATE TRIGGER reject_revision BEFORE INSERT ON manifest_revisions FOR EACH ROW EXECUTE FUNCTION reject_revision()", check))
            await command.ExecuteNonQueryAsync();
        var error = await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await Import(services, FirstTime).ExecuteAsync(CompleteEnvelope("v1"), "actor"));
        Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(0, await ScalarAsync(check, "SELECT count(*) FROM applications"));
        Assert.Equal(0, await ScalarAsync(check, "SELECT count(*) FROM manifest_revisions"));
    }

    [Fact]
    public async Task Migration_failure_is_visible_to_startup()
    {
        var connection = await NewDatabaseAsync();
        await using (var check = new NpgsqlConnection(connection))
        {
            await check.OpenAsync();
            await using var obstruction = new NpgsqlCommand("CREATE TABLE applications (unexpected integer)", check);
            await obstruction.ExecuteNonQueryAsync();
        }
        await using var services = Services(connection);
        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync());
        Assert.Equal(PostgresErrorCodes.DuplicateTable, error.SqlState);
        await using var verify = new NpgsqlConnection(connection);
        await verify.OpenAsync();
        Assert.Equal(0, await ScalarAsync(verify, "SELECT count(*) FROM \"__EFMigrationsHistory\""));
    }

    private async Task<string> NewDatabaseAsync()
    {
        var name = "haby_catalog_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    private static ServiceProvider Services(string connection) => new ServiceCollection()
        .AddPostgreSqlManifestCatalog(connection).BuildServiceProvider();

    private static ImportApplicationManifest Import(IServiceProvider services, DateTimeOffset now) => new(
        services.GetRequiredService<IManifestImportStore>(), Catalog, new FixedTimeProvider(now));

    private static byte[] CompleteEnvelope(string version)
    {
        using var stream = typeof(ManifestCatalogPersistenceTests).Assembly
            .GetManifestResourceStream("Haby.PostgreSql.IntegrationTests.complete.json")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var manifest = reader.ReadToEnd();
        return Encoding.UTF8.GetBytes("{\"applicationVersion\":\"" + version + "\",\"manifest\":" + manifest + "}");
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task AssertUniqueViolation(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
