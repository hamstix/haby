using System.Data.Common;
using System.Text;
using Haby.Application;
using Haby.Domain;
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

public sealed class SharedCredentialAuthorityTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Reused_context_cannot_carry_issuance_or_revocation_permits_across_rollback()
    {
        var connection = await Database();
        await using var services = Services(connection);
        var created = (await Apply(services)).Provider!;
        var scope = (await services.GetRequiredService<ISharedCredentialAdministration>().InspectAsync(
            created.Environment, created.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default))[0].Scope;
        var environment = Guid.Parse(created.Environment.ToString());
        var provider = Guid.Parse(created.InstanceId.ToString());
        foreach (var issuance in new[] { true, false })
        {
            // Reusing this context is intentional: transaction ownership, including its tracked state, is under test.
            await using var db = Context(connection);
            await using (var first = await db.Database.BeginTransactionAsync())
            {
                Assert.True(await db.AcquireSharedCredentialGateAsync(environment, provider, default));
                if (issuance)
                {
                    var revision = await db.ProviderConfigurationRevisions.AsNoTracking().SingleAsync();
                    revision.RevisionNumber = 2;
                    revision.IsSealed = false;
                    db.ProviderConfigurationRevisions.Add(revision);
                    foreach (var reference in await db.ProviderRevisionSecretRefs.AsNoTracking().ToArrayAsync())
                    {
                        reference.RevisionNumber = 2;
                        db.ProviderRevisionSecretRefs.Add(reference);
                    }
                    var binding = await db.SharedCredentialBindings.AsNoTracking().FirstAsync();
                    binding.RevisionNumber = 2;
                    db.AuthorizeSharedCredentialBinding(binding);
                    db.SharedCredentialBindings.Add(binding);
                }
                else
                {
                    var revocation = new SharedCredentialRevocationRow
                    {
                        ProviderInstanceId = provider,
                        RevisionNumber = 1,
                        Slot = scope.Slot,
                        Actor = "revoker",
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    db.AuthorizeSharedCredentialRevocation(revocation, scope);
                    db.SharedCredentialRevocations.Add(revocation);
                }
                await first.RollbackAsync();
            }
            await using var second = await db.Database.BeginTransactionAsync();
            Assert.True(await db.AcquireSharedCredentialGateAsync(environment, provider, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using var check = Context(connection);
        Assert.Single(await check.ProviderConfigurationRevisions.AsNoTracking().ToArrayAsync());
        Assert.Equal(2, await check.SharedCredentialBindings.CountAsync());
        Assert.Empty(await check.SharedCredentialRevocations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task One_material_two_explicit_uses_are_atomic_exact_and_closed_to_application_delivery()
    {
        var connection = await Database();
        await using var services = Services(connection);
        var env = (await services.GetRequiredService<IEnvironmentQuery>().FindAsync(EnvironmentId.Create("pilot"), default))!;
        var reference = await Reference(services, "shared");
        var resolver = services.GetRequiredService<ISharedCredentialSystemAccess>();
        var guessed = new SharedCredentialScope(env, ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1),
            PostgreSqlProviderConfigurationCodec.SystemAccessCredentialSlot, SharedCredentialUse.SystemAccess, reference);
        Assert.Null(await resolver.ResolveAsync(guessed, 100, default));
        var created = await Apply(services);
        Assert.Equal(ProviderOutcome.Created, created.Outcome);
        var authority = services.GetRequiredService<ISharedCredentialAdministration>();
        var bindings = await authority.InspectAsync(env, created.Provider!.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default);
        Assert.Equal(2, bindings.Count);
        Assert.All(bindings, x => { Assert.Equal(reference, x.Scope.Reference); Assert.Equal("issuing-actor", x.IssuedBy); Assert.Null(x.RevokedAt); });
        Assert.Equal(2, bindings.Select(x => x.Scope.Use).Distinct().Count());
        var system = Assert.Single(bindings, x => x.Scope.Use == SharedCredentialUse.SystemAccess).Scope;
        using var resolved = await resolver.ResolveAsync(system, 100, default);
        Assert.NotNull(resolved);
        Assert.Equal(new byte[] { 17, 29 }, resolved.CopyBytes());
        var application = Assert.Single(bindings, x => x.Scope.Use == SharedCredentialUse.ApplicationDelivery).Scope;
        Assert.Null(await resolver.ResolveAsync(application, 100, default));
        foreach (var wrong in new[]
        {
            system with { Environment = EnvironmentRecordId.New() }, system with { Provider = ProviderInstanceId.New() },
            system with { Revision = ProviderConfigurationRevisionNumber.Create(2) }, system with { Slot = application.Slot },
            system with { Use = SharedCredentialUse.ApplicationDelivery }, system with { Reference = new(SecretId.New(), reference.SecretVersionId) },
            system with { Reference = new(reference.SecretId, SecretVersionId.New()) }
        }) Assert.Null(await resolver.ResolveAsync(wrong, 100, default));
        await Assert.ThrowsAsync<ConfigurationDocumentLimitException>(async () => await resolver.ResolveAsync(system, 1, default));
        Assert.Equal(ProviderOutcome.ExistingEquivalent, (await Apply(services)).Outcome);
        await using var check = Context(connection);
        Assert.Equal(2, await check.SharedCredentialBindings.CountAsync());
        Assert.Equal(1, await check.SecretVersions.CountAsync(x => x.SecretId == Guid.Parse(reference.SecretId.ToString())));
        Assert.Equal(1, await check.SecretPayloads.CountAsync(x => x.SecretVersionId == Guid.Parse(reference.SecretVersionId.ToString())));
        Assert.Single(await check.ProviderConfigurationRevisions.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Concurrent_per_use_revocation_is_irreversible_after_restart_retry_and_new_revision()
    {
        var connection = await Database();
        SharedCredentialScope system;
        SharedCredentialScope application;
        await using (var services = Services(connection))
        {
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => await Apply(services)));
            Assert.Single(outcomes, x => x.Outcome == ProviderOutcome.Created);
            Assert.Equal(7, outcomes.Count(x => x.Outcome == ProviderOutcome.ExistingEquivalent));
            var first = outcomes[0].Provider!;
            var authority = services.GetRequiredService<ISharedCredentialAdministration>();
            var bindings = await authority.InspectAsync(first.Environment, first.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default);
            system = Assert.Single(bindings, x => x.Scope.Use == SharedCredentialUse.SystemAccess).Scope;
            application = Assert.Single(bindings, x => x.Scope.Use == SharedCredentialUse.ApplicationDelivery).Scope;
            var revoked = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => await authority.RevokeAsync(system, "revoking-actor", default)));
            Assert.Single(revoked, x => x == SharedCredentialRevocationOutcome.Revoked);
            Assert.Equal(7, revoked.Count(x => x == SharedCredentialRevocationOutcome.AlreadyRevoked));
        }
        await using (var services = Services(connection))
        {
            var authority = services.GetRequiredService<ISharedCredentialAdministration>();
            var resolver = services.GetRequiredService<ISharedCredentialSystemAccess>();
            Assert.Equal(ProviderOutcome.ExistingEquivalent, (await Apply(services)).Outcome);
            Assert.Null(await resolver.ResolveAsync(system, 100, default));
            Assert.Equal(SharedCredentialRevocationOutcome.AlreadyRevoked, await authority.RevokeAsync(system, "later-actor", default));
            var old = await authority.InspectAsync(system.Environment, system.Provider, system.Revision, default);
            Assert.Null(Assert.Single(old, x => x.Scope.Use == SharedCredentialUse.ApplicationDelivery).RevokedAt);
            var audit = Assert.Single(old, x => x.Scope.Use == SharedCredentialUse.SystemAccess);
            Assert.Equal("revoking-actor", audit.RevokedBy);
            Assert.NotNull(audit.RevokedAt);
            Assert.True(audit.RevokedAt >= audit.IssuedAt);
            Assert.Equal(ProviderOutcome.Created, (await Apply(services, Declaration(2))).Outcome);
            using var current = await resolver.ResolveAsync(system with { Revision = ProviderConfigurationRevisionNumber.Create(2) }, 100, default);
            Assert.NotNull(current);
            Assert.Null(await resolver.ResolveAsync(system, 100, default));
            Assert.Equal(SharedCredentialRevocationOutcome.Revoked, await authority.RevokeAsync(application, "app-revoker", default));
            Assert.Equal(SharedCredentialRevocationOutcome.NotFound, await authority.RevokeAsync(application with { Reference = new(SecretId.New(), SecretVersionId.New()) }, "actor", default));
        }
        await using var check = Context(connection);
        Assert.Equal(4, await check.SharedCredentialBindings.CountAsync());
        Assert.Equal(2, await check.SharedCredentialRevocations.CountAsync());
        Assert.Equal(2, await check.ProviderConfigurationRevisions.CountAsync());
    }

    [Fact]
    public async Task Purpose_environment_version_and_changed_selection_never_substitute_authority()
    {
        var connection = await Database();
        await using var services = Services(connection);
        foreach (var invalid in new[]
        {
            Declaration().Replace("\"system-access-credential\":{\"secretName\":\"shared\"", "\"system-access-credential\":{\"secretName\":\"admin\"", StringComparison.Ordinal),
            Declaration().Replace("\"secretName\":\"admin\"", "\"secretName\":\"shared\"", StringComparison.Ordinal),
            Declaration().Replace("\"secretName\":\"shared\"", "\"secretName\":\"configuration\"", StringComparison.Ordinal),
            Declaration().Replace("\"secretName\":\"shared\",\"version\":1", "\"secretName\":\"shared\",\"version\":2", StringComparison.Ordinal),
            Declaration().Replace("\"secretName\":\"shared\"", "\"secretName\":\"other-only\"", StringComparison.Ordinal)
        }) Assert.Equal(ProviderOutcome.SecretReferenceUnavailable, (await Apply(services, invalid)).Outcome);
        var created = await Apply(services);
        Assert.Equal(ProviderOutcome.Created, created.Outcome);
        Assert.Equal(ProviderOutcome.RevisionContentConflict, (await Apply(services, Declaration().Replace("\"secretName\":\"shared\"", "\"secretName\":\"second\"", StringComparison.Ordinal))).Outcome);
        var configuration = await services.GetRequiredService<IProviderConfigurationStore>().FindRevisionAsync<PostgreSqlProviderConfiguration>(
            created.Provider!.Environment, created.Provider.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default);
        Assert.NotNull(configuration!.Configuration.SystemAccessCredential);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await services.GetRequiredService<IProviderConfigurationStore>().AddRevisionAsync(
            created.Provider.Environment, created.Provider.InstanceId, ProviderConfigurationRevisionNumber.Create(2), configuration.Configuration, default));
        await using var check = Context(connection);
        Assert.Equal(2, await check.SharedCredentialBindings.CountAsync());
        Assert.Single(await check.ProviderConfigurationRevisions.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Failure_or_cancellation_after_issuance_rolls_back_revision_and_every_binding()
    {
        foreach (var cancel in new[] { false, true })
        {
            var connection = await Database();
            using var cancellation = new CancellationTokenSource();
            var fault = new SealFault(cancel ? cancellation : null);
            await using (var services = Services(connection, fault))
            {
                if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Apply(services, token: cancellation.Token));
                else Assert.Equal(ProviderOutcome.StorageUnavailable, (await Apply(services)).Outcome);
            }
            Assert.Equal(1, fault.Hits);
            await using (var check = Context(connection))
            {
                Assert.Empty(await check.ProviderInstances.AsNoTracking().ToArrayAsync());
                Assert.Empty(await check.ProviderConfigurationRevisions.AsNoTracking().ToArrayAsync());
                Assert.Empty(await check.ProviderRevisionSecretRefs.AsNoTracking().ToArrayAsync());
                Assert.Empty(await check.SharedCredentialBindings.AsNoTracking().ToArrayAsync());
                Assert.Equal(6, await check.SecretPayloads.CountAsync());
            }
            await using var restarted = Services(connection);
            Assert.Equal(ProviderOutcome.Created, (await Apply(restarted)).Outcome);
        }
    }

    [Fact]
    public async Task Exact_constraints_and_guarded_issuance_keep_sealed_history_closed()
    {
        var connection = await Database();
        await using var services = Services(connection);
        var created = (await Apply(services)).Provider!;
        var authority = services.GetRequiredService<ISharedCredentialAdministration>();
        var binding = (await authority.InspectAsync(created.Environment, created.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default))[0];
        Assert.Equal(SharedCredentialRevocationOutcome.Revoked, await authority.RevokeAsync(binding.Scope, "revoker", default));
        // An incomplete but sealed typed revision has valid foreign keys and no grant key collision.
        // Supported late issuance must be refused in C#, and reload must fail closed.
        // Direct SQL maintenance is trusted; identity mismatches still face declarative FKs.
        await using (var arrange = Context(connection))
        {
            await using var transaction = await arrange.Database.BeginTransactionAsync();
            var revision = await arrange.ProviderConfigurationRevisions.AsNoTracking().SingleAsync();
            revision.RevisionNumber = 2; revision.IsSealed = false;
            arrange.ProviderConfigurationRevisions.Add(revision);
            foreach (var reference in await arrange.ProviderRevisionSecretRefs.AsNoTracking().ToArrayAsync())
            {
                reference.RevisionNumber = 2;
                arrange.ProviderRevisionSecretRefs.Add(reference);
            }
            await arrange.SaveChangesAsync();
            await arrange.ProviderConfigurationRevisions.Where(x => x.RevisionNumber == 2)
                .ExecuteUpdateAsync(x => x.SetProperty(r => r.IsSealed, true));
            await transaction.CommitAsync();
        }
        await using (var act = Context(connection))
        {
            var grant = await act.SharedCredentialBindings.AsNoTracking().FirstAsync();
            grant.RevisionNumber = 2;
            act.SharedCredentialBindings.Add(grant);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await act.SaveChangesAsync());
        }
        foreach (var mismatch in new[] { "environment", "secret", "version" })
        {
            await using var act = Context(connection);
            var grant = await act.SharedCredentialBindings.AsNoTracking().FirstAsync();
            var environment = mismatch == "environment" ? Guid.NewGuid() : grant.EnvironmentRecordId;
            var secret = mismatch == "secret" ? Guid.NewGuid() : grant.SecretId;
            var version = mismatch == "version" ? Guid.NewGuid() : grant.SecretVersionId;
            var error = await Assert.ThrowsAsync<PostgresException>(async () => await act.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO shared_credential_bindings
                    (provider_instance_id, revision_number, slot, environment_record_id, secret_id, secret_version_id, use, actor, created_at)
                VALUES ({grant.ProviderInstanceId}, {2L}, {grant.Slot}, {environment}, {secret}, {version}, {grant.Use}, {grant.Actor}, {grant.CreatedAt})
                """));
            Assert.Equal("23503", error.SqlState);
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await services.GetRequiredService<IProviderConfigurationStore>()
            .FindRevisionAsync<PostgreSqlProviderConfiguration>(created.Environment, created.InstanceId, ProviderConfigurationRevisionNumber.Create(2), default));
        await using var check = Context(connection);
        Assert.Equal(2, await check.SharedCredentialBindings.CountAsync());
        Assert.Single(await check.SharedCredentialRevocations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Rejected_cancelled_and_revoked_scopes_read_no_material_and_metadata_is_payload_free()
    {
        var connection = await Database();
        var observed = new MaterialReads();
        await using var services = Services(connection, observed);
        var created = (await Apply(services)).Provider!;
        var authority = services.GetRequiredService<ISharedCredentialAdministration>();
        var bindings = await authority.InspectAsync(created.Environment, created.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default);
        Assert.Equal(0, observed.Reads);
        var scope = Assert.Single(bindings, x => x.Scope.Use == SharedCredentialUse.SystemAccess).Scope;
        var resolver = services.GetRequiredService<ISharedCredentialSystemAccess>();
        Assert.Null(await resolver.ResolveAsync(scope with { Reference = new(scope.Reference.SecretId, SecretVersionId.New()) }, 100, default));
        Assert.Null(await resolver.ResolveAsync(scope with { Use = SharedCredentialUse.ApplicationDelivery }, 100, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await resolver.ResolveAsync(scope, 100, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await authority.RevokeAsync(scope, "actor", cancellation.Token));
        Assert.Equal(0, observed.Reads);
        using (var material = await resolver.ResolveAsync(scope, 100, default)) Assert.NotNull(material);
        Assert.True(observed.Reads > 0);
        var before = observed.Reads;
        Assert.Equal(SharedCredentialRevocationOutcome.Revoked, await authority.RevokeAsync(scope, "revoker", default));
        Assert.Null(await resolver.ResolveAsync(scope, 100, default));
        Assert.Equal(ProviderOutcome.ExistingEquivalent, (await Apply(services)).Outcome);
        Assert.Equal(2, (await authority.InspectAsync(scope.Environment, scope.Provider, scope.Revision, default)).Count);
        Assert.Equal(before, observed.Reads);
    }

    private sealed class MaterialReads : DbCommandInterceptor
    {
        public int Reads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("secret_payloads", StringComparison.Ordinal)) Interlocked.Increment(ref Reads);
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public void C_migration_adds_no_new_stored_server_code()
    {
        using var db = Context(fixture.ConnectionString);
        var migration = db.Database.GetMigrations().Last();
        var migrator = db.GetService<IMigrator>();
        foreach (var sql in new[] { migrator.GenerateScript("20261123114429_ReferenceSharingAssociations", migration),
            migrator.GenerateScript(migration, "20261123114429_ReferenceSharingAssociations") })
            foreach (var forbidden in new[] { "CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", "CREATE TRIGGER", "CREATE PROCEDURE", "DO $" })
                Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Supported_EF_history_mutations_fail_before_database_in_sync_and_async_paths()
    {
        var connection = await Database();
        await using var services = Services(connection);
        var created = (await Apply(services)).Provider!;
        var authority = services.GetRequiredService<ISharedCredentialAdministration>();
        var binding = (await authority.InspectAsync(created.Environment, created.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default))[0];
        Assert.Equal(SharedCredentialRevocationOutcome.Revoked, await authority.RevokeAsync(binding.Scope, "revoker", default));
        foreach (var revocation in new[] { false, true })
            foreach (var delete in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                {
                    var observed = new HistoryWrites();
                    await using var act = Context(connection, observed);
                    if (revocation)
                    {
                        var row = await act.SharedCredentialRevocations.SingleAsync();
                        if (delete) act.Remove(row); else row.Actor = "changed";
                    }
                    else
                    {
                        var row = await act.SharedCredentialBindings.FirstAsync();
                        if (delete) act.Remove(row); else row.Actor = "changed";
                    }
                    if (asynchronous) await Assert.ThrowsAsync<InvalidOperationException>(async () => await act.SaveChangesAsync());
                    else Assert.Throws<InvalidOperationException>(() => act.SaveChanges());
                    Assert.Equal(0, observed.Writes);
                }
        await using var check = Context(connection);
        Assert.Equal(2, await check.SharedCredentialBindings.CountAsync());
        Assert.Equal("revoker", (await check.SharedCredentialRevocations.AsNoTracking().SingleAsync()).Actor);
        Assert.All(await check.SharedCredentialBindings.AsNoTracking().ToArrayAsync(), row => Assert.Equal("issuing-actor", row.Actor));
    }

    [Fact]
    public async Task Supported_EF_bulk_history_mutations_are_rejected_before_database()
    {
        var connection = await Database();
        await using var services = Services(connection);
        var created = (await Apply(services)).Provider!;
        var authority = services.GetRequiredService<ISharedCredentialAdministration>();
        var binding = (await authority.InspectAsync(created.Environment, created.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default))[0];
        Assert.Equal(SharedCredentialRevocationOutcome.Revoked, await authority.RevokeAsync(binding.Scope, "revoker", default));
        foreach (var revocation in new[] { false, true })
            foreach (var delete in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                {
                    var observed = new HistoryWrites();
                    await using var act = Context(connection, observed);
                    if (asynchronous)
                        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                        {
                            if (revocation)
                            {
                                if (delete) await act.SharedCredentialRevocations.ExecuteDeleteAsync();
                                else await act.SharedCredentialRevocations.ExecuteUpdateAsync(x => x.SetProperty(r => r.Actor, "changed"));
                            }
                            else
                            {
                                if (delete) await act.SharedCredentialBindings.ExecuteDeleteAsync();
                                else await act.SharedCredentialBindings.ExecuteUpdateAsync(x => x.SetProperty(r => r.Actor, "changed"));
                            }
                        });
                    else
                        Assert.Throws<InvalidOperationException>(() =>
                        {
                            if (revocation)
                            {
                                if (delete) act.SharedCredentialRevocations.ExecuteDelete();
                                else act.SharedCredentialRevocations.ExecuteUpdate(x => x.SetProperty(r => r.Actor, "changed"));
                            }
                            else
                            {
                                if (delete) act.SharedCredentialBindings.ExecuteDelete();
                                else act.SharedCredentialBindings.ExecuteUpdate(x => x.SetProperty(r => r.Actor, "changed"));
                            }
                        });
                    Assert.Equal(0, observed.Writes);
                }
        await using var check = Context(connection);
        Assert.Equal(2, await check.SharedCredentialBindings.CountAsync());
        Assert.Single(await check.SharedCredentialRevocations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Prior_unmerged_server_guard_catalog_refuses_before_any_mutation()
    {
        var connection = await Database(migrate: false);
        string[] current;
        await using (var model = Context(connection)) current = model.Database.GetMigrations().ToArray();
        var prior = current[..^1].Append("20261123153600_SharedCredentialUseAuthority").ToArray();
        await using (var arrange = Context(connection))
        {
            await arrange.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "__EFMigrationsHistory" ("MigrationId" varchar(150) NOT NULL PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
                CREATE TABLE retained_marker (value integer NOT NULL);
                INSERT INTO retained_marker VALUES (41);
                """);
            foreach (var migration in prior)
                await arrange.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({migration}, {"10.0.0"})");
        }
        await using (var services = Services(connection))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync());
            Assert.StartsWith("Unsupported catalog schema.", error.Message);
        }
        await using var check = Context(connection);
        Assert.Equal(prior, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        Assert.Equal(41, (int)(await new NpgsqlCommand("SELECT value FROM retained_marker", database).ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Projected_bulk_history_updates_are_rejected_before_database()
    {
        var connection = await Database();
        await using var services = Services(connection);
        var created = (await Apply(services)).Provider!;
        var authority = services.GetRequiredService<ISharedCredentialAdministration>();
        var binding = (await authority.InspectAsync(created.Environment, created.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default))[0];
        Assert.Equal(SharedCredentialRevocationOutcome.Revoked, await authority.RevokeAsync(binding.Scope, "revoker", default));
        foreach (var revocation in new[] { false, true })
            foreach (var asynchronous in new[] { false, true })
            {
                var observed = new HistoryWrites();
                await using var act = Context(connection, observed);
                InvalidOperationException error;
                if (asynchronous)
                    error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    {
                        if (revocation) await act.SharedCredentialRevocations.Select(row => new { History = row })
                            .ExecuteUpdateAsync(x => x.SetProperty(r => r.History.Actor, "changed"));
                        else await act.SharedCredentialBindings.Select(row => new { History = row })
                            .ExecuteUpdateAsync(x => x.SetProperty(r => r.History.Actor, "changed"));
                    });
                else
                    error = Assert.Throws<InvalidOperationException>(() =>
                    {
                        if (revocation) act.SharedCredentialRevocations.Select(row => new { History = row })
                            .ExecuteUpdate(x => x.SetProperty(r => r.History.Actor, "changed"));
                        else act.SharedCredentialBindings.Select(row => new { History = row })
                            .ExecuteUpdate(x => x.SetProperty(r => r.History.Actor, "changed"));
                    });
                Assert.Equal("Shared credential history does not support bulk mutation.", error.Message);
                Assert.Equal(0, observed.Writes);
            }
        await using var check = Context(connection);
        Assert.All(await check.SharedCredentialBindings.AsNoTracking().ToArrayAsync(), row => Assert.Equal("issuing-actor", row.Actor));
        Assert.Equal("revoker", (await check.SharedCredentialRevocations.AsNoTracking().SingleAsync()).Actor);
    }

    private sealed class HistoryWrites : DbCommandInterceptor
    {
        public int Writes;
        private void Observe(DbCommand command)
        {
            if (command.CommandText.Contains("UPDATE shared_credential_", StringComparison.Ordinal) ||
                command.CommandText.Contains("DELETE FROM shared_credential_", StringComparison.Ordinal)) Interlocked.Increment(ref Writes);
        }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData data, InterceptionResult<int> result)
        { Observe(command); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken token = default)
        { Observe(command); return ValueTask.FromResult(result); }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result)
        { Observe(command); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        { Observe(command); return ValueTask.FromResult(result); }
    }

    private static string Declaration(int revision = 1) => ProviderDeclarationTests.Declaration.Replace("\"revision\":1", "\"revision\":" + revision, StringComparison.Ordinal)
        .Replace("\"secretRefs\":{", "\"secretRefs\":{\"system-access-credential\":{\"secretName\":\"shared\",\"version\":1},\"application-credential\":{\"secretName\":\"shared\",\"version\":1},", StringComparison.Ordinal);
    private static ValueTask<ProviderResult> Apply(ServiceProvider services, string? declaration = null, CancellationToken token = default) =>
        services.GetRequiredService<ProviderAdministration>().ApplyAsync(Encoding.UTF8.GetBytes(declaration ?? Declaration()), null, false, null, "issuing-actor", token);
    private static async Task<SecretRef> Reference(ServiceProvider services, string name)
    {
        var result = await services.GetRequiredService<SecretAdministration>().GetAsync("pilot", name, null, default);
        return new(result.Secret!.SecretId, result.Secret.Versions[0].VersionId);
    }
    private async Task<string> Database(bool migrate = true)
    {
        var name = "haby_shared_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        var result = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name, Pooling = true, MaxPoolSize = 16 }.ConnectionString;
        if (!migrate) return result;
        await using var services = Services(result);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await services.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("pilot"), default);
        await services.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("other"), default);
        using var material = new SecretMaterial([17, 29]);
        foreach (var item in new[] { ("pilot", "admin", SecretPurpose.ProviderAdministrator), ("pilot", "shared", SecretPurpose.SharedApplicationCredential),
            ("pilot", "second", SecretPurpose.SharedApplicationCredential), ("pilot", "configuration", SecretPurpose.Configuration),
            ("other", "other-only", SecretPurpose.SharedApplicationCredential), ("other", "admin", SecretPurpose.ProviderAdministrator) })
            Assert.Equal(SecretOutcome.Created, (await services.GetRequiredService<SecretAdministration>().CreateAsync(item.Item1, item.Item2,
                Guid.NewGuid(), material, "secret-creator", default, item.Item3)).Outcome);
        return result;
    }
    private static ServiceProvider Services(string connection, IInterceptor? interceptor = null)
    {
        var services = new ServiceCollection().AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore()
            .AddPostgreSqlProviderConfigurationStore().AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
            .AddPostgreSqlProviderConfigurationCodec();
        if (interceptor is not null) services.AddDbContextFactory<ManifestCatalogDbContext>(o => o.UseNpgsql(connection).AddInterceptors(interceptor));
        return services.BuildServiceProvider();
    }
    private static ManifestCatalogDbContext Context(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }
    private sealed class SealFault(CancellationTokenSource? cancellation) : DbCommandInterceptor
    {
        public int Hits;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE provider_configuration_revisions", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Hits);
                if (cancellation is not null) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
                throw new NpgsqlException("Test-only issuance fault.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
