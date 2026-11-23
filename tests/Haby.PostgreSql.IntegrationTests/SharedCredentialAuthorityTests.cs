using System.Data.Common;
using System.Text;
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

public sealed class SharedCredentialAuthorityTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
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
    public async Task Immutable_database_guards_refuse_late_issuance_mutation_and_unrevocation()
    {
        var connection = await Database();
        await using var services = Services(connection);
        var created = (await Apply(services)).Provider!;
        var authority = services.GetRequiredService<ISharedCredentialAdministration>();
        var binding = (await authority.InspectAsync(created.Environment, created.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default))[0];
        Assert.Equal(SharedCredentialRevocationOutcome.Revoked, await authority.RevokeAsync(binding.Scope, "revoker", default));
        foreach (var sql in new[]
        {
            "UPDATE shared_credential_bindings SET actor = 'changed'",
            "DELETE FROM shared_credential_bindings",
            "UPDATE shared_credential_revocations SET actor = 'changed'",
            "DELETE FROM shared_credential_revocations"
        })
        {
            await using var act = Context(connection);
            await Assert.ThrowsAsync<PostgresException>(async () => await act.Database.ExecuteSqlRawAsync(sql));
        }
        // An incomplete but sealed typed revision has valid foreign keys and no grant key collision.
        // Late issuance must be refused by the seal guard itself, and reload must fail closed.
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
            var error = await Assert.ThrowsAsync<DbUpdateException>(async () => await act.SaveChangesAsync());
            Assert.Equal("P0001", Assert.IsType<PostgresException>(error.InnerException).SqlState);
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

    private static string Declaration(int revision = 1) => ProviderDeclarationTests.Declaration.Replace("\"revision\":1", "\"revision\":" + revision, StringComparison.Ordinal)
        .Replace("\"secretRefs\":{", "\"secretRefs\":{\"system-access-credential\":{\"secretName\":\"shared\",\"version\":1},\"application-credential\":{\"secretName\":\"shared\",\"version\":1},", StringComparison.Ordinal);
    private static ValueTask<ProviderResult> Apply(ServiceProvider services, string? declaration = null, CancellationToken token = default) =>
        services.GetRequiredService<ProviderAdministration>().ApplyAsync(Encoding.UTF8.GetBytes(declaration ?? Declaration()), null, false, null, "issuing-actor", token);
    private static async Task<SecretRef> Reference(ServiceProvider services, string name)
    {
        var result = await services.GetRequiredService<SecretAdministration>().GetAsync("pilot", name, null, default);
        return new(result.Secret!.SecretId, result.Secret.Versions[0].VersionId);
    }
    private async Task<string> Database()
    {
        var name = "haby_shared_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        var result = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name, Pooling = true, MaxPoolSize = 16 }.ConnectionString;
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
    private static ManifestCatalogDbContext Context(string connection) => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
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
