using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Tests;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Target = Haby.Application.PlacementSelectionTarget;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PlacementRevisionPersistenceTests
{
    private static readonly PlacementSelectionPrecondition Absent = new PlacementSelectionPrecondition.ExpectedAbsence();
    private static PlacementSelectionPrecondition Match(PersistedPlacementSelection s) => new PlacementSelectionPrecondition.ExpectedToken(s.Token);
    private async Task<(string Connection, AcceptedPlacementRevision First, AcceptedPlacementRevision Second)> SelectionDatabase()
    {
        var connection = await Database();
        await using var services = Services(connection); var admin = services.GetRequiredService<PlacementAdministration>();
        var first = (await admin.ImportAsync("dev", "cluster", Bytes(PlacementSamples.Minimal), "importer", default)).Revision!;
        var second = (await admin.ImportAsync("dev", "cluster", Bytes(PlacementSamples.Minimal.Replace("\"revision\":1", "\"revision\":2")), "importer", default)).Revision!;
        Assert.Equal(2, second.Key.Revision);
        return (connection, first, second);
    }
    private static PlacementSelectionAdministration SelectionService(string connection, params IInterceptor[] interceptors)
    {
        var factory = new Factory(connection, interceptors); var codec = new KubernetesPlacementCodec();
        // Services owns the regular query graph; scope validation is exercised through the real Host tests.
        return new(new PostgreSqlEnvironmentQuery(factory), new UnusedScope(), new PostgreSqlPlacementRevisionStore(factory, codec),
            new PostgreSqlPlacementSelectionStore(factory, codec), TimeProvider.System);
    }
    private sealed class UnusedScope : IPlacementSelectionScopeQuery
    {
        public ValueTask<bool> ApplicationExistsAsync(Haby.Domain.ApplicationId application, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ManifestRevision?> FindVersionAsync(Haby.Domain.ApplicationId application, ApplicationVersion version, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private static ValueTask<PlacementSelectionResult> Select(PlacementSelectionAdministration service, Target target, int revision,
        PlacementSelectionPrecondition? condition, string actor = "actor", CancellationToken token = default, string environment = "dev", string alias = "cluster")
        => service.ApplyAsync(environment, alias, target, "standard-runtime", revision, null, condition, actor, token);

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("equal")]
    public async Task Component_CAS_uses_one_pool_connection_and_preserves_exact_checked_audit(string operation)
    {
        var (connection, first, second) = await SelectionDatabase();
        var references = new Dictionary<string, ManifestRevisionRef>();
        await using (var arrange = Services(connection))
        {
            var importer = new ImportApplicationManifest(arrange.GetRequiredService<IManifestImportStore>(), new ManifestContractCatalog([], [], [], []), TimeProvider.System);
            foreach (var version in new[] { "2", "1" })
            {
                var envelope = "{\"applicationVersion\":\"" + version + "\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"assigned\"},\"spec\":{\"components\":{\"api\":{}}}}}";
                references[version] = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await importer.ExecuteAsync(Bytes(envelope), "importer")).ManifestRevisionRef;
            }
        }
        var target = Target.Create("component", application: "assigned", component: "api");
        PersistedPlacementSelection? original = null;
        if (operation != "create")
        {
            await using var arrange = Services(connection);
            original = (await arrange.GetRequiredService<PlacementSelectionAdministration>().ApplyAsync("dev", "cluster", target,
                "standard-runtime", 1, "2", Absent, "original", default)).Selection!;
        }
        PlacementSelectionResult result;
        var settings = new NpgsqlConnectionStringBuilder(connection) { Pooling = true, MaxPoolSize = 1, Timeout = 1 };
        await using (var source = NpgsqlDataSource.Create(settings.ConnectionString))
        {
            var factory = new SingleConnectionFactory(source); var codec = new KubernetesPlacementCodec();
            var manifests = new PostgreSqlManifestImportStore(factory);
            var service = new PlacementSelectionAdministration(new PostgreSqlEnvironmentQuery(factory), new PostgreSqlPlacementSelectionScopeQuery(factory, manifests),
                new PostgreSqlPlacementRevisionStore(factory, codec), new PostgreSqlPlacementSelectionStore(factory, codec), TimeProvider.System);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            result = await service.ApplyAsync("dev", "cluster", target, "standard-runtime", operation == "update" ? 2 : 1,
                operation == "create" ? "2" : "1", original is null ? Absent : Match(original), "limited", deadline.Token);
            var found = await service.GetAsync("dev", "cluster", target, deadline.Token);
            Assert.Equal(PlacementSelectionOutcome.Found, found.Outcome); Assert.Equal(result.Selection, found.Selection);
        }
        Assert.Equal(operation == "create" ? PlacementSelectionOutcome.Created : operation == "update" ? PlacementSelectionOutcome.Updated : PlacementSelectionOutcome.Unchanged, result.Outcome);
        var accepted = result.Selection!;
        Assert.Equal(operation == "update" ? second.Key : first.Key, accepted.Reference);
        Assert.Equal(references[operation == "update" ? "1" : "2"], accepted.Audit.CheckedRevision);
        if (operation == "equal") Assert.Equal(original, accepted);
        else { Assert.Equal("limited", accepted.Audit.ActorId); Assert.NotEqual(original?.Token, accepted.Token); }
        await using var verify = Context(connection); var stored = await verify.PlacementSelections.AsNoTracking().SingleAsync();
        Assert.Equal(accepted.Token.Value, stored.Token); Assert.Equal(accepted.Audit.ActorId, stored.ActorId); Assert.Equal(accepted.Audit.ChangedAtUtc, stored.ChangedAtUtc);
        Assert.Equal(operation == "update" ? second.Id : first.Id, stored.RevisionId);
        Assert.Equal(Guid.Parse(accepted.Audit.CheckedRevision!.ApplicationRecordId.ToString()), stored.CheckedApplicationRecordId);
        Assert.Equal(accepted.Audit.CheckedRevision.RevisionNumber.Value, stored.CheckedRevisionNumber);
    }
    private sealed class SingleConnectionFactory(NpgsqlDataSource source) : IDbContextFactory<ManifestCatalogDbContext>
    {
        public ManifestCatalogDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(source).Options);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("equal")]
    [InlineData("mixed")]
    public async Task Selection_synchronized_CAS_races_have_truthful_reference_token_actor_and_audit(string race)
    {
        var (connection, first, second) = await SelectionDatabase(); var target = Target.Create("environment");
        PersistedPlacementSelection? original = null;
        if (race != "create") original = (await Select(SelectionService(connection), target, 1, Absent, "original")).Selection!;
        var barrier = new InsertBarrier(8);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var service = SelectionService(connection, barrier);
            var revision = race == "create" || race == "equal" || race == "mixed" && i % 2 == 0 ? 1 : 2;
            return await Select(service, target, revision, original is null ? Absent : Match(original), "actor-" + i);
        }));
        var accepted = results.Where(x => x.Outcome != PlacementSelectionOutcome.Conflict).ToArray();
        if (race == "equal") Assert.Equal(8, accepted.Length);
        else Assert.Single(results, x => x.Outcome == (race == "create" ? PlacementSelectionOutcome.Created : PlacementSelectionOutcome.Updated));
        Assert.All(results.Where(x => x.Outcome == PlacementSelectionOutcome.Conflict), x => Assert.Null(x.Selection));
        var winner = race == "equal" ? original! : Assert.Single(results, x => x.Outcome is PlacementSelectionOutcome.Created or PlacementSelectionOutcome.Updated).Selection!;
        Assert.Equal(race == "create" || race == "equal" ? first.Key : second.Key, winner.Reference);
        if (race != "equal")
        {
            var winningRequest = Array.FindIndex(results, x => x.Outcome is PlacementSelectionOutcome.Created or PlacementSelectionOutcome.Updated);
            Assert.Equal("actor-" + winningRequest, winner.Audit.ActorId);
            Assert.NotEqual(original?.Token, winner.Token);
        }
        foreach (var equal in results.Where(x => x.Outcome == PlacementSelectionOutcome.Unchanged)) Assert.Equal(original, equal.Selection);
        var reloaded = (await SelectionService(connection).GetAsync("dev", "cluster", target, default)).Selection;
        Assert.Equal(winner, reloaded);
        await using var verify = Context(connection); var row = Assert.Single(await verify.PlacementSelections.AsNoTracking().ToArrayAsync());
        Assert.Equal(winner.Token.Value, row.Token); Assert.Equal(winner.Audit.ActorId, row.ActorId); Assert.Equal(winner.Audit.ChangedAtUtc, row.ChangedAtUtc);
        Assert.Equal(race == "create" || race == "equal" ? first.Id : second.Id, row.RevisionId);
    }

    [Fact]
    public async Task Selection_preconditions_scope_integrity_and_corrupt_present_refusal_preserve_state()
    {
        var (connection, first, _) = await SelectionDatabase(); var service = SelectionService(connection); var target = Target.Create("named", "production");
        Assert.Equal(PlacementSelectionOutcome.InvalidContext, (await Select(service, target, 1, null)).Outcome);
        Assert.Equal(PlacementSelectionOutcome.RevisionNotFound, (await Select(service, target, 3, Absent)).Outcome);
        foreach (var (env, alias) in new[] { ("prod", "cluster"), ("dev", "other") })
            Assert.Equal(PlacementSelectionOutcome.RevisionNotFound, (await Select(service, target, 1, Absent, environment: env, alias: alias)).Outcome);
        var created = (await Select(service, target, 1, Absent)).Selection!;
        Assert.Equal(PlacementSelectionOutcome.Conflict, (await Select(service, target, 1, Absent)).Outcome);
        Assert.Equal(PlacementSelectionOutcome.Conflict, (await Select(service, target, 1, new PlacementSelectionPrecondition.ExpectedToken(PlacementSelectionToken.New()))).Outcome);
        Assert.Equal(created, (await Select(service, target, 1, Match(created), "later")).Selection);
        foreach (var invalid in new[] { "missing", "alias", "environment" })
        {
            await using var act = Context(connection);
            var row = new PlacementSelectionRow
            {
                EnvironmentRecordId = invalid == "environment" ? Guid.NewGuid() : Guid.Parse(first.Key.Environment.ToString()),
                ProviderAlias = invalid == "alias" ? "other" : "cluster",
                TargetKind = "named",
                Selector = "invalid",
                ApplicationId = "",
                ComponentRef = "",
                RevisionId = invalid == "missing" ? Guid.NewGuid() : first.Id,
                Token = Guid.NewGuid(),
                ActorId = "actor",
                ChangedAtUtc = DateTimeOffset.UtcNow
            };
            act.PlacementSelections.Add(row);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => act.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        await using (var arrange = Context(connection)) await arrange.Database.ExecuteSqlRawAsync("UPDATE placement_revisions SET format_version = 999");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.GetAsync("dev", "cluster", target, default));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Select(service, target, 1, Match(created)));
        await using var verify = Context(connection); var saved = Assert.Single(await verify.PlacementSelections.AsNoTracking().ToArrayAsync());
        Assert.Equal(created.Token.Value, saved.Token); Assert.Equal(first.Id, saved.RevisionId); Assert.Equal(created.Audit.ActorId, saved.ActorId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selection_precommit_failure_or_cancellation_preserves_prior_reference_and_audit(bool cancel)
    {
        var (connection, _, _) = await SelectionDatabase(); var target = Target.Create("environment");
        var first = (await Select(SelectionService(connection), target, 1, Absent)).Selection!;
        using var cancellation = new CancellationTokenSource();
        var act = Select(SelectionService(connection, new PrecommitFault(cancel, cancellation)), target, 2, Match(first), "later", cancellation.Token).AsTask();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => act); else await Assert.ThrowsAsync<IOException>(() => act);
        Assert.Equal(first, (await SelectionService(connection).GetAsync("dev", "cluster", target, default)).Selection);
        await using var verify = Context(connection); var row = await verify.PlacementSelections.AsNoTracking().SingleAsync();
        Assert.Equal(first.Token.Value, row.Token); Assert.Equal(first.Audit.ActorId, row.ActorId); Assert.Equal(first.Audit.ChangedAtUtc, row.ChangedAtUtc);
    }

    [Fact]
    public async Task Selection_current_base_upgrade_and_dump_restore_preserve_exact_state()
    {
        var connection = await Database(false);
        await using (var arrange = Context(connection))
        {
            await arrange.GetService<IMigrator>().MigrateAsync("20261111184028_AddPlacementRevisions");
            arrange.Environments.Add(new() { Id = Guid.CreateVersion7(), EnvironmentId = "dev" }); await arrange.SaveChangesAsync();
        }
        await using (var act = Services(connection))
        {
            await act.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            await act.GetRequiredService<PlacementAdministration>().ImportAsync("dev", "cluster", Bytes(PlacementSamples.Minimal), "importer", default);
        }
        var target = Target.Create("environment"); var original = (await Select(SelectionService(connection), target, 1, Absent)).Selection!;
        var restored = await Database(false); var directory = "/tmp/haby-selection-restore-" + Guid.NewGuid().ToString("N"); var dump = directory + "/catalog.dump";
        try
        {
            await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + new NpgsqlConnectionStringBuilder(connection).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
            Assert.Equal(original, (await SelectionService(restored).GetAsync("dev", "cluster", target, default)).Selection);
            Assert.Equal(original, (await Select(SelectionService(restored), target, 1, Match(original), "later")).Selection);
            await using var verify = Context(restored); Assert.False(verify.Database.HasPendingModelChanges());
            Assert.Equal(original.Token.Value, (await verify.PlacementSelections.AsNoTracking().SingleAsync()).Token);
            Assert.Empty(await verify.DurablePlans.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.DurableOperations.AsNoTracking().ToArrayAsync());
        }
        finally { await fixture.RunClientUtilityAsync("rm", "-f", dump); await fixture.RunClientUtilityAsync("rmdir", directory); }
    }
}
