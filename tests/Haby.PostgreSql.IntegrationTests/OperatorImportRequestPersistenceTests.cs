using System.Data.Common;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

/// <summary>Catalog request atomicity only; no external provider verification is performed.</summary>
[Collection("Catalog database group")]
public sealed class OperatorImportRequestPersistenceTests(CatalogServerFixture fixture) : CatalogTestCase(fixture)
{
    [Fact]
    public async Task Pending_binding_and_private_stage_survive_restart_without_claiming_completion()
    {
        var connection = await CreateDatabaseAsync("operator_import_");
        var binding = await Arrange(connection);
        using var password = Password();
        await using (var bind = Services(connection))
        {
            var store = bind.GetRequiredService<IOperatorImportRequestStore>();
            Assert.Equal(OperatorImportRequestStatus.NotFound, (await store.GetAsync(binding.RequestId, default)).Status);
            Assert.Equal(OperatorImportRequestStatus.Pending, (await store.BindAsync(binding, "original", default)).Status);
        }
        await using (var stage = Services(connection))
            Assert.Equal(ProtectedCredentialStageStatus.Staged, (await stage.GetRequiredService<IProtectedApplicationCredentialIntake>()
                .StageAsync(binding.RequestId, binding.CredentialOwner, password, default)).Status);
        OperatorImportRequestView saved;
        await using (var restart = Services(connection))
        {
            var service = restart.GetRequiredService<OperatorImportRequests>();
            var pending = await service.GetAsync(binding.RequestId, default);
            Assert.Equal(OperatorImportRequestStatus.Pending, pending.Status);
            Assert.Null(pending.Request!.ChangeSetId);
            Assert.Null(pending.Request.CompletedBy);
            var result = await service.CreateAsync(binding, password, "completing", default);
            Assert.Equal(OperatorImportRequestStatus.Created, result.Status);
            saved = result.Request!;
            Assert.Equal("original", saved.CreatedBy);
            Assert.Equal("completing", saved.CompletedBy);
        }
        await using (var replay = Services(connection))
        {
            var service = replay.GetRequiredService<OperatorImportRequests>();
            var result = await service.CreateAsync(binding, password, "later-operator", default);
            Assert.Equal(OperatorImportRequestStatus.Existing, result.Status);
            Assert.Equal(saved, result.Request);
            using var different = Password();
            Assert.Equal(OperatorImportRequestStatus.Conflict, (await service.CreateAsync(binding, different, "later", default)).Status);
            Assert.Equal(saved, (await service.GetAsync(binding.RequestId, default)).Request);
        }
        await AssertCounts(connection, 1, 1, 1);
    }

