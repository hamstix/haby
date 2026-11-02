using System.Data.Common;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class GeneratedCredentialSlotTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Non_database_owner_parallel_candidates_converge_on_one_reference_value_and_audit_after_restart()
    {
        var (connection, slot) = await ArrangeAsync();
        var barrier = new CandidateBarrier(8);
        var service = new PostgreSqlGeneratedCredentialSlots(new Factory(connection, barrier));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Ensure(service, slot)));
        Assert.Single(results, x => x.Outcome == GeneratedCredentialOutcome.Created);
        Assert.Equal(7, results.Count(x => x.Outcome == GeneratedCredentialOutcome.Existing));
        var reference = results[0].Reference!;
        Assert.All(results, x => Assert.Equal(reference, x.Reference));
        byte[] original;
        await using (var services = Services(connection)) original = await Bytes(services, slot, reference);
        Assert.Equal(43, original.Length);
        Assert.True(original.All(b => b is >= 65 and <= 90 or >= 97 and <= 122 or >= 48 and <= 57 or 45 or 95), "Expected base64url ASCII material.");
        await using (var restarted = Services(connection))
        {
            foreach (var result in results) Assert.True(Enumerable.SequenceEqual(original, await Bytes(restarted, slot, result.Reference!)), "Exact generated material must remain unchanged.");
            Assert.Equal(reference, (await Ensure(restarted.GetRequiredService<IGeneratedCredentialSlots>(), slot)).Reference);
            Assert.True(Enumerable.SequenceEqual(original, await Bytes(restarted, slot, reference)), "Exact generated material must remain unchanged.");
        }
        await Counts(connection, 1);
        await using var check = Context(connection);
        Assert.Equal(0, await check.ProviderInstances.CountAsync());
        Assert.Equal(0, await check.DurableOperations.CountAsync());
        var owner = await check.Secrets.AsNoTracking().SingleAsync();
        Assert.Equal("queue-user", owner.ResourceLocalId);
        Assert.Equal("Resource", owner.Owner);
        Assert.Equal("ApplicationLogin", owner.Purpose);
        Assert.Equal(PostgreSqlGeneratedCredentialSlots.TechnicalActor,
            (await check.GeneratedCredentialCreationAudits.AsNoTracking().SingleAsync()).TechnicalActor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_commit_response_is_resolved_by_same_key_and_restart_preserves_material(bool cancelAfterCommit)
    {
        var (connection, slot) = await ArrangeAsync();
        using var cancellation = new CancellationTokenSource();
        var lost = new LostCommitResponse(cancelAfterCommit ? cancellation : null);
        var service = new PostgreSqlGeneratedCredentialSlots(new Factory(connection, lost));
        var result = await service.GetOrCreateAsync(slot, GeneratedCredentialSlot.RandomPasswordV1, cancellation.Token);
        Assert.Equal(cancelAfterCommit, cancellation.IsCancellationRequested);
        Assert.True(lost.Thrown);
        Assert.Equal(GeneratedCredentialOutcome.Existing, result.Outcome);
        await using var fresh = Services(connection);
        var original = await Bytes(fresh, slot, result.Reference!);
        Assert.Equal(result.Reference, (await Ensure(fresh.GetRequiredService<IGeneratedCredentialSlots>(), slot)).Reference);
        Assert.True(Enumerable.SequenceEqual(original, await Bytes(fresh, slot, result.Reference!)), "Exact generated material must remain unchanged.");
        await Counts(connection, 1);
    }

    [Fact]
    public async Task Lost_response_with_unavailable_reread_is_bounded_and_explicit_retry_reuses_committed_winner()
    {
        var (connection, slot) = await ArrangeAsync();
        var factory = new Factory(connection, new LostCommitResponse(), failAfterFirstContext: true);
        var result = await Ensure(new PostgreSqlGeneratedCredentialSlots(factory), slot);
        Assert.Equal(new(GeneratedCredentialOutcome.StorageUnavailable), result);
        SecretRef winner;
        await using (var check = Context(connection))
        {
            var row = await check.GeneratedCredentialSlots.AsNoTracking().SingleAsync();
            winner = new(SecretId.Parse(row.SecretId.ToString()), SecretVersionId.Parse(row.VersionId.ToString()));
        }
        await using var fresh = Services(connection);
        var bytes = await Bytes(fresh, slot, winner);
        Assert.Equal(winner, (await Ensure(fresh.GetRequiredService<IGeneratedCredentialSlots>(), slot)).Reference);
        Assert.True(Enumerable.SequenceEqual(bytes, await Bytes(fresh, slot, winner)), "Exact generated material must remain unchanged.");
        await Counts(connection, 1);
    }

    [Fact]
    public async Task Distinct_owner_dimensions_are_isolated_and_invalid_owners_profiles_slots_leave_no_partial_rows()
    {
        var (connection, slot) = await ArrangeAsync();
        await using var services = Services(connection);
        var service = services.GetRequiredService<IGeneratedCredentialSlots>();
        Assert.Equal(GeneratedCredentialOutcome.ProfileConflict, (await Ensure(service, slot, "unsupported")).Outcome);
        Assert.Equal(GeneratedCredentialOutcome.InvalidOwner, (await Ensure(service, slot with { SlotId = "invalid" })).Outcome);
        Assert.Equal(GeneratedCredentialOutcome.InvalidOwner, (await Ensure(service, slot with
        { Owner = slot.Owner with { EnvironmentRecordId = EnvironmentRecordId.New() } })).Outcome);
        Assert.Equal(GeneratedCredentialOutcome.InvalidOwner, (await Ensure(service, slot with
        { Owner = slot.Owner with { ApplicationRecordId = ApplicationRecordId.New() } })).Outcome);
        await Counts(connection, 0);
        var otherApp = ApplicationRecordId.New();
        var otherEnv = await services.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("other"), default);
        await using (var arrange = Context(connection))
        {
            arrange.Applications.Add(new() { Id = Guid.Parse(otherApp.ToString()), ApplicationId = "other" });
            await arrange.SaveChangesAsync();
        }
        var slots = new[] { slot, slot with { Owner = slot.Owner with { ResourceLocalId = ManifestLocalId.Create("http-client") } },
            slot with { Owner = slot.Owner with { ApplicationRecordId = otherApp } },
            slot with { Owner = slot.Owner with { EnvironmentRecordId = otherEnv } } };
        var refs = new List<SecretRef>();
        foreach (var item in slots) refs.Add((await Ensure(service, item)).Reference!);
        Assert.Equal(4, refs.Distinct().Count());
        var original = await Bytes(services, slot, refs[0]);
        Assert.Equal(GeneratedCredentialOutcome.ProfileConflict, (await Ensure(service, slot, "random-password/v2")).Outcome);
        Assert.Equal(refs[0], (await Ensure(service, slot)).Reference);
        Assert.True(Enumerable.SequenceEqual(original, await Bytes(services, slot, refs[0])), "Exact generated material must remain unchanged.");
        Assert.Null(await services.GetRequiredService<ISecretStore>().ResolveAsync(new SecretDefinition(new SecretOwner.Environment(otherEnv), SecretPurpose.ProviderAdministrator), refs[0], default));
        Assert.Null(await services.GetRequiredService<ISecretStore>().ResolveAsync(
            new(new SecretOwner.Resource(slots[1].Owner), SecretPurpose.ApplicationLogin), refs[0], default));
        Assert.Null(await services.GetRequiredService<ISecretStore>().ResolveAsync(
            new(new SecretOwner.Resource(slot.Owner with { EnvironmentRecordId = otherEnv }), SecretPurpose.ApplicationLogin), refs[0], default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await services.GetRequiredService<ISecretStore>()
            .AddVersionAsync(slot.Owner.EnvironmentRecordId, new(refs[0].SecretId, SecretVersionId.New()), [1], default));
        await Counts(connection, 4);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("format")]
    [InlineData("format-version")]
    [InlineData("bytes")]
    [InlineData("version")]
    [InlineData("owner")]
    [InlineData("association")]
    [InlineData("audit")]
    public async Task Inconsistent_saved_winner_never_regenerates_or_discloses_material(string corruption)
    {
        var (connection, slot) = await ArrangeAsync();
        await using (var services = Services(connection))
            Assert.Equal(GeneratedCredentialOutcome.Created, (await Ensure(services.GetRequiredService<IGeneratedCredentialSlots>(), slot)).Outcome);
        await using (var arrange = Context(connection))
        {
            // Deliberately bypass guards only in this owned disposable corruption fixture.
            await arrange.Database.OpenConnectionAsync();
            await arrange.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
            var sql = corruption switch
            {
                "payload" => "DELETE FROM secret_payloads",
                "format" => "UPDATE secret_versions SET protection_format = 'private-offending-value'",
                "format-version" => "UPDATE secret_versions SET format_version = 99",
                "bytes" => "UPDATE secret_payloads SET bytes = decode('deadbeef', 'hex')",
                "version" => "DELETE FROM secret_versions",
                "owner" => "UPDATE secrets SET resource_local_id = 'different'",
                "association" => "DELETE FROM generated_credential_slots",
                _ => "DELETE FROM generated_credential_creation_audits"
            };
            await arrange.Database.ExecuteSqlRawAsync(sql);
        }
        await using (var fresh = Services(connection))
        {
            for (var i = 0; i < 2; i++)
            {
                var refusal = await Ensure(fresh.GetRequiredService<IGeneratedCredentialSlots>(), slot);
                Assert.Equal(new(GeneratedCredentialOutcome.MaterialUnavailable), refusal);
                Assert.DoesNotContain("private-offending-value", refusal.ToString());
            }
        }
        await using var check = Context(connection);
        Assert.Equal(1, await check.Secrets.CountAsync());
        Assert.Equal(corruption == "version" ? 0 : 1, await check.SecretVersions.CountAsync());
    }

    [Fact]
    public async Task Database_constraints_reject_cross_environment_owner_and_slot_reassignment()
    {
        var (connection, slot) = await ArrangeAsync();
        await using (var services = Services(connection))
            await Ensure(services.GetRequiredService<IGeneratedCredentialSlots>(), slot);
        await using var check = Context(connection);
        var original = await check.GeneratedCredentialSlots.AsNoTracking().SingleAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => check.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO generated_credential_slots (environment_record_id, application_record_id, resource_local_id, slot_id, generation_profile, secret_id, version_id) VALUES ({Guid.CreateVersion7()}, {original.ApplicationRecordId}, {original.ResourceLocalId}, {original.SlotId}, {original.GenerationProfile}, {original.SecretId}, {original.VersionId})"));
        Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        await Assert.ThrowsAsync<PostgresException>(() => check.Database.ExecuteSqlRawAsync("UPDATE generated_credential_slots SET resource_local_id = 'different'"));
        await Assert.ThrowsAsync<PostgresException>(() => check.Database.ExecuteSqlRawAsync("DELETE FROM generated_credential_creation_audits"));
        await Assert.ThrowsAsync<PostgresException>(() => check.Database.ExecuteSqlRawAsync("UPDATE secrets SET purpose = 'ProviderAdministrator'"));
        Assert.False(check.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_cancellation_after_payload_before_commit_rolls_back_every_candidate(bool cancel)
    {
        var (connection, slot) = await ArrangeAsync();
        using var cancellation = new CancellationTokenSource();
        var interceptor = new FailBeforeSlot(cancel ? cancellation : null);
        var service = new PostgreSqlGeneratedCredentialSlots(new Factory(connection, interceptor));
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetOrCreateAsync(slot,
                GeneratedCredentialSlot.RandomPasswordV1, cancellation.Token).AsTask());
        else Assert.Equal(new(GeneratedCredentialOutcome.StorageUnavailable), await Ensure(service, slot));
        Assert.True(interceptor.SawSavedPayload);
        await Counts(connection, 0);
        await using var fresh = Services(connection);
        Assert.Equal(GeneratedCredentialOutcome.Created, (await Ensure(fresh.GetRequiredService<IGeneratedCredentialSlots>(), slot)).Outcome);
    }

    [Fact]
    public async Task Administrative_discovery_and_retries_exclude_resource_credentials()
    {
        var (connection, slot) = await ArrangeAsync();
        await using var services = Services(connection);
        var generated = await Ensure(services.GetRequiredService<IGeneratedCredentialSlots>(), slot);
        var admin = services.GetRequiredService<SecretAdministration>();
        Assert.Equal(SecretOutcome.NotFound, (await admin.GetAsync("pilot", null, generated.Reference!.SecretId.ToString(), default)).Outcome);
        Assert.Empty((await admin.ListAsync("pilot", 1, null, default)).Items);
        Assert.Null(await services.GetRequiredService<ISecretStore>().ResolveAsync(
            new(new SecretOwner.Environment(slot.Owner.EnvironmentRecordId), SecretPurpose.ProviderAdministrator), generated.Reference, default));
        using var material = new SecretMaterial([42]);
        var request = Guid.NewGuid();
        var created = await admin.CreateAsync("pilot", "queue-user", request, material, "test", default);
        Assert.Equal(SecretOutcome.Created, created.Outcome);
        Assert.Equal(SecretPurpose.ProviderAdministrator, created.Secret!.Purpose);
        Assert.Equal(new SecretOwner.Environment(slot.Owner.EnvironmentRecordId), created.Secret.Owner);
        Assert.Equal(SecretOutcome.ExistingEquivalent, (await admin.CreateAsync("pilot", "queue-user", request, material, "test", default)).Outcome);
        using var different = new SecretMaterial([43]);
        Assert.Equal(SecretOutcome.RequestContentConflict, (await admin.CreateAsync("pilot", "queue-user", request, different, "test", default)).Outcome);
        Assert.Equal(created.Secret.SecretId, (await admin.GetAsync("pilot", "queue-user", null, default)).Secret!.SecretId);
        Assert.Equal(created.Secret.SecretId, Assert.Single((await admin.ListAsync("pilot", 1, null, default)).Items).SecretId);
        using var resolved = await services.GetRequiredService<ISecretStore>().ResolveAsync(new SecretDefinition(new SecretOwner.Environment(slot.Owner.EnvironmentRecordId), SecretPurpose.ProviderAdministrator),
            new(created.Secret.SecretId, created.Secret.Versions[0].VersionId), default);
        Assert.Equal(new byte[] { 42 }, resolved!.CopyBytes());
        Assert.Equal(generated.Reference, (await Ensure(services.GetRequiredService<IGeneratedCredentialSlots>(), slot)).Reference);
    }

    [Fact]
    public async Task Provider_administrator_revision_refuses_generated_application_login_reference()
    {
        var (connection, slot) = await ArrangeAsync();
        await using var services = new ServiceCollection().AddPostgreSqlManifestCatalog(connection)
            .AddPostgreSqlPlaintextSecretStore().AddPostgreSqlProviderConfigurationStore()
            .AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
            .AddPostgreSqlProviderConfigurationCodec().BuildServiceProvider();
        var generated = await Ensure(services.GetRequiredService<IGeneratedCredentialSlots>(), slot);
        var configuration = new PostgreSqlProviderConfiguration(1, "127.0.0.1", 5432, "postgres", "admin", generated.Reference!,
            PostgreSqlDatabaseCheckpointCapability.ImplementationId);
        var providers = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await providers.CreateInstanceAsync(slot.Owner.EnvironmentRecordId, configuration, default);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => providers.AddRevisionAsync(slot.Owner.EnvironmentRecordId,
            instance, ProviderConfigurationRevisionNumber.Create(1), configuration, default).AsTask());
        Assert.Equal("Provider secret reference is unavailable.", error.Message);
        await using var check = Context(connection);
        Assert.Equal(0, await check.ProviderConfigurationRevisions.CountAsync());
        Assert.Equal(0, await check.ProviderRevisionSecretRefs.CountAsync());
    }

    [Fact]
    public async Task Real_dump_restore_retains_exact_owner_slot_bytes_and_creation_audit()
    {
        var (source, slot) = await ArrangeAsync();
        var target = await DatabaseAsync();
        var dump = "/tmp/haby-generated-" + Guid.NewGuid().ToString("N") + ".dump";
        SecretRef reference;
        byte[] original;
        DateTimeOffset createdAt;
        await using (var services = Services(source))
        {
            reference = (await Ensure(services.GetRequiredService<IGeneratedCredentialSlots>(), slot)).Reference!;
            original = await Bytes(services, slot, reference);
        }
        await using (var check = Context(source)) createdAt = (await check.GeneratedCredentialCreationAudits.AsNoTracking().SingleAsync()).CreatedAt;
        try
        {
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump,
                "--dbname=" + new NpgsqlConnectionStringBuilder(source).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges",
                "--dbname=" + new NpgsqlConnectionStringBuilder(target).Database, dump);
            await using (var fresh = Services(target))
            {
                await fresh.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
                Assert.True(Enumerable.SequenceEqual(original, await Bytes(fresh, slot, reference)), "Exact generated material must remain unchanged.");
                var repeated = await Ensure(fresh.GetRequiredService<IGeneratedCredentialSlots>(), slot);
                Assert.Equal(new(GeneratedCredentialOutcome.Existing, reference), repeated);
                Assert.True(Enumerable.SequenceEqual(original, await Bytes(fresh, slot, repeated.Reference!)), "Exact generated material must remain unchanged.");
            }
            await Counts(target, 1);
            await using var verify = Context(target);
            Assert.Equal(createdAt, (await verify.GeneratedCredentialCreationAudits.AsNoTracking().SingleAsync()).CreatedAt);
            var owner = await verify.Secrets.AsNoTracking().SingleAsync();
            Assert.Equal(slot.Owner.ResourceLocalId.Value, owner.ResourceLocalId);
            Assert.Equal(Guid.Parse(slot.Owner.ApplicationRecordId.ToString()), owner.ApplicationRecordId);
        }
        finally { await fixture.RunClientUtilityAsync("rm", "-f", dump); }
    }

    private async Task<(string Connection, GeneratedCredentialSlot Slot)> ArrangeAsync()
    {
        var connection = await DatabaseAsync();
        await using var services = Services(connection);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var environment = await services.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var app = ApplicationRecordId.New();
        await using (var arrange = Context(connection))
        {
            arrange.Applications.Add(new() { Id = Guid.Parse(app.ToString()), ApplicationId = "message-consumer" });
            await arrange.SaveChangesAsync();
        }
        return (connection, new(new(environment, app, ManifestLocalId.Create("queue-user")), GeneratedCredentialSlot.ApplicationPassword));
    }

    private async Task<string> DatabaseAsync()
    {
        var name = "haby_generated_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    private static Task<GeneratedCredentialResult> Ensure(IGeneratedCredentialSlots service, GeneratedCredentialSlot slot,
        string profile = GeneratedCredentialSlot.RandomPasswordV1) => service.GetOrCreateAsync(slot, profile, default).AsTask();

    private static async Task<byte[]> Bytes(ServiceProvider services, GeneratedCredentialSlot slot, SecretRef reference)
    {
        using var material = await services.GetRequiredService<ISecretStore>().ResolveAsync(new SecretDefinition(new SecretOwner.Resource(slot.Owner), SecretPurpose.ApplicationLogin), reference, default);
        Assert.NotNull(material);
        return material.CopyBytes();
    }

    private static async Task Counts(string connection, int expected)
    {
        await using var check = Context(connection);
        Assert.Equal(expected, await check.Secrets.CountAsync());
        Assert.Equal(expected, await check.SecretVersions.CountAsync());
        Assert.Equal(expected, await check.SecretPayloads.CountAsync());
        Assert.Equal(expected, await check.GeneratedCredentialSlots.CountAsync());
        Assert.Equal(expected, await check.GeneratedCredentialCreationAudits.CountAsync());
    }

    private static ManifestCatalogDbContext Context(string connection) => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
    private static ServiceProvider Services(string connection) => new ServiceCollection().AddPostgreSqlManifestCatalog(connection)
        .AddPostgreSqlPlaintextSecretStore().BuildServiceProvider();

    private sealed class Factory(string connection, IInterceptor interceptor, bool failAfterFirstContext = false) : IDbContextFactory<ManifestCatalogDbContext>
    {
        private int _contexts;
        public ManifestCatalogDbContext CreateDbContext()
        {
            if (Interlocked.Increment(ref _contexts) > 1 && failAfterFirstContext) throw new IOException("private-storage-error");
            return new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).AddInterceptors(interceptor).Options);
        }
    }

    private sealed class CandidateBarrier(int count) : SaveChangesInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<GeneratedCredentialSlotRow>().Any())
            {
                if (Interlocked.Increment(ref _arrived) == count) _ready.SetResult();
                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    private sealed class LostCommitResponse(CancellationTokenSource? cancellation = null) : DbTransactionInterceptor
    {
        internal bool Thrown { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (!Thrown) { Thrown = true; cancellation?.Cancel(); throw new IOException("private-lost-response"); }
            return Task.CompletedTask;
        }
    }

    private sealed class FailBeforeSlot(CancellationTokenSource? cancellation) : SaveChangesInterceptor
    {
        internal bool SawSavedPayload { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<GeneratedCredentialSlotRow>().Any())
            {
                SawSavedPayload = eventData.Context.ChangeTracker.Entries<SecretPayloadRow>().All(x => x.State == EntityState.Unchanged);
                if (cancellation is null) throw new InvalidOperationException("private-precommit-failure");
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(result);
        }
    }
}
