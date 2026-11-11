using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PlacementRevisionPersistenceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Restart_exact_scopes_retry_original_identity_and_audit_conflict_and_no_other_state()
    {
        var connection = await Database();
        PlacementResult first;
        await using (var act = Services(connection))
        {
            var service = act.GetRequiredService<PlacementAdministration>();
            Assert.Equal(PlacementOutcome.EnvironmentNotFound, (await service.ImportAsync("missing", "cluster", Bytes(), "actor", default)).Outcome);
            Assert.Equal(PlacementOutcome.InvalidContext, (await service.ImportAsync("dev", "BAD", Bytes(), "actor", default)).Outcome);
            Assert.Equal(PlacementOutcome.InvalidDeclaration, (await service.ImportAsync("dev", "cluster", "{}"u8.ToArray(), "actor", default)).Outcome);
            first = await service.ImportAsync("dev", "cluster", Bytes(), "first-actor", default);
            Assert.Equal(PlacementOutcome.Created, first.Outcome);
        }
        await using (var act = Services(connection))
        {
            var service = act.GetRequiredService<PlacementAdministration>();
            var found = await service.GetAsync("dev", "cluster", "standard-runtime", 1, default);
            Assert.Equal(PlacementOutcome.Found, found.Outcome); Assert.Equal(first.Revision, found.Revision);
            var retry = await service.ImportAsync("dev", "cluster", Bytes(PlacementSamples.Full.Replace("\"revision\":1", "\"revision\":1.0e0")), "later-actor", default);
            Assert.Equal(PlacementOutcome.ExistingEquivalent, retry.Outcome); Assert.Equal(first.Revision, retry.Revision);
            var conflict = await service.ImportAsync("dev", "cluster", Bytes(PlacementSamples.Full.Replace("\"replicas\":3", "\"replicas\":4")), "other-actor", default);
            Assert.Equal(PlacementOutcome.RevisionContentConflict, conflict.Outcome); Assert.Equal(first.Revision, conflict.Revision);
            Assert.Equal(PlacementOutcome.NotFound, (await service.GetAsync("dev", "cluster", "standard-runtime", 2, default)).Outcome);
            Assert.Equal(PlacementOutcome.NotFound, (await service.GetAsync("dev", "other", "standard-runtime", 1, default)).Outcome);
            Assert.Equal(PlacementOutcome.Created, (await service.ImportAsync("dev", "other", Bytes(), "actor", default)).Outcome);
            Assert.Equal(PlacementOutcome.Created, (await service.ImportAsync("prod", "cluster", Bytes(), "actor", default)).Outcome);
            Assert.Equal(PlacementOutcome.Created, (await service.ImportAsync("dev", "cluster", Bytes(PlacementSamples.Minimal.Replace("standard-runtime", "minimal")), "actor", default)).Outcome);
            var typed = KubernetesPlacementCodec.ToTyped(found.Revision!.Declaration);
            Assert.Equal("apps", typed.Namespace); Assert.Equal("existing-tls", typed.Settings.Ingress!.TlsSecret);
            ResolveExactRead(found.Revision);
        }
        await using var verify = Context(connection);
        var original = await verify.PlacementRevisions.AsNoTracking().SingleAsync(x => x.Id == first.Revision!.Id);
        Assert.Equal("first-actor", original.ActorId); Assert.Equal(first.Revision!.Audit.ImportedAt, original.ImportedAt);
        Assert.Equal(4, await verify.PlacementRevisions.CountAsync());
        Assert.Empty(await verify.ProviderInstances.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.ProviderAliases.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.Secrets.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.DurablePlans.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.ApplicationDeployments.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.DurableOperations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Dump_restore_preserves_full_and_omitted_declarations_identity_audit_and_retry()
    {
        var source = await Database(); var restored = await Database(false);
        var accepted = new List<AcceptedPlacementRevision>();
        await using (var act = Services(source))
            foreach (var body in new[] { PlacementSamples.Full, PlacementSamples.Minimal.Replace("standard-runtime", "minimal") })
                accepted.Add((await act.GetRequiredService<PlacementAdministration>().ImportAsync("dev", "cluster", Bytes(body), "original", default)).Revision!);
        var directory = "/tmp/haby-placement-restore-" + Guid.NewGuid().ToString("N"); var dump = directory + "/catalog.dump";
        try
        {
            await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump,
                "--dbname=" + new NpgsqlConnectionStringBuilder(source).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges",
                "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
            await using (var act = Services(restored))
            {
                await act.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
                var service = act.GetRequiredService<PlacementAdministration>();
                foreach (var original in accepted)
                {
                    var found = await service.GetAsync("dev", "cluster", original.Key.Name.Value, original.Key.Revision, default);
                    Assert.Equal(original, found.Revision);
                    var retry = await service.ImportAsync("dev", "cluster", Bytes(original.Declaration.Payload), "later", default);
                    Assert.Equal(PlacementOutcome.ExistingEquivalent, retry.Outcome); Assert.Equal(original, retry.Revision);
                }
            }
            await using var verify = Context(restored); Assert.Equal(2, await verify.PlacementRevisions.CountAsync());
            Assert.Empty(await verify.DurableOperations.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.ProviderInstances.AsNoTracking().ToArrayAsync());
        }
        finally { await fixture.RunClientUtilityAsync("rm", "-f", dump); await fixture.RunClientUtilityAsync("rmdir", directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Precommit_failure_or_cancellation_rolls_back_the_complete_revision(bool cancel)
    {
        var connection = await Database(); var codec = new KubernetesPlacementCodec();
        using var cancellation = new CancellationTokenSource();
        await using (var services = Services(connection))
        {
            var service = new PlacementAdministration(services.GetRequiredService<IEnvironmentQuery>(), codec,
                new PostgreSqlPlacementRevisionStore(new Factory(connection, new PrecommitFault(cancel, cancellation)), codec), TimeProvider.System);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await service.ImportAsync("dev", "cluster", Bytes(), "actor", cancellation.Token));
            else await Assert.ThrowsAsync<IOException>(async () => await service.ImportAsync("dev", "cluster", Bytes(), "actor", default));
        }
        await using var verify = Context(connection); Assert.Empty(await verify.PlacementRevisions.AsNoTracking().ToArrayAsync());
    }

    private static void ResolveExactRead(AcceptedPlacementRevision accepted)
    {
        var envelope = """{"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example"},"spec":{"components":{"api":{"workloads":{"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"cluster","properties":{"image":{"name":"api","tag":"1"},"ports":{"http":{"containerPort":8080}},"routes":[{"hostLabel":"api","path":"/","port":"http"}]}}}}}}}}""";
        var catalog = new ManifestContractCatalog([], [KubernetesContract.Definition], [], []);
        var manifest = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Bytes(envelope), catalog)).Candidate.Declaration;
        var provider = ProviderInstanceId.New(); var configuration = ProviderConfigurationRevisionNumber.Create(1);
        var implementation = ProviderImplementationId.Create("kubernetes.test");
        var resolution = new ExactProviderResolutionSet(manifest, accepted.Key.Environment,
            [new(new ProviderInput.Workload(ManifestLocalId.Create("api"), ManifestLocalId.Create("main")), accepted.Key.Environment, accepted.Key.Alias, provider, configuration, implementation)]);
        var typed = KubernetesPlacementCodec.ToTyped(accepted.Declaration);
        var inputs = new PlacementInputs([new(accepted.Key.Environment, accepted.Key.Alias, provider, configuration, implementation, typed)],
            [new(new(accepted.Key.Environment, accepted.Key.Alias), typed.Reference)]);
        var result = Assert.Single(KubernetesWorkloadPlanner.Resolve(manifest, ApplicationVersion.Create("1"), resolution, inputs));
        Assert.Equal("registry.example.test:5000/product/team/api:1", result.ImageReference);
        Assert.Equal(3, result.Settings.Replicas);
        Assert.All(result.Objects, x => Assert.Equal("apps", x.Namespace));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_insert_winner_is_truthful_for_equivalent_and_conflicting_content(bool different)
    {
        var connection = await Database(); var barrier = new InsertBarrier(8);
        var codec = new KubernetesPlacementCodec();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var services = Services(connection);
            var store = new PostgreSqlPlacementRevisionStore(new Factory(connection, barrier), codec);
            var service = new PlacementAdministration(services.GetRequiredService<IEnvironmentQuery>(), codec, store, TimeProvider.System);
            return await service.ImportAsync("dev", "cluster", Bytes(different && i % 2 == 1
                ? PlacementSamples.Full.Replace("\"replicas\":3", "\"replicas\":4") : PlacementSamples.Full), "actor-" + i, default);
        }));
        var winner = Assert.Single(results, x => x.Outcome == PlacementOutcome.Created);
        Assert.Equal(different ? 4 : 0, results.Count(x => x.Outcome == PlacementOutcome.RevisionContentConflict));
        Assert.Equal(different ? 3 : 7, results.Count(x => x.Outcome == PlacementOutcome.ExistingEquivalent));
        Assert.All(results, x => Assert.Equal(winner.Revision, x.Revision));
        await using var verify = Context(connection);
        var saved = Assert.Single(await verify.PlacementRevisions.AsNoTracking().ToArrayAsync());
        Assert.Equal(winner.Revision!.Id, saved.Id); Assert.Equal(winner.Revision.Audit.ActorId, saved.ActorId);
        Assert.Equal(winner.Revision.Audit.ImportedAt, saved.ImportedAt); Assert.Equal(winner.Revision.Declaration.Payload, saved.Payload);
    }

    [Theory]
    [InlineData("payload = 'private-corrupt-marker'")]
    [InlineData("format_version = 99")]
    [InlineData("contract = 'unsupported/v99'")]
    [InlineData("name = 'wrong-name'")]
    public async Task Stored_corruption_refuses_read_and_retry_without_repair(string mutation)
    {
        var connection = await Database();
        await using (var act = Services(connection))
            Assert.Equal(PlacementOutcome.Created, (await act.GetRequiredService<PlacementAdministration>().ImportAsync("dev", "cluster", Bytes(), "actor", default)).Outcome);
        // Fixed test-owned SQL exercises privileged corruption; no submitted SQL enters production.
        var sql = "UPDATE placement_revisions SET " + mutation;
        await using (var arrange = Context(connection)) await arrange.Database.ExecuteSqlRawAsync(sql);
        await using (var act = Services(connection))
        {
            var db = act.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            await using var read = await db.CreateDbContextAsync();
            var raw = await read.PlacementRevisions.AsNoTracking().SingleAsync();
            var service = act.GetRequiredService<PlacementAdministration>();
            var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await service.GetAsync("dev", "cluster", raw.Name, 1, default));
            Assert.Equal("Stored Placement revision is unavailable.", error.Message);
            if (raw.Name == "standard-runtime")
                await Assert.ThrowsAsync<InvalidDataException>(async () => await service.ImportAsync("dev", "cluster", Bytes(), "actor", default));
        }
        await using var verify = Context(connection); Assert.Single(await verify.PlacementRevisions.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Current_base_migrates_additively_and_EF_writers_cannot_modify_or_delete_revisions()
    {
        var connection = await Database(false);
        await using (var arrange = Context(connection))
        {
            await arrange.GetService<IMigrator>().MigrateAsync("20261110122411_AddResourceNaming");
            arrange.Environments.Add(new() { Id = Guid.CreateVersion7(), EnvironmentId = "dev" }); await arrange.SaveChangesAsync();
        }
        await using (var act = Services(connection))
        {
            await act.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            Assert.Equal(PlacementOutcome.Created, (await act.GetRequiredService<PlacementAdministration>().ImportAsync("dev", "cluster", Bytes(), "actor", default)).Outcome);
        }
        foreach (var delete in new[] { false, true })
        {
            await using var act = Context(connection);
            var row = await act.PlacementRevisions.SingleAsync();
            if (delete) act.Remove(row); else row.Payload = "{}";
            await Assert.ThrowsAsync<InvalidOperationException>(() => act.SaveChangesAsync());
        }
        await using var verify = Context(connection);
        Assert.False(verify.Database.HasPendingModelChanges()); Assert.Single(await verify.Environments.AsNoTracking().ToArrayAsync());
        Assert.Equal(new KubernetesPlacementCodec().Decode(Bytes()).Payload, (await verify.PlacementRevisions.AsNoTracking().SingleAsync()).Payload);
    }

    private async Task<string> Database(bool migrate = true)
    {
        var name = "haby_placements_" + Guid.NewGuid().ToString("N");
        await using (var db = new NpgsqlConnection(fixture.ConnectionString))
        { await db.OpenAsync(); await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", db); await cmd.ExecuteNonQueryAsync(); }
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
        if (migrate)
        {
            await using (var services = Services(connection)) await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            await using var arrange = Context(connection);
            foreach (var environment in new[] { "dev", "prod" }) arrange.Environments.Add(new() { Id = Guid.CreateVersion7(), EnvironmentId = environment });
            await arrange.SaveChangesAsync();
        }
        return connection;
    }
    private static byte[] Bytes(string source = PlacementSamples.Full) => Encoding.UTF8.GetBytes(source);
    private static ManifestCatalogDbContext Context(string connection) => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
    private static ServiceProvider Services(string connection) => new ServiceCollection().AddPostgreSqlManifestCatalog(connection)
        .AddSingleton<IPlacementCodec, KubernetesPlacementCodec>().AddSingleton<IPlacementRevisionStore, PostgreSqlPlacementRevisionStore>()
        .AddSingleton<IPlacementSelectionScopeQuery, PostgreSqlPlacementSelectionScopeQuery>()
        .AddSingleton<IPlacementSelectionStore, PostgreSqlPlacementSelectionStore>().AddSingleton<PlacementSelectionAdministration>()
        .AddSingleton(TimeProvider.System).AddSingleton<PlacementAdministration>().BuildServiceProvider();
    private sealed class Factory(string connection, params IInterceptor[] interceptors) : IDbContextFactory<ManifestCatalogDbContext>
    {
        public ManifestCatalogDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).AddInterceptors(interceptors).Options);
    }
    private sealed class InsertBarrier(int count) : SaveChangesInterceptor
    {
        private int arrived;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (Interlocked.Increment(ref arrived) == count) ready.SetResult(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); return result; }
    }
    private sealed class PrecommitFault(bool cancel, CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (cancel) { cancellation.Cancel(); return ValueTask.FromResult(result); }
            throw new IOException("Injected precommit failure.");
        }
    }
}
