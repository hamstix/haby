using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class AdministrativeSecretTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Equivalent_retry_reuses_transaction_connection_with_a_single_connection_pool()
    {
        var connection = new NpgsqlConnectionStringBuilder(await DatabaseAsync()) { MaxPoolSize = 1, Timeout = 3 }.ConnectionString;
        await using var services = Services(connection);
        var request = Guid.NewGuid();
        Assert.Equal(SecretOutcome.Created, (await Create(services, "admin", request)).Outcome);
        Assert.Equal(SecretOutcome.ExistingEquivalent, (await Create(services, "admin", request)).Outcome);
        Assert.Equal(SecretOutcome.ExistingEquivalent, (await Create(services, "admin", Guid.NewGuid())).Outcome);
    }

    [Fact]
    public async Task Database_independently_enforces_name_number_and_receipt_uniqueness()
    {
        var connection = await DatabaseAsync();
        var request = Guid.NewGuid();
        await using (var services = Services(connection)) Assert.Equal(SecretOutcome.Created, (await Create(services, "admin", request)).Outcome);
        await using var check = Context(connection);
        var secret = await check.Secrets.AsNoTracking().SingleAsync();
        var version = await check.SecretVersions.AsNoTracking().SingleAsync();
        var receipt = await check.SecretCreationReceipts.AsNoTracking().SingleAsync();
        var otherSecret = Guid.Parse(SecretId.New().ToString());
        var duplicateName = await Assert.ThrowsAsync<PostgresException>(() => check.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO secrets (id, environment_record_id, name, owner, purpose, sensitivity, content_type, storage_backend, ownership_policy) VALUES ({otherSecret}, {secret.EnvironmentRecordId}, 'admin', 'Environment', 'ProviderAdministrator', 'Credential', 'application/octet-stream', 'PostgreSqlPlaintext', 'Managed')"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateName.SqlState);
        Assert.Equal("IX_secrets_environment_record_id_name", duplicateName.ConstraintName);
        var duplicateNumber = await Assert.ThrowsAsync<PostgresException>(() => check.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO secret_versions (id, secret_id, number, protection_format, format_version) VALUES ({Guid.Parse(SecretVersionId.New().ToString())}, {secret.Id}, 1, 'Plaintext', 1)"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateNumber.SqlState);
        Assert.Equal("IX_secret_versions_secret_id_number", duplicateNumber.ConstraintName);
        var duplicateReceipt = await Assert.ThrowsAsync<PostgresException>(() => check.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO secret_creation_receipts (environment_record_id, operation, request_id, name, secret_id, version_id, actor, created_at) VALUES ({secret.EnvironmentRecordId}, {receipt.Operation}, {request}, 'admin', {secret.Id}, {version.Id}, 'actor', {DateTimeOffset.UtcNow})"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateReceipt.SqlState);
        Assert.Equal("PK_secret_creation_receipts", duplicateReceipt.ConstraintName);
    }

    [Fact]
    public async Task Receipts_name_equivalence_conflicts_restart_and_exact_initial_version()
    {
        var connection = await DatabaseAsync();
        var request = Guid.NewGuid();
        SecretMetadata original;
        await using (var services = Services(connection))
        {
            var useCase = services.GetRequiredService<SecretAdministration>();
            var created = await Create(services, "admin", request);
            Assert.Equal(SecretOutcome.Created, created.Outcome);
            original = created.Secret!;
            Assert.Equal(1, Assert.Single(original.Versions).Number);
            Assert.Equal(SecretOutcome.ExistingEquivalent, (await Create(services, "admin", request)).Outcome);
            var equivalent = await Create(services, "admin", Guid.NewGuid());
            Assert.Equal(SecretOutcome.ExistingEquivalent, equivalent.Outcome);
            Assert.Equal(original.SecretId, equivalent.Secret!.SecretId);
            Assert.Equal(SecretOutcome.RequestContentConflict, (await Create(services, "admin", Guid.NewGuid(), [2])).Outcome);
            Assert.Equal(SecretOutcome.RequestContentConflict, (await Create(services, "admin", request, [2])).Outcome);
            Assert.Equal(SecretOutcome.RequestContentConflict, (await Create(services, "different", request)).Outcome);
            Assert.Equal(SecretOutcome.RequestContentConflict, (await Create(services, "admin", request, actor: "other")).Outcome);
            // Current version changes independently; create still returns and compares exact initial version 1.
            await services.GetRequiredService<ISecretStore>().AddVersionAsync(original.Environment,
                new SecretRef(original.SecretId, SecretVersionId.New()), [9], default);
            Assert.Equal(SecretOutcome.ExistingEquivalent, (await Create(services, "admin", Guid.NewGuid())).Outcome);
            Assert.Equal(SecretOutcome.RequestContentConflict, (await Create(services, "admin", Guid.NewGuid(), [9])).Outcome);
            Assert.Equal(SecretOutcome.NotFound, (await useCase.GetAsync("other", "admin", null, default)).Outcome);
            var isolated = await Create(services, "admin", request, [2], "other");
            Assert.Equal(SecretOutcome.Created, isolated.Outcome);
            Assert.NotEqual(original.SecretId, isolated.Secret!.SecretId);
        }
        // The first response can be discarded and recovered after process/service restart.
        await using (var restarted = Services(connection))
        {
            var retry = await Create(restarted, "admin", request);
            Assert.Equal(SecretOutcome.ExistingEquivalent, retry.Outcome);
            Assert.Equal(original.SecretId, retry.Secret!.SecretId);
            Assert.Equal(original.Versions[0].VersionId, Assert.Single(retry.Secret.Versions).VersionId);
        }
        await using var check = Context(connection);
        Assert.Equal(2, await check.Secrets.AsNoTracking().CountAsync());
        Assert.Equal(4, await check.SecretCreationReceipts.AsNoTracking().CountAsync());
        Assert.All(await check.SecretCreationReceipts.AsNoTracking().ToArrayAsync(), x => Assert.NotEqual(Guid.Empty, x.VersionId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Concurrent_requests_serialize_request_identity_and_name(bool sameRequest, bool incompatible)
    {
        var connection = await DatabaseAsync();
        await using var services = Services(connection);
        var request = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 12).Select(async i =>
        {
            await gate.Task;
            return await Create(services, incompatible && i % 2 == 1 ? "other-name" : "admin",
                sameRequest ? request : Guid.NewGuid(), incompatible && i % 2 == 1 ? [2] : [1]);
        }).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);
        Assert.Single(results, x => x.Outcome == SecretOutcome.Created);
        Assert.Equal(incompatible ? 6 : 0, results.Count(x => x.Outcome == SecretOutcome.RequestContentConflict));
        Assert.All(results.Where(x => x.Secret is not null), x => Assert.Equal(results.First(r => r.Secret is not null).Secret!.SecretId, x.Secret!.SecretId));
        await using var check = Context(connection);
        Assert.Equal(1, await check.Secrets.AsNoTracking().CountAsync());
        Assert.Equal(1, await check.SecretVersions.AsNoTracking().CountAsync());
        Assert.Equal(sameRequest ? 1 : 12, await check.SecretCreationReceipts.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Same_request_racing_different_values_or_different_names_cannot_create_twice()
    {
        var connection = await DatabaseAsync();
        await using var services = Services(connection);
        foreach (var sameName in new[] { true, false })
        {
            var request = Guid.NewGuid();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 2).Select(async i =>
            {
                await gate.Task;
                return await Create(services, sameName ? "value-race" : (i == 0 ? "name-a" : "name-b"), request, [(byte)(i + 1)]);
            }).ToArray();
            gate.SetResult();
            var results = await Task.WhenAll(tasks);
            Assert.Single(results, x => x.Outcome == SecretOutcome.Created);
            Assert.Single(results, x => x.Outcome == SecretOutcome.RequestContentConflict);
        }
        await using var check = Context(connection);
        Assert.Equal(2, await check.Secrets.AsNoTracking().CountAsync());
        Assert.Equal(2, await check.SecretCreationReceipts.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Receipt_insert_failure_rolls_back_secret_version_and_payload_and_cancellation_is_safe()
    {
        var connection = await DatabaseAsync();
        await using (var arrange = Context(connection))
            await arrange.Database.ExecuteSqlRawAsync("ALTER TABLE secret_creation_receipts ADD CONSTRAINT test_reject_actor CHECK (actor <> 'reject')");
        await using (var services = Services(connection))
        {
            Assert.Equal(SecretOutcome.SecretStorageUnavailable, (await Create(services, "admin", Guid.NewGuid(), actor: "reject")).Outcome);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            using var material = new SecretMaterial([1]);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await services.GetRequiredService<SecretAdministration>()
                .CreateAsync("pilot", "admin", Guid.NewGuid(), material, "actor", cancellation.Token));
        }
        await using var check = Context(connection);
        Assert.Empty(await check.Secrets.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.SecretVersions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.SecretPayloads.AsNoTracking().Select(x => x.SecretVersionId).ToArrayAsync());
        Assert.Empty(await check.SecretCreationReceipts.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Cancellation_after_secret_insert_before_receipt_commit_rolls_back_every_row()
    {
        var connection = await DatabaseAsync();
        using var cancellation = new CancellationTokenSource();
        var factory = new CancelFactory(connection, cancellation);
        var store = new PostgreSqlPlaintextSecretStore(factory);
        var useCase = new SecretAdministration(new PostgreSqlEnvironmentQuery(factory), new PostgreSqlAdministrativeSecretCatalog(factory, store));
        using var material = new SecretMaterial([1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await useCase.CreateAsync("pilot", "admin", Guid.NewGuid(), material, "actor", cancellation.Token));
        Assert.True(factory.Interceptor.SawPersistedSecret);
        await using var check = Context(connection);
        Assert.Equal(0, await check.Secrets.AsNoTracking().CountAsync());
        Assert.Equal(0, await check.SecretVersions.AsNoTracking().CountAsync());
        Assert.Equal(0, await check.SecretPayloads.AsNoTracking().CountAsync());
        Assert.Equal(0, await check.SecretCreationReceipts.AsNoTracking().CountAsync());
    }

    private sealed class CancelBeforeReceipt(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        internal bool SawPersistedSecret { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<SecretCreationReceiptRow>().Any())
            {
                SawPersistedSecret = eventData.Context.ChangeTracker.Entries<SecretRow>().Any(x => x.State != EntityState.Added);
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelFactory(string connection, CancellationTokenSource cancellation) : IDbContextFactory<ManifestCatalogDbContext>
    {
        internal CancelBeforeReceipt Interceptor { get; } = new(cancellation);
        public ManifestCatalogDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>()
            .UseNpgsql(connection).AddInterceptors(Interceptor).Options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_unsupported_material_fails_closed_without_receipt_or_creation(bool unsupported)
    {
        var connection = await DatabaseAsync();
        var request = Guid.NewGuid();
        await using (var services = Services(connection)) Assert.Equal(SecretOutcome.Created, (await Create(services, "admin", request)).Outcome);
        await using (var arrange = Context(connection))
        {
            // Test-only corruption bypasses old immutability guards; production does not alter them.
            if (unsupported)
            {
                await arrange.Database.ExecuteSqlRawAsync("ALTER TABLE secret_versions DISABLE TRIGGER secret_versions_immutable");
                await arrange.Database.ExecuteSqlRawAsync("UPDATE secret_versions SET protection_format = 'Unsupported'");
            }
            else
            {
                await arrange.Database.ExecuteSqlRawAsync("ALTER TABLE secret_payloads DISABLE TRIGGER secret_payloads_immutable");
                await arrange.Database.ExecuteSqlRawAsync("DELETE FROM secret_payloads");
            }
        }
        await using (var services = Services(connection))
        {
            Assert.Equal(SecretOutcome.SecretStorageUnavailable, (await Create(services, "admin", request)).Outcome);
            Assert.Equal(SecretOutcome.SecretStorageUnavailable, (await Create(services, "admin", Guid.NewGuid())).Outcome);
        }
        await using var check = Context(connection);
        Assert.Equal(1, await check.Secrets.AsNoTracking().CountAsync());
        Assert.Equal(1, await check.SecretVersions.AsNoTracking().CountAsync());
        Assert.Equal(1, await check.SecretCreationReceipts.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Server_validates_sizes_names_and_existing_environment_without_side_effects()
    {
        var connection = await DatabaseAsync();
        await using (var services = Services(connection))
        {
            foreach (var size in new[] { 0, 1, 65_536, 65_537 })
            {
                var result = await Create(services, "size-" + size, Guid.NewGuid(), new byte[size]);
                Assert.Equal(size is 1 or 65_536 ? SecretOutcome.Created : SecretOutcome.InvalidRequest, result.Outcome);
            }
            foreach (var name in new[] { "", "Bad", "bad--name", new string('a', 65), "bad\n" })
                Assert.Equal(SecretOutcome.InvalidRequest, (await Create(services, name, Guid.NewGuid())).Outcome);
            Assert.Equal(SecretOutcome.Created, (await Create(services, new string('a', 64), Guid.NewGuid())).Outcome);
            Assert.Equal(SecretOutcome.EnvironmentNotFound, (await Create(services, "admin", Guid.NewGuid(), environment: "absent")).Outcome);
            Assert.Equal(SecretOutcome.InvalidRequest, (await Create(services, "admin", Guid.Empty)).Outcome);
        }
        await using var check = Context(connection);
        Assert.Equal(2, await check.Environments.AsNoTracking().CountAsync());
        Assert.Equal(3, await check.Secrets.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Metadata_is_scoped_ordinal_bounded_and_live_with_secret_specific_cursors()
    {
        var connection = await DatabaseAsync();
        await using var services = Services(connection);
        var useCase = services.GetRequiredService<SecretAdministration>();
        var names = Enumerable.Range(0, 104).Select(i => "admin-" + i.ToString("D3")).Append(new string('a', 64)).ToArray();
        foreach (var name in names) Assert.Equal(SecretOutcome.Created, (await Create(services, name, Guid.NewGuid())).Outcome);
        foreach (var size in new[] { 1, 2, 0, 50, 100 })
        {
            var seen = new List<string>();
            string? cursor = null;
            do
            {
                var result = await useCase.ListAsync("pilot", size, cursor, default);
                Assert.Equal(SecretOutcome.Listed, result.Outcome);
                Assert.InRange(result.Items.Count, 1, size == 0 ? 50 : size);
                seen.AddRange(result.Items.Select(x => x.Name!));
                cursor = result.NextCursor;
            } while (cursor is not null);
            Assert.Equal(names.Order(StringComparer.Ordinal), seen);
        }
        var first = await useCase.ListAsync("pilot", 1, null, default);
        var prefix = "secret1:" + first.Items[0].Environment + ":";
        var unknownPosition = await useCase.ListAsync("pilot", 1, prefix + "admin-000a", default);
        Assert.Equal("admin-001", Assert.Single(unknownPosition.Items).Name);
        var beyondEnd = await useCase.ListAsync("pilot", 1, prefix + "zzzz", default);
        Assert.Empty(beyondEnd.Items);
        Assert.Null(beyondEnd.NextCursor);
        Assert.Equal(SecretOutcome.InvalidCursor, (await useCase.ListAsync("other", 1, first.NextCursor, default)).Outcome);
        foreach (var cursor in new[] { "env1:a", "secret2:a", "secret1:", first.NextCursor + "\n", first.NextCursor + new string('a', 100) })
            Assert.Equal(SecretOutcome.InvalidCursor, (await useCase.ListAsync("pilot", 1, cursor, default)).Outcome);
        foreach (var size in new[] { -1, 101, int.MaxValue })
            Assert.Equal(SecretOutcome.InvalidPageSize, (await useCase.ListAsync("pilot", size, null, default)).Outcome);
        var selected = first.Items[0];
        Assert.Equal(selected.SecretId, (await useCase.GetAsync("pilot", selected.Name, null, default)).Secret!.SecretId);
        Assert.Equal(selected.SecretId, (await useCase.GetAsync("pilot", null, selected.SecretId.ToString(), default)).Secret!.SecretId);
        Assert.Equal(SecretOutcome.NotFound, (await useCase.GetAsync("other", null, selected.SecretId.ToString(), default)).Outcome);
        Assert.Equal(SecretOutcome.InvalidRequest, (await useCase.GetAsync("pilot", "admin", selected.SecretId.ToString(), default)).Outcome);
        Assert.Equal(SecretOutcome.InvalidRequest, (await useCase.GetAsync("pilot", null, "bad", default)).Outcome);
        var page = await useCase.ListAsync("pilot", 100, null, default);
        await Create(services, "zzz", Guid.NewGuid());
        var tail = await useCase.ListAsync("pilot", 100, page.NextCursor, default);
        Assert.Contains(tail.Items, x => x.Name == "zzz");
        Assert.Null(tail.NextCursor);
    }

    [Fact]
    public async Task Legacy_unnamed_items_follow_names_with_identity_cursor_without_invented_metadata()
    {
        var connection = await DatabaseAsync();
        await using var services = Services(connection);
        var created = await Create(services, "admin", Guid.NewGuid());
        var store = services.GetRequiredService<ISecretStore>();
        for (var i = 0; i < 2; i++)
            await store.CreateAsync(created.Secret!.Environment, new SecretRef(SecretId.New(), SecretVersionId.New()), [1], default);
        var useCase = services.GetRequiredService<SecretAdministration>();
        var page = await useCase.ListAsync("pilot", 1, null, default);
        Assert.Equal("admin", Assert.Single(page.Items).Name);
        page = await useCase.ListAsync("pilot", 1, page.NextCursor, default);
        Assert.Null(Assert.Single(page.Items).Name);
        Assert.Contains(":~", page.NextCursor!);
        var tail = await useCase.ListAsync("pilot", 1, page.NextCursor, default);
        Assert.Null(Assert.Single(tail.Items).Name);
        Assert.NotEqual(page.Items[0].SecretId, tail.Items[0].SecretId);
        Assert.Null(tail.NextCursor);
        Assert.Equal(SecretOutcome.InvalidCursor, (await useCase.ListAsync("other", 1, page.NextCursor, default)).Outcome);
    }

    [Fact]
    public async Task Migration_preserves_unnamed_exact_references_without_assigning_names_numbers_or_dates()
    {
        var connection = await DatabaseAsync(migrate: false);
        var environment = Guid.Parse(EnvironmentRecordId.New().ToString());
        var secret = Guid.Parse(SecretId.New().ToString());
        var version = Guid.Parse(SecretVersionId.New().ToString());
        await using (var arrange = Context(connection))
        {
            await arrange.GetService<IMigrator>().MigrateAsync("20261027175823_AddPostgreSqlDatabaseRecoveryDecision");
            await arrange.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environments (id, environment_id) VALUES ({environment}, 'legacy')");
            await arrange.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO secrets (id, environment_record_id, owner, purpose, sensitivity, content_type, storage_backend, ownership_policy) VALUES ({secret}, {environment}, 'Environment', 'ProviderAdministrator', 'Credential', 'application/octet-stream', 'PostgreSqlPlaintext', 'Managed')");
            await arrange.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO secret_versions (id, secret_id, protection_format, format_version) VALUES ({version}, {secret}, 'Plaintext', 1)");
            await arrange.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO secret_payloads (secret_version_id, bytes) VALUES ({version}, {new byte[] { 1 }})");
            await arrange.Database.ExecuteSqlInterpolatedAsync($"UPDATE secrets SET current_version_id = {version} WHERE id = {secret}");
        }
        await using (var act = Context(connection)) await act.Database.MigrateAsync();
        await using (var services = Services(connection))
        {
            var reference = new SecretRef(SecretId.Parse(secret.ToString()), SecretVersionId.Parse(version.ToString()));
            using var material = await services.GetRequiredService<ISecretStore>().ResolveAsync(EnvironmentRecordId.Parse(environment.ToString()), reference, default);
            Assert.NotNull(material);
            Assert.True(material.CopyBytes().AsSpan().SequenceEqual(new byte[] { 1 }), "Exact stored bytes must remain unchanged.");
            var metadata = await services.GetRequiredService<SecretAdministration>().GetAsync("legacy", null, secret.ToString(), default);
            Assert.Equal(SecretOutcome.Found, metadata.Outcome);
            Assert.Null(metadata.Secret!.Name);
            Assert.Null(metadata.Secret.CreatedAt);
            Assert.Null(Assert.Single(metadata.Secret.Versions).Number);
            var page = await services.GetRequiredService<SecretAdministration>().ListAsync("legacy", 1, null, default);
            Assert.Equal(SecretOutcome.Listed, page.Outcome);
            Assert.Null(Assert.Single(page.Items).Name);
        }
        await using var check = Context(connection);
        Assert.Equal(secret, (await check.Secrets.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(version, (await check.SecretVersions.AsNoTracking().SingleAsync()).Id);
        Assert.Empty(await check.SecretCreationReceipts.AsNoTracking().ToArrayAsync());
    }

    private static async Task<SecretResult> Create(ServiceProvider services, string name, Guid request,
        byte[]? bytes = null, string environment = "pilot", string actor = "actor")
    {
        using var material = new SecretMaterial(bytes ?? [1]);
        return await services.GetRequiredService<SecretAdministration>().CreateAsync(environment, name, request, material, actor, default);
    }

    private async Task<string> DatabaseAsync(bool migrate = true)
    {
        var name = "haby_admin_secret_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        var result = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
        if (migrate)
        {
            await using var services = Services(result);
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var catalog = services.GetRequiredService<IEnvironmentCatalog>();
            await catalog.CreateOrGetAsync(EnvironmentId.Create("pilot"), default);
            await catalog.CreateOrGetAsync(EnvironmentId.Create("other"), default);
        }
        return result;
    }
    private static ManifestCatalogDbContext Context(string connection) => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
    private static ServiceProvider Services(string connection) => new ServiceCollection().AddPostgreSqlManifestCatalog(connection)
        .AddPostgreSqlPlaintextSecretStore().BuildServiceProvider();
}