    [Fact]
    public async Task Concurrent_replays_link_one_changeset_and_preserve_first_successful_actor()
    {
        var connection = await CreateDatabaseAsync("operator_race_");
        var binding = await Arrange(connection);
        using var password = Password();
        await using var services = Services(connection);
        var service = services.GetRequiredService<OperatorImportRequests>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            service.CreateAsync(binding, password, "operator-" + i, timeout.Token).AsTask()));
        Assert.All(results, x => Assert.Contains(x.Status, new[] { OperatorImportRequestStatus.Created, OperatorImportRequestStatus.Existing }));
        Assert.Single(results, x => x.Status == OperatorImportRequestStatus.Created);
        Assert.All(results, x => Assert.Equal(results[0].Request, x.Request));
        await AssertCounts(connection, 1, 1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transaction_fault_rolls_back_both_rows_or_recovers_the_original_link(bool afterCommit)
    {
        var connection = await CreateDatabaseAsync("operator_fault_");
        var binding = await Arrange(connection);
        using var password = Password();
        var fault = new LinkCommitFault(binding.RequestId, afterCommit);
        await using (var act = Services(connection, fault))
        {
            var result = await act.GetRequiredService<OperatorImportRequests>().CreateAsync(binding, password, "first", default);
            Assert.True(fault.Thrown);
            Assert.Equal(afterCommit ? OperatorImportRequestStatus.Existing : OperatorImportRequestStatus.StorageUnavailable, result.Status);
        }
        await AssertCounts(connection, 1, 1, afterCommit ? 1 : 0);
        await using (var restart = Services(connection))
        {
            var service = restart.GetRequiredService<OperatorImportRequests>();
            Assert.Equal(afterCommit ? OperatorImportRequestStatus.Created : OperatorImportRequestStatus.Pending,
                (await service.GetAsync(binding.RequestId, default)).Status);
            var retry = await service.CreateAsync(binding, password, "recovery", default);
            Assert.Equal(afterCommit ? OperatorImportRequestStatus.Existing : OperatorImportRequestStatus.Created, retry.Status);
            Assert.Equal("first", retry.Request!.CreatedBy);
            Assert.Equal(afterCommit ? "first" : "recovery", retry.Request.CompletedBy);
        }
        await AssertCounts(connection, 1, 1, 1);
    }

    [Fact]
    public async Task New_uuid_does_not_replace_an_unconfirmed_owner_stage_before_changeset_creation()
    {
        var connection = await CreateDatabaseAsync("operator_owner_");
        var binding = await Arrange(connection);
        using var password = Password();
        await using (var original = Services(connection))
        {
            await original.GetRequiredService<IOperatorImportRequestStore>().BindAsync(binding, "original", default);
            await original.GetRequiredService<IProtectedApplicationCredentialIntake>().StageAsync(binding.RequestId, binding.CredentialOwner, password, default);
        }
        var second = binding with { RequestId = Guid.NewGuid() };
        await using (var act = Services(connection))
        {
            var service = act.GetRequiredService<OperatorImportRequests>();
            Assert.Equal(OperatorImportRequestStatus.Conflict, (await service.CreateAsync(second, password, "second", default)).Status);
            Assert.Equal(OperatorImportRequestStatus.Pending, (await service.GetAsync(second.RequestId, default)).Status);
            Assert.Equal(OperatorImportRequestStatus.Created, (await service.CreateAsync(binding, password, "original", default)).Status);
            Assert.Equal(OperatorImportRequestStatus.Conflict, (await service.CreateAsync(binding with { CredentialResource = ManifestLocalId.Create("db") }, password, "other", default)).Status);
        }
        await AssertCounts(connection, 2, 1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_commit_rolls_back_but_lost_commit_cancellation_recovers_link(bool afterCommit)
    {
        var connection = await CreateDatabaseAsync("operator_cancel_");
        var binding = await Arrange(connection);
        using var password = Password();
        using var canceled = new CancellationTokenSource();
        var boundary = new LinkCancellation(binding.RequestId, canceled, afterCommit);
        await using (var act = Services(connection, boundary))
        {
            var task = act.GetRequiredService<OperatorImportRequests>().CreateAsync(binding, password, "first", canceled.Token).AsTask();
            if (afterCommit) Assert.Equal(OperatorImportRequestStatus.Existing, (await task).Status);
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.True(boundary.Triggered);
        }
        await AssertCounts(connection, 1, 1, afterCommit ? 1 : 0);
        await using var restart = Services(connection);
        var retry = await restart.GetRequiredService<OperatorImportRequests>().CreateAsync(binding, password, "recovery", default);
        Assert.Equal(afterCommit ? OperatorImportRequestStatus.Existing : OperatorImportRequestStatus.Created, retry.Status);
        Assert.Equal("first", retry.Request!.CreatedBy);
        Assert.Equal(afterCommit ? "first" : "recovery", retry.Request.CompletedBy);
    }

    private static async Task<OperatorImportRequestBinding> Arrange(string connection, bool migrate = true)
    {
        await using var services = Services(connection);
        if (migrate) await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var environment = await services.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), new([PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition], [], [], []),
            TimeProvider.System).ExecuteAsync(PostgreSqlPairPlanningTests.Bytes(PostgreSqlPairPlanningTests.Example()), "manifest-author")).ManifestRevisionRef;
        var input = PostgreSqlImportContracts.Encode(new("db", "app-login", "existing_database", 123, "app_login", 124, "public", 125, 125));
        return new(Guid.NewGuid(), environment, revision, ManifestLocalId.Create("app-login"),
            new([new(ManifestLocalId.Create("db"), input), new(ManifestLocalId.Create("app-login"), input)]));
    }

    [Fact]
    public async Task Linkage_uses_one_catalog_connection_without_nested_revision_lookup()
    {
        var connection = await CreateDatabaseAsync("operator_pool_");
        var binding = await Arrange(connection);
        var pooled = new NpgsqlConnectionStringBuilder(connection) { Pooling = true, MaxPoolSize = 1, Timeout = 3 }.ConnectionString;
        try
        {
            await using var services = Services(pooled);
            using var password = Password();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.Equal(OperatorImportRequestStatus.Created, (await services.GetRequiredService<OperatorImportRequests>()
                .CreateAsync(binding, password, "operator", timeout.Token)).Status);
        }
        finally
        {
            using var pool = new NpgsqlConnection(pooled);
            NpgsqlConnection.ClearPool(pool);
        }
        await AssertCounts(connection, 1, 1, 1);
    }

    [Fact]
    public async Task Concurrent_changed_binding_has_one_immutable_winner_before_any_staging()
    {
        var connection = await CreateDatabaseAsync("operator_binding_race_");
        var first = await Arrange(connection);
        var second = first with { CredentialResource = ManifestLocalId.Create("db") };
        var barrier = new BindingBarrier(first.RequestId);
        await using (var services = Services(connection, barrier))
        {
            var store = services.GetRequiredService<IOperatorImportRequestStore>();
            var results = await Task.WhenAll(store.BindAsync(first, "first", default).AsTask(), store.BindAsync(second, "second", default).AsTask());
            Assert.Single(results, x => x.Status == OperatorImportRequestStatus.Pending);
            Assert.Single(results, x => x.Status == OperatorImportRequestStatus.Conflict);
        }
        await using (var restart = Services(connection))
        {
            var store = restart.GetRequiredService<IOperatorImportRequestStore>();
            var saved = (await store.GetAsync(first.RequestId, default)).Request!;
            var winner = saved.CreatedBy == "first" ? first : second;
            var loser = saved.CreatedBy == "first" ? second : first;
            Assert.Equal(OperatorImportRequestStatus.Pending, (await store.BindAsync(winner, "later", default)).Status);
            Assert.Equal(OperatorImportRequestStatus.Conflict, (await store.BindAsync(loser, "later", default)).Status);
            Assert.Equal(saved, (await store.GetAsync(first.RequestId, default)).Request);
        }
        await AssertCounts(connection, 1, 0, 0);
    }

    [Fact]
    public async Task Persistence_refuses_binding_rewrite_delete_and_out_of_transaction_linkage()
    {
        var connection = await CreateDatabaseAsync("operator_guards_");
        var binding = await Arrange(connection);
        await using (var arrange = Services(connection))
            await arrange.GetRequiredService<IOperatorImportRequestStore>().BindAsync(binding, "original", default);
        for (var attack = 0; attack < 3; attack++)
        {
            await using var services = Services(connection);
            await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            var row = await db.OperatorImportRequestRecords.SingleAsync();
            if (attack == 0) row.CreatedBy = "replacement";
            else if (attack == 1) db.OperatorImportRequestRecords.Remove(row);
            else
            {
                row.ChangeSet = Guid.NewGuid(); row.StagedCredential = Guid.NewGuid();
                row.CompletedAt = DateTimeOffset.UtcNow; row.CompletedBy = "replacement";
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        using var password = Password();
        await using (var complete = Services(connection))
            Assert.Equal(OperatorImportRequestStatus.Created, (await complete.GetRequiredService<OperatorImportRequests>()
                .CreateAsync(binding, password, "completing", default)).Status);
        await using (var mutate = Services(connection))
        await using (var db = await mutate.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            var row = await db.OperatorImportRequestRecords.SingleAsync();
            row.CompletedBy = "replacement";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await AssertCounts(connection, 1, 1, 1);
    }

    private static SecretMaterial Password() => new(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString() + ";existing"));

    [Fact]
    public async Task Additive_migration_preserves_previous_private_stage_and_import_changeset()
    {
        var connection = await CreateDatabaseAsync("operator_upgrade_");
        await using (var previous = Services(connection))
        await using (var db = await previous.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            var migrations = db.Database.GetMigrations().ToArray();
            var current = Array.FindIndex(migrations, x => x.EndsWith("_PersistOperatorImportRequests", StringComparison.Ordinal));
            await db.GetService<IMigrator>().MigrateAsync(migrations[current - 1]);
        }
        var binding = await Arrange(connection, false);
        using var password = Password();
        ProtectedCredentialReference staged;
        var change = DeploymentChangeSetId.New();
        string[] previousMigrations;
        var created = DateTimeOffset.UtcNow;
        await using (var previous = Services(connection))
        {
            // Arrange the historical schema directly: current intake requires the new owner-reservation table.
            await using (var db = await previous.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            {
                var row = new ProtectedApplicationCredentialRow
                {
                    Id = Guid.CreateVersion7(), Request = binding.RequestId, Environment = Guid.Parse(binding.Environment.ToString()),
                    Application = Guid.Parse(binding.Revision.ApplicationRecordId.ToString()), Resource = binding.CredentialResource.Value,
                    Secret = Guid.CreateVersion7(), Version = Guid.CreateVersion7(), Material = password.CopyBytes()
                };
                try
                {
                    db.ProtectedApplicationCredentials.Add(row);
                    await db.SaveChangesAsync();
                    staged = new(row.Id, new(SecretId.Parse(row.Secret.ToString()), SecretVersionId.Parse(row.Version.ToString())));
                }
                finally { Array.Clear(row.Material); }
            }
            // Historical schema has no acquisition_intent_json; do not invoke the current planner.
            await using var historical = await previous.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            previousMigrations = (await historical.Database.GetAppliedMigrationsAsync()).ToArray();
            var intent = ResourceImportIntentCodec.Encode(binding.Attach(staged));
            await historical.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO deployment_change_sets
                    (id, environment_record_id, application_record_id, revision_number, expected_absence, created_at, actor_id, import_intent_json)
                VALUES ({Guid.Parse(change.ToString())}, {Guid.Parse(binding.Environment.ToString())},
                    {Guid.Parse(binding.Revision.ApplicationRecordId.ToString())}, {binding.Revision.RevisionNumber.Value},
                    {true}, {created}, {"previous-operator"}, {intent})
                """);
        }
        await using (var migrate = Services(connection))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => migrate.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync());
            Assert.Contains("fresh catalog", error.Message);
        }
        await using (var verify = Services(connection))
        {
            await using var db = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.Equal(previousMigrations, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            var savedStage = await db.ProtectedApplicationCredentials.AsNoTracking().SingleAsync();
            var material = password.CopyBytes();
            try
            {
                Assert.True(material.AsSpan().SequenceEqual(savedStage.Material), "Staged material changed.");
                Assert.Equal(binding.RequestId, savedStage.Request);
                Assert.Equal(Guid.Parse(binding.Environment.ToString()), savedStage.Environment);
                Assert.Equal(Guid.Parse(binding.Revision.ApplicationRecordId.ToString()), savedStage.Application);
                Assert.Equal(binding.CredentialResource.Value, savedStage.Resource);
                Assert.Equal(staged.Id, savedStage.Id);
                Assert.Equal(staged.Reference.SecretId.ToString(), savedStage.Secret.ToString());
                Assert.Equal(staged.Reference.SecretVersionId.ToString(), savedStage.Version.ToString());
            }
            finally { Array.Clear(material); Array.Clear(savedStage.Material); }
            await using var sql = new NpgsqlConnection(connection); await sql.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT id, actor_id, import_intent_json, created_at FROM deployment_change_sets", sql);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(Guid.Parse(change.ToString()), reader.GetGuid(0));
            Assert.Equal("previous-operator", reader.GetString(1));
            Assert.Equal(ResourceImportIntentCodec.Encode(binding.Attach(staged)), reader.GetString(2));
            Assert.Equal(created.ToUnixTimeMilliseconds(), reader.GetFieldValue<DateTimeOffset>(3).ToUnixTimeMilliseconds());
            Assert.False(await reader.ReadAsync());
            Assert.Empty(await db.Secrets.AsNoTracking().ToArrayAsync());
            Assert.Empty(await db.OperationOutputs.AsNoTracking().ToArrayAsync());
            await reader.DisposeAsync();
            Assert.Equal(true, await new NpgsqlCommand("SELECT to_regclass('public.operator_resource_import_requests') IS NULL AND to_regclass('public.resource_identity_claims') IS NULL", sql).ExecuteScalarAsync());
            Assert.Equal(0L, await new NpgsqlCommand("SELECT count(*) FROM resource_import_receipts", sql).ExecuteScalarAsync());
        }
    }

    private static ServiceProvider Services(string connection, params IInterceptor[] interceptors)
    {
        var services = new ServiceCollection().AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore()
            .AddSingleton<IProviderAliasResolver, NoProviderAccess>().AddPostgreSqlOperatorImportRequests();
        if (interceptors.Length > 0) services.AddSingleton<IDbContextFactory<ManifestCatalogDbContext>>(new Factory(connection, interceptors));
        return services.BuildServiceProvider();
    }

    private sealed class NoProviderAccess : IProviderAliasResolver
    {
        public ValueTask<PersistedProviderAliasResolution?> ResolveAsync(EnvironmentRecordId environment, ProviderAlias alias, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Request binding and linkage must not access providers.");
    }

    private static async Task AssertCounts(string connection, int requests, int stages, int changes)
    {
        await using var services = Services(connection);
        await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(requests, await db.OperatorImportRequestRecords.CountAsync());
        Assert.Equal(stages, await db.ProtectedApplicationCredentials.CountAsync());
        Assert.Equal(changes, await db.DurableChangeSets.CountAsync());
        Assert.Empty(await db.Secrets.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.ResourceObservationReceipts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.OperationOutputs.AsNoTracking().ToArrayAsync());
    }

    private sealed class Factory(string connection, IInterceptor[] interceptors) : IDbContextFactory<ManifestCatalogDbContext>
    {
        public ManifestCatalogDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>()
            .UseNpgsql(connection).AddInterceptors(interceptors).Options);
    }

    private sealed class BindingBarrier(Guid request) : SaveChangesInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<OperatorImportRequestRow>().Any(x => x.Entity.Id == request && x.State == EntityState.Added))
            {
                if (Interlocked.Increment(ref _arrived) == 2) _ready.TrySetResult();
                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    private sealed class LinkCommitFault(Guid request, bool afterCommit) : DbTransactionInterceptor
    {
        public bool Thrown { get; private set; }
        private bool Linked(DbContext? context) => !Thrown && context is not null && context.ChangeTracker.Entries<OperatorImportRequestRow>()
            .Any(x => x.Entity.Id == request && x.Entity.ChangeSet is not null);
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && Linked(eventData.Context)) { Thrown = true; throw new IOException("Injected linkage commit failure."); }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (afterCommit && Linked(eventData.Context)) { Thrown = true; throw new IOException("Injected lost linkage response."); }
            return Task.CompletedTask;
        }
    }

    private sealed class LinkCancellation(Guid request, CancellationTokenSource source, bool afterCommit) : DbTransactionInterceptor, ISaveChangesInterceptor
    {
        public bool Triggered { get; private set; }
        private bool Linked(DbContext? context) => !Triggered && context is not null && context.ChangeTracker.Entries<OperatorImportRequestRow>()
            .Any(x => x.Entity.Id == request && x.Entity.ChangeSet is not null);
        public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit && Linked(eventData.Context)) { Triggered = true; source.Cancel(); }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (afterCommit && Linked(eventData.Context))
            {
                Assert.False(cancellationToken.CanBeCanceled);
                Triggered = true; source.Cancel(); throw new OperationCanceledException(source.Token);
            }
            return Task.CompletedTask;
        }
    }
}
