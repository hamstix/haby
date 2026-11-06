using System.Data.Common;
using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
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
using static Haby.Tests.ProfilePersistenceSamples;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ConfigurationProfilePersistenceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Fresh_composition_retains_full_secret_declaration_audit_and_exact_retry_behavior()
    {
        var connection = await Database();
        var node = Node(); var expected = Parse(node);
        AcceptedConfigurationProfile accepted;
        await using (var first = Services(connection))
        {
            Assert.IsType<ImportConfigurationProfileResult.InvalidDeclaration>(await Import(first).ExecuteAsync(Encoding.UTF8.GetBytes("{bad"), "actor"));
            accepted = Success(await Import(first).ExecuteAsync(Bytes(node), "first")).Revision;
            Assert.Equal(new ImportAuditMetadata("first", Time), accepted.AuditMetadata);
        }
        await using (var fresh = Services(connection))
        {
            var query = fresh.GetRequiredService<IConfigurationProfileRevisionQuery>();
            Assert.Equal(accepted, await query.FindAsync(expected.Reference));
            Assert.Null(await query.FindAsync(new(expected.Reference.ProfileId, 2)));
            var repeat = Success(await Import(fresh, Time.AddDays(1)).ExecuteAsync(Bytes(Equivalent(node)), "later"));
            Assert.Equal(ImportConfigurationProfileResult.ImportDisposition.ExistingEquivalent, repeat.Disposition);
            Assert.Equal(accepted, repeat.Revision);
            node["spec"]!["staticContributions"]!["telemetry"]!["secretProjections"]![1]!["secretRef"]!["version"] = 8;
            Assert.IsType<ImportConfigurationProfileResult.RevisionContentConflict>(await Import(fresh).ExecuteAsync(Bytes(node), "conflict"));
            Assert.Equal(accepted, await query.FindAsync(expected.Reference));
            var component = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Encoding.UTF8.GetBytes("""
                {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"empty"},"spec":{"components":{"api":{}}}}}
                """), new([], [], [], []))).Candidate.Declaration.Spec.Components.Single().Value;
            var expanded = new ConfigurationProfileExpander(new([repeat.Revision.Declaration])).Expand(
                [new ProfileDecision.Use(expected.Reference)], [], [new(ManifestLocalId.Create("api"), component, [])]);
            Assert.Null(expanded.Layouts);
            Assert.Contains(expanded.Diagnostics, x => x.Code == "profile.secret-projections.unsupported");
        }
        await using var check = Context(connection);
        var row = await check.ProfileRevisions.AsNoTracking().SingleAsync();
        Assert.Equal(expected, ProfileSnapshotCodec.Decode(row.Snapshot, row.ProfileId, row.Revision, row.SnapshotFormatVersion, row.ModelVersion));
        Assert.Empty(await check.Secrets.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.Environments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.ProviderInstances.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.DurablePlans.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.Applications.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronized_equal_and_unequal_insert_races_preserve_one_full_winner(bool different)
    {
        var connection = await Database();
        var barrier = new CandidateBarrier(8);
        var payloads = Enumerable.Range(0, 8).Select(index =>
        {
            var node = index % 2 == 0 ? Node() : Equivalent(Node());
            if (different && index % 2 == 1) node["spec"]!["staticContributions"]!["telemetry"]!["secretProjections"]![0]!["secretRef"]!["version"] = 99;
            return node;
        }).ToArray();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            var store = new PostgreSqlConfigurationProfileStore(new Factory(connection, barrier));
            return await new ImportConfigurationProfile(store, new Clock(Time.AddMinutes(index)))
                .ExecuteAsync(Bytes(payloads[index]), "actor-" + index);
        }));
        var winner = Assert.Single(results.OfType<ImportConfigurationProfileResult.Succeeded>(), x => x.Disposition == ImportConfigurationProfileResult.ImportDisposition.Created);
        Assert.Equal(different ? 4 : 0, results.OfType<ImportConfigurationProfileResult.RevisionContentConflict>().Count());
        Assert.Equal(different ? 3 : 7, results.OfType<ImportConfigurationProfileResult.Succeeded>().Count(x => x.Disposition == ImportConfigurationProfileResult.ImportDisposition.ExistingEquivalent));
        var winningIndex = int.Parse(winner.Revision.AuditMetadata.ActorId[6..]);
        Assert.Equal(Parse(payloads[winningIndex]), winner.Revision.Declaration);
        Assert.Equal(Time.AddMinutes(winningIndex), winner.Revision.AuditMetadata.ImportedAt);
        Assert.All(results.OfType<ImportConfigurationProfileResult.Succeeded>(), x => Assert.Equal(winner.Revision, x.Revision));
        await using (var fresh = Services(connection))
            Assert.Equal(winner.Revision, await fresh.GetRequiredService<IConfigurationProfileRevisionQuery>().FindAsync(winner.Revision.Reference));
        await using var check = Context(connection);
        Assert.Single(await check.ProfileRevisions.AsNoTracking().ToArrayAsync());
        // A direct writer bypassing store coordination still cannot add the identity twice.
        var duplicate = await check.ProfileRevisions.AsNoTracking().SingleAsync();
        check.ProfileRevisions.Add(duplicate);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => check.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Theory]
    [InlineData("cancel-before-save", false)]
    [InlineData("cancel-after-save", false)]
    [InlineData("fail-after-save", false)]
    [InlineData("cancel-commit-start", true)]
    [InlineData("cancel-after-commit", true)]
    [InlineData("lost-response", true)]
    public async Task Transaction_boundaries_do_not_publish_false_success_or_partial_audit(string mode, bool durable)
    {
        var connection = await Database();
        using var cancellation = new CancellationTokenSource();
        var store = new PostgreSqlConfigurationProfileStore(new Factory(connection,
            new SaveFault(mode, cancellation), new CommitFault(mode, cancellation)));
        var importer = new ImportConfigurationProfile(store, new Clock(Time.AddTicks(123)));
        var operation = importer.ExecuteAsync(Bytes(Node()), "winner", cancellation.Token).AsTask();
        if (mode.StartsWith("cancel-", StringComparison.Ordinal) && !durable) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else if (mode is "fail-after-save" or "lost-response") await Assert.ThrowsAsync<IOException>(() => operation);
        else
        {
            var created = Success(await operation);
            Assert.Equal(ImportConfigurationProfileResult.ImportDisposition.Created, created.Disposition);
            Assert.Equal(Time.AddTicks(120), created.Revision.AuditMetadata.ImportedAt);
        }
        await using (var check = Context(connection))
        {
            var rows = await check.ProfileRevisions.AsNoTracking().ToArrayAsync();
            Assert.Equal(durable ? 1 : 0, rows.Length);
            if (durable)
            {
                Assert.Equal("winner", rows[0].ActorId);
                Assert.Equal(Time.AddTicks(120), rows[0].ImportedAt);
            }
        }
        await using var fresh = Services(connection);
        var retry = Success(await Import(fresh, Time.AddDays(1)).ExecuteAsync(Bytes(Node()), "retry"));
        Assert.Equal(durable ? ImportConfigurationProfileResult.ImportDisposition.ExistingEquivalent : ImportConfigurationProfileResult.ImportDisposition.Created, retry.Disposition);
        Assert.Equal(durable ? "winner" : "retry", retry.Revision.AuditMetadata.ActorId);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("format")]
    [InlineData("model")]
    [InlineData("identity")]
    [InlineData("audit")]
    public async Task Corrupt_saved_profiles_fail_visibly_without_overwrite_or_false_not_found(string corruption)
    {
        var connection = await Database();
        await using (var first = Services(connection)) await Import(first).ExecuteAsync(Bytes(Node()), "original");
        string saved;
        await using (var arrange = Context(connection))
        {
            var row = await arrange.ProfileRevisions.SingleAsync();
            if (corruption == "format") row.SnapshotFormatVersion++;
            if (corruption == "model") row.ModelVersion++;
            if (corruption == "identity") row.Snapshot = row.Snapshot.Replace("\"id\":\"layout\"", "\"id\":\"another\"", StringComparison.Ordinal);
            if (corruption == "snapshot") row.Snapshot = "{private-value";
            if (corruption == "audit") row.ActorId = "bad\nactor";
            saved = row.Snapshot;
            await arrange.SaveChangesAsync();
        }
        await using (var fresh = Services(connection))
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await fresh.GetRequiredService<IConfigurationProfileRevisionQuery>().FindAsync(Parse(Node()).Reference));
            Assert.DoesNotContain("private-value", error.ToString());
            await Assert.ThrowsAsync<InvalidDataException>(async () => await Import(fresh).ExecuteAsync(Bytes(Node()), "replacement"));
        }
        await using var check = Context(connection);
        Assert.Equal(saved, (await check.ProfileRevisions.AsNoTracking().SingleAsync()).Snapshot);
    }

    [Fact]
    public async Task Real_dump_restore_retains_full_profiles_references_audit_and_side_effect_free_reads()
    {
        var source = await Database(); var restored = await Database(migrate: false);
        var ordinary = Node(false); ordinary["metadata"]!["revision"] = 2;
        var accepted = new List<AcceptedConfigurationProfile>();
        await using (var services = Services(source))
        {
            foreach (var node in new[] { Node(), ordinary })
                accepted.Add(Success(await Import(services).ExecuteAsync(Bytes(node), "original")).Revision);
        }
        var directory = "/tmp/haby-profile-restore-" + Guid.NewGuid().ToString("N");
        var dump = directory + "/catalog.dump";
        Exception? failure = null;
        try
        {
            await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump,
                "--dbname=" + new NpgsqlConnectionStringBuilder(source).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges",
                "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
            await using (var services = Services(restored))
            {
                await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
                foreach (var expected in accepted)
                {
                    Assert.Equal(expected, await services.GetRequiredService<IConfigurationProfileRevisionQuery>().FindAsync(expected.Reference));
                    var node = expected.Reference.Revision == 1 ? Node() : ordinary;
                    var retry = Success(await Import(services, Time.AddYears(1)).ExecuteAsync(Bytes(node), "retry"));
                    Assert.Equal(ImportConfigurationProfileResult.ImportDisposition.ExistingEquivalent, retry.Disposition);
                    Assert.Equal(expected, retry.Revision);
                }
            }
            await using var check = Context(restored);
            Assert.Equal(2, await check.ProfileRevisions.CountAsync());
            Assert.Empty(await check.Secrets.AsNoTracking().ToArrayAsync());
            Assert.Empty(await check.ProviderInstances.AsNoTracking().ToArrayAsync());
            Assert.Empty(await check.DurableOperations.AsNoTracking().ToArrayAsync());
            Assert.Equal(check.Database.GetMigrations(), await check.Database.GetAppliedMigrationsAsync());
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { await fixture.RunClientUtilityAsync("rm", "-f", dump); await fixture.RunClientUtilityAsync("rmdir", directory); }
            catch when (failure is not null) { /* Preserve the primary test failure. */ }
        }
    }

    [Fact]
    public async Task Current_base_catalog_accepts_additive_profile_migration_without_reset()
    {
        var connection = await Database(migrate: false);
        await using (var arrange = Context(connection))
        {
            await arrange.GetService<IMigrator>().MigrateAsync("20261103084056_AddProviderResourceReservations");
            arrange.Environments.Add(new() { Id = Guid.CreateVersion7(), EnvironmentId = "retained" });
            await arrange.SaveChangesAsync();
        }
        await using (var act = Services(connection))
            await act.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await using var check = Context(connection);
        Assert.Equal("retained", (await check.Environments.AsNoTracking().SingleAsync()).EnvironmentId);
        Assert.Empty(await check.ProfileRevisions.AsNoTracking().ToArrayAsync());
        Assert.False(check.Database.HasPendingModelChanges());
        Assert.Equal(check.Database.GetMigrations(), await check.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Saved_profiles_and_slotted_application_drive_real_catalog_expansion_and_redacted_composition()
    {
        var connection = await Database();
        var layout = Node(false);
        layout["spec"]!["bindingRules"]!["connection"]!["documentProjections"]!.AsArray().RemoveAt(1);
        layout["spec"]!["bindingRules"]!["events"] = JsonNode.Parse("""
            {"slot":"events","rendererRef":"sample.events/v1","options":{"durable":true},"documentProjections":[{"documentRef":"settings","path":"/Events"}]}
            """u8);
        var replacement = layout.DeepClone(); replacement["metadata"]!["revision"] = 2;
        replacement["spec"]!["staticContributions"]!["logging"]!["value"] = JsonNode.Parse("""{"Level":{"Default":"Warning"}}"""u8);
        var catalog = new ManifestContractCatalog(
            [new(ResourceTypeId.Create("sample.resource/v1"), """{"type":"object"}""", """{"type":"object"}""")], [], [],
            [new(ConfigurationRendererId.Create("sample.connection/v1")), new(ConfigurationRendererId.Create("sample.events/v1"))]);
        ManifestRevisionRef applicationRef;
        await using (var first = Services(connection))
        {
            await Import(first).ExecuteAsync(Bytes(layout), "profile-author");
            await Import(first).ExecuteAsync(Bytes(replacement), "profile-author");
            var manifest = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                first.GetRequiredService<IManifestImportStore>(), catalog, new Clock(Time)).ExecuteAsync(Encoding.UTF8.GetBytes("""
                {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"slotted"},"spec":{
                  "resources":{"login":{"type":"sample.resource/v1","providerRef":"absent","properties":{}},"queue":{"type":"sample.resource/v1","providerRef":"absent","properties":{}}},
                  "components":{"api":{"bindings":{"main":{"target":{"kind":"local-resource","resourceRef":"login"},"configurationSlot":"main","options":{"timeout":15}},"events":{"target":{"kind":"local-resource","resourceRef":"queue"},"configurationSlot":"events"}}},
                    "worker":{"bindings":{"main":{"target":{"kind":"local-resource","resourceRef":"login"},"configurationSlot":"main","options":{"timeout":60}},"events":{"target":{"kind":"local-resource","resourceRef":"queue"},"configurationSlot":"events"}}}}
                }}}
                """), "app-author"));
            applicationRef = manifest.ManifestRevisionRef;
        }
        await using var fresh = Services(connection);
        var query = fresh.GetRequiredService<IConfigurationProfileRevisionQuery>();
        var one = (await query.FindAsync(Parse(layout).Reference))!;
        var two = (await query.FindAsync(Parse(replacement).Reference))!;
        var app = (await fresh.GetRequiredService<IManifestRevisionQuery>().FindAsync(applicationRef))!;
        await fresh.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("expansion"), default);
        var admin = AssignmentAdmin(fresh);
        await admin.ApplyAsync(AssignmentBytes(new ProfileAssignmentTarget.Environment(), new ProfileDecision.Use(one.Reference)), "expansion", null, new AssignmentPrecondition.ExpectedAbsence(), "operator");
        await admin.ApplyAsync(AssignmentBytes(new ProfileAssignmentTarget.Application(app.ApplicationId)), "expansion", null, new AssignmentPrecondition.ExpectedAbsence(), "operator");
        var workerTarget = new ProfileAssignmentTarget.Component(app.ApplicationId, ManifestLocalId.Create("worker"));
        await admin.ApplyAsync(AssignmentBytes(workerTarget, new ProfileDecision.Use(two.Reference)), "expansion", "1", new AssignmentPrecondition.ExpectedAbsence(), "operator");
        var envDecisions = (await admin.GetAsync("expansion", new ProfileAssignmentTarget.Environment())).Assignment!.Declaration.Decisions;
        var appDecisions = (await admin.GetAsync("expansion", new ProfileAssignmentTarget.Application(app.ApplicationId))).Assignment!.Declaration.Decisions;
        var workerDecisions = (await admin.GetAsync("expansion", workerTarget)).Assignment!.Declaration.Decisions;
        var components = app.Declaration.Spec.Components.Select(x => new ProfileComponentInput(x.Key, x.Value,
            x.Key.Value == "worker" ? workerDecisions : []));
        ProfileExpansionResult expanded;
        await using (var capture = Context(connection))
        await using (var transaction = await capture.Database.BeginTransactionAsync())
        {
            var environment = (await fresh.GetRequiredService<IEnvironmentCatalog>().GetAsync(EnvironmentId.Create("expansion"), default))!;
            var prepared = await ConfigurationPlanningCapture.CaptureAsync(capture, environment, applicationRef, app.Declaration, default);
            Assert.NotNull(prepared.Snapshot);
            var encoded = ConfigurationPlanningSnapshotCodec.Encode(prepared.Snapshot);
            Assert.Equal(prepared.Snapshot, ConfigurationPlanningSnapshotCodec.Decode(encoded, applicationRef));
            expanded = prepared.Expansion;
        }
        Assert.True(expanded.IsSuccess, string.Join(";", expanded.Diagnostics));
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        var composer = new ConfigurationComposer(new([], []));
        foreach (var component in expanded.Layouts!)
        {
            var worker = component.Component.Value == "worker";
            Assert.Equal(worker ? two.Reference : one.Reference,
                Assert.IsType<ProfileDecision.Use>(Assert.Single(component.Decisions).Decision).Reference);
            var logging = Assert.Single(component.Contributions).Contribution;
            Assert.Equal((worker ? two : one).Declaration.Contributions.Single().Value, logging.Value);
            Assert.Equal(2, component.BindingRequests.Count);
            var connectionRequest = component.BindingRequests.Single(x => x.Slot!.Value == "main");
            Assert.Equal(ConfigurationNumber.Parse(worker ? "60" : "15"), connectionRequest.Options.Members["timeout"]);
            var projections = component.Contributions.Select(x => new ConfigurationProjection(x.Contribution.Path, new(x.Contribution.Value), x.Contribution.Id)).ToList();
            foreach (var request in component.BindingRequests)
            {
                // Two typed synthetic output families exercise the existing ready-fragment seam.
                // This test neither registers nor executes a production renderer or reads material.
                ConfigurationFragment fragment = request.Renderer.Value switch
                {
                    "sample.connection/v1" => ConnectionFragment(new(secret, request.Options)),
                    "sample.events/v1" => EventsFragment(new(secret, request.Options)),
                    _ => throw new InvalidOperationException("Unexpected test renderer.")
                };
                projections.AddRange(request.Projections.Select(x => new ConfigurationProjection(x.Path, fragment, request.BindingId)));
            }
            var composed = composer.Compose(Assert.Single(component.Documents).Definition.Template, projections);
            Assert.True(composed.IsSuccess, string.Join(";", composed.Diagnostics));
            Assert.False(ConfigurationJson.Serialize(composed.Document!).IsSuccess);
            var preview = ConfigurationJson.Preview(composed.Document!);
            Assert.Equal(new[] { "/Connections/Main/credential", "/Events/credential" }, preview.RedactedPaths);
            Assert.DoesNotContain(secret.SecretId.ToString(), preview.Json);
            Assert.DoesNotContain(secret.SecretVersionId.ToString(), preview.Json);
        }
        // A later accepted revision omits the old worker and renames api. Read only exact
        // current Component targets; the old local exception remains inspectable, never transferred.
        var next = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            fresh.GetRequiredService<IManifestImportStore>(), catalog, new Clock(Time)).ExecuteAsync(Encoding.UTF8.GetBytes("""
            {"applicationVersion":"2","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"slotted"},"spec":{"components":{"renamed":{}}}}}
            """), "app-author"));
        var currentApp = (await fresh.GetRequiredService<IManifestRevisionQuery>().FindAsync(next.ManifestRevisionRef))!;
        var renamedTarget = new ProfileAssignmentTarget.Component(app.ApplicationId, ManifestLocalId.Create("renamed"));
        Assert.Equal(AssignmentOutcome.NotFound, (await admin.GetAsync("expansion", renamedTarget)).Outcome);
        Assert.Equal(workerDecisions, (await admin.GetAsync("expansion", workerTarget)).Assignment!.Declaration.Decisions);
        var currentInputs = currentApp.Declaration.Spec.Components.Select(x => new ProfileComponentInput(x.Key, x.Value, []));
        var inherited = new ConfigurationProfileExpander(new([one.Declaration, two.Declaration])).Expand(envDecisions, appDecisions, currentInputs);
        Assert.True(inherited.IsSuccess);
        Assert.Equal(one.Reference, Assert.IsType<ProfileDecision.Use>(Assert.Single(Assert.Single(inherited.Layouts!).Decisions).Decision).Reference);
        var appTarget = new ProfileAssignmentTarget.Application(app.ApplicationId);
        var local = (await admin.GetAsync("expansion", appTarget)).Assignment!;
        var disabled = await admin.ApplyAsync(AssignmentBytes(appTarget, new ProfileDecision.Disable(one.Reference.ProfileId)), "expansion", null, Match(local), "disable");
        var disabledRead = (await admin.GetAsync("expansion", appTarget)).Assignment!;
        var suppressed = new ConfigurationProfileExpander(new([one.Declaration, two.Declaration])).Expand(envDecisions, disabledRead.Declaration.Decisions, currentInputs);
        Assert.True(suppressed.IsSuccess); Assert.Empty(Assert.Single(suppressed.Layouts!).Documents);
        var reset = await admin.ApplyAsync(AssignmentBytes(appTarget), "expansion", null, Match(disabled.Assignment!), "inherit");
        Assert.Equal(AssignmentOutcome.Updated, reset.Outcome);
        var emptyRead = (await admin.GetAsync("expansion", appTarget)).Assignment!;
        Assert.Empty(emptyRead.Declaration.Decisions);
        var restored = new ConfigurationProfileExpander(new([one.Declaration, two.Declaration])).Expand(envDecisions, emptyRead.Declaration.Decisions, currentInputs);
        Assert.True(restored.IsSuccess); Assert.Single(Assert.Single(restored.Layouts!).Documents);
    }

    private sealed record ConnectionOutput(SecretRef Credential, ConfigurationObject Options);
    private sealed record EventsOutput(SecretRef Credential, ConfigurationObject Options);
    private static ConfigurationFragment ConnectionFragment(ConnectionOutput output) => Fragment(output.Credential, output.Options);
    private static ConfigurationFragment EventsFragment(EventsOutput output) => Fragment(output.Credential, output.Options);
    private static ConfigurationFragment Fragment(SecretRef credential, ConfigurationObject options) => new(new ConfigurationObject(
        [KeyValuePair.Create<string, ConfigurationValue>("credential", new ConfigurationSecretReference(credential)), KeyValuePair.Create<string, ConfigurationValue>("options", options)]));

    private async Task<string> Database(bool migrate = true)
    {
        var name = "haby_profiles_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }
        var result = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
        if (migrate)
        {
            await using var services = Services(result);
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        }
        return result;
    }
    private static ManifestCatalogDbContext Context(string connection) => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
    private static ServiceProvider Services(string connection) => new ServiceCollection().AddPostgreSqlManifestCatalog(connection).BuildServiceProvider();
    private static ImportConfigurationProfile Import(IServiceProvider services, DateTimeOffset? now = null) => new(
        services.GetRequiredService<IConfigurationProfileImportStore>(), new Clock(now ?? Time));
    private static ImportConfigurationProfileResult.Succeeded Success(ImportConfigurationProfileResult result) => Assert.IsType<ImportConfigurationProfileResult.Succeeded>(result);

    private sealed class Factory(string connection, params IInterceptor[] interceptors) : IDbContextFactory<ManifestCatalogDbContext>
    {
        public ManifestCatalogDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).AddInterceptors(interceptors).Options);
    }
    private sealed class CandidateBarrier(int count) : SaveChangesInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrived) == count) _ready.SetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }
    private sealed class SaveFault(string mode, CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (mode == "cancel-before-save") { cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (mode == "cancel-after-save") cancellation.Cancel();
            if (mode == "fail-after-save") throw new IOException("Injected precommit failure.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CommitFault(string mode, CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            if (mode == "cancel-commit-start") cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (mode == "cancel-after-commit") cancellation.Cancel();
            if (mode == "lost-response") throw new IOException("Injected lost commit response.");
            return Task.CompletedTask;
        }
    }
}
