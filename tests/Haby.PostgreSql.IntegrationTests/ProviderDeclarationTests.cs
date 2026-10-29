using System.Text;
using System.Text.Json;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class ProviderDeclarationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    internal const string Declaration = """
        {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"pg-main","environment":"pilot"},
        "spec":{"revision":1,"providerKind":"postgresql/v1","configurationContract":"haby.postgresql.connection/v1",
        "implementationId":"haby.postgresql.database/v1alpha1","properties":{"host":"unreachable.invalid","port":5432,
        "maintenanceDatabase":"postgres","administratorUserName":"admin"},
        "secretRefs":{"administrator-password":{"secretName":"admin","version":1}}}}
        """;

    [Fact]
    public async Task Confirmed_transfer_keeps_pinned_history_and_audits_once_after_restart()
    {
        var connection = await Database();
        ProviderResult first;
        await using (var services = Services(connection))
        {
            first = await Apply(services);
            Assert.Equal(ProviderOutcome.Created, first.Outcome);
            var changed = Json(3, host: "new.invalid");
            var unconfirmed = await Apply(services, changed);
            Assert.Equal(ProviderOutcome.IdentityConfirmationRequired, unconfirmed.Outcome);
            Assert.Equal(1, unconfirmed.PreviousRevision);
            Assert.Equal(ProviderOutcome.StaleBaseline, (await Apply(services, changed, true, 2)).Outcome);
            Assert.Equal(ProviderOutcome.Created, (await Apply(services, changed, true, 1)).Outcome);
            Assert.Equal(ProviderOutcome.ExistingEquivalent, (await Apply(services, changed)).Outcome);
            Assert.Equal(ProviderOutcome.ExistingEquivalent, (await Apply(services, changed, true, 1)).Outcome);
            Assert.Equal(ProviderOutcome.ExistingEquivalent, (await Apply(services)).Outcome);
            Assert.Equal(ProviderOutcome.RevisionContentConflict, (await Apply(services, Json(1, database: "different"))).Outcome);
            Assert.Equal(ProviderOutcome.RevisionNotIncreasing, (await Apply(services, Json(2))).Outcome);
            Assert.Equal(ProviderOutcome.StaleBaseline, (await Apply(services, Json(4, host: "new.invalid"), true, 1)).Outcome);
        }
        await using (var restarted = Services(connection))
        {
            var store = restarted.GetRequiredService<IProviderConfigurationStore>();
            var pinned = await store.FindRevisionAsync<PostgreSqlProviderConfiguration>(first.Provider!.Environment,
                first.Provider.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default);
            Assert.Equal("unreachable.invalid", pinned!.Configuration.Host);
            var transferred = await store.FindRevisionAsync<PostgreSqlProviderConfiguration>(first.Provider.Environment,
                first.Provider.InstanceId, ProviderConfigurationRevisionNumber.Create(3), default);
            Assert.Equal("new.invalid", transferred!.Configuration.Host);
            Assert.Equal(ProviderOutcome.ExistingEquivalent, (await Apply(restarted, Json(3, host: "new.invalid"))).Outcome);
        }
        await using var check = Context(connection);
        Assert.Single(await check.ProviderInstances.AsNoTracking().ToArrayAsync());
        var revisions = await check.ProviderConfigurationRevisions.AsNoTracking().OrderBy(x => x.RevisionNumber).ToArrayAsync();
        Assert.Equal(2, revisions.Length);
        Assert.Null(revisions[0].ConfirmationActor);
        Assert.Equal(1, revisions[1].ConfirmedPreviousRevision);
        Assert.Equal("actor", revisions[1].ConfirmationActor);
        Assert.NotNull(revisions[1].ConfirmedAt);
        Assert.Equal(2, await check.ProviderRevisionSecretRefs.CountAsync());
        Assert.Empty(await check.ProviderAliases.AsNoTracking().ToArrayAsync());
        Assert.Empty(await check.DurableCreateOperations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Natural_identity_races_equivalence_conflicts_and_stale_confirmations_are_atomic()
    {
        var connection = await Database();
        await using var services = Services(connection);
        var requests = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ => await Apply(services)));
        Assert.Single(requests, x => x.Outcome == ProviderOutcome.Created);
        Assert.Equal(11, requests.Count(x => x.Outcome == ProviderOutcome.ExistingEquivalent));
        Assert.Single(requests.Select(x => x.Provider!.InstanceId).Distinct());
        var revision = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i => await Apply(services, Json(5, database: i % 2 == 0 ? "one" : "two"))));
        Assert.Single(revision, x => x.Outcome == ProviderOutcome.Created);
        Assert.Equal(4, revision.Count(x => x.Outcome == ProviderOutcome.ExistingEquivalent));
        Assert.Equal(5, revision.Count(x => x.Outcome == ProviderOutcome.RevisionContentConflict));
        var transfers = await Task.WhenAll(new[] { 6, 7 }.Select(async n => await Apply(services, Json(n, host: "transfer.invalid"), true, 5)));
        Assert.Single(transfers, x => x.Outcome == ProviderOutcome.Created);
        Assert.Single(transfers, x => x.Outcome is ProviderOutcome.StaleBaseline or ProviderOutcome.RevisionNotIncreasing);
        await using var check = Context(connection);
        Assert.Equal(3, await check.ProviderConfigurationRevisions.CountAsync());
        Assert.Equal(1, await check.ProviderConfigurationRevisions.CountAsync(x => x.ConfirmationActor != null));
        Assert.True(await check.ProviderConfigurationRevisions.AllAsync(x => x.IsSealed));
    }

    [Fact]
    public async Task Secret_resolution_is_exact_environment_scoped_and_never_reads_material()
    {
        var connection = await Database();
        await using (var arrange = Services(connection))
        {
            using var sameMaterial = new SecretMaterial([1, 2, 3]);
            await arrange.GetRequiredService<SecretAdministration>().CreateAsync("pilot", "admin-two", Guid.NewGuid(), sameMaterial, "actor", default);
        }
        // The apply path has no ISecretStore dependency; reject any payload-table reads as well.
        await using var services = Services(connection, intercept: new NoSecretMaterial());
        Assert.Equal(ProviderOutcome.EnvironmentMismatch, (await services.GetRequiredService<ProviderAdministration>()
            .ApplyAsync(Encoding.UTF8.GetBytes(Declaration), "other", false, null, "actor", default)).Outcome);
        Assert.Equal(ProviderOutcome.SecretReferenceUnavailable, (await Apply(services, Declaration.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal))).Outcome);
        Assert.Equal(ProviderOutcome.SecretReferenceUnavailable, (await Apply(services, Declaration.Replace("\"environment\":\"pilot\"", "\"environment\":\"other\"", StringComparison.Ordinal))).Outcome);
        Assert.Equal(ProviderOutcome.InvalidRequest, (await Apply(services, Declaration.Replace("administrator-password", "wrong-slot", StringComparison.Ordinal))).Outcome);
        Assert.Equal(ProviderOutcome.EnvironmentNotFound, (await Apply(services, Declaration.Replace("\"environment\":\"pilot\"", "\"environment\":\"absent\"", StringComparison.Ordinal))).Outcome);
        var first = await Apply(services);
        Assert.Equal(ProviderOutcome.Created, first.Outcome);
        Assert.Equal(ProviderOutcome.RevisionContentConflict, (await Apply(services,
            Declaration.Replace("\"secretName\":\"admin\"", "\"secretName\":\"admin-two\"", StringComparison.Ordinal))).Outcome);
        Assert.Equal(ProviderOutcome.RevisionNotIncreasing, (await Apply(services, Json(3, name: "new-name"))).Outcome);
        var reordered = "  " + Declaration.Replace("\"host\":\"unreachable.invalid\",\"port\":5432", "\"port\":5432,\"host\":\"unreachable.invalid\"", StringComparison.Ordinal);
        Assert.Equal(ProviderOutcome.ExistingEquivalent, (await Apply(services, reordered)).Outcome);
        await using var check = Context(connection);
        var secret = await check.Secrets.AsNoTracking().SingleAsync(x => x.Name == "admin");
        var version = await check.SecretVersions.AsNoTracking().SingleAsync(x => x.SecretId == secret.Id && x.Number == 1);
        var reference = await check.ProviderRevisionSecretRefs.AsNoTracking().SingleAsync();
        Assert.Equal(secret.Id, reference.SecretId);
        Assert.Equal(version.Id, reference.SecretVersionId);
    }

    [Fact]
    public async Task Failure_after_first_save_rolls_back_instance_revision_refs_and_confirmation()
    {
        var connection = await Database();
        await using (var services = Services(connection, intercept: new RejectSealing()))
            Assert.Equal(ProviderOutcome.StorageUnavailable, (await Apply(services)).Outcome);
        await using (var check = Context(connection))
        {
            Assert.Empty(await check.ProviderInstances.AsNoTracking().ToArrayAsync());
            Assert.Empty(await check.ProviderConfigurationRevisions.AsNoTracking().ToArrayAsync());
            Assert.Empty(await check.ProviderRevisionSecretRefs.AsNoTracking().ToArrayAsync());
        }
        await using (var services = Services(connection)) Assert.Equal(ProviderOutcome.Created, (await Apply(services)).Outcome);
        await using (var services = Services(connection, intercept: new RejectSealing()))
            Assert.Equal(ProviderOutcome.StorageUnavailable, (await Apply(services, Json(2, host: "new.invalid"), true, 1)).Outcome);
        await using var verify = Context(connection);
        Assert.Single(await verify.ProviderConfigurationRevisions.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.ProviderRevisionSecretRefs.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Second_typed_adapter_persists_reloads_and_missing_handler_keeps_metadata_available()
    {
        var connection = await Database();
        var json = """
            {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"sample","environment":"pilot"},
            "spec":{"revision":1,"providerKind":"sample/v1","configurationContract":"sample.configuration/v1",
            "implementationId":"sample.impl/v1","properties":{"Target":"sample-target","ImplementationId":"sample.impl/v1"},"secretRefs":{}}}
            """;
        ProviderResult first;
        await using (var services = Services(connection, sample: true))
        {
            first = await Apply(services, json);
            Assert.Equal(ProviderOutcome.Created, first.Outcome);
        }
        await using (var restarted = Services(connection, sample: true))
        {
            var value = await restarted.GetRequiredService<IProviderConfigurationStore>().FindRevisionAsync<SampleConfiguration>(
                first.Provider!.Environment, first.Provider.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default);
            Assert.Equal("sample-target", value!.Configuration.Target);
            Assert.Equal(ProviderOutcome.ExistingEquivalent, (await Apply(restarted, json)).Outcome);
        }
        await using (var missing = Services(connection))
        {
            var admin = missing.GetRequiredService<ProviderAdministration>();
            Assert.Equal(ProviderOutcome.Found, (await admin.GetAsync("pilot", "sample", 1, default)).Outcome);
            Assert.Equal(ProviderOutcome.Listed, (await admin.ListAsync("pilot", 50, null, default)).Outcome);
            Assert.Equal(ProviderOutcome.UnsupportedConfiguration, (await Apply(missing, json)).Outcome);
        }
    }

    [Fact]
    public async Task Implementation_changes_require_explicit_compatibility_and_exact_previous_decoder()
    {
        var connection = await Database();
        var json = """
            {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"sample","environment":"pilot"},
            "spec":{"revision":1,"providerKind":"sample/v1","configurationContract":"sample.configuration/v1",
            "implementationId":"sample.impl/v1","properties":{"Target":"sample-target","ImplementationId":"sample.impl/v1"},"secretRefs":{}}}
            """;
        var updated = json.Replace("\"revision\":1", "\"revision\":2", StringComparison.Ordinal).Replace("sample.impl/v1", "sample.impl/v2", StringComparison.Ordinal);
        await using (var original = Services(connection, sample: true)) Assert.Equal(ProviderOutcome.Created, (await Apply(original, json)).Outcome);
        await using (var incompatible = Services(connection, sample: true, configure: s =>
            s.AddSingleton<IProviderDeclarationAdapter>(new SampleDeclarationAdapter("sample.impl/v2"))))
            Assert.Equal(ProviderOutcome.UnsupportedConfiguration, (await Apply(incompatible, updated)).Outcome);
        await using (var missingPrevious = Services(connection, configure: s =>
            s.AddSingleton<IProviderConfigurationCodec, SampleConfigurationCodec>()
                .AddSingleton<IProviderDeclarationAdapter>(new SampleDeclarationAdapter("sample.impl/v2", compatible: true))))
        {
            Assert.Equal(ProviderOutcome.Found, (await missingPrevious.GetRequiredService<ProviderAdministration>().GetAsync("pilot", "sample", 1, default)).Outcome);
            Assert.Equal(ProviderOutcome.ConfigurationUnavailable, (await Apply(missingPrevious, updated)).Outcome);
        }
        await using (var compatible = Services(connection, sample: true, configure: s =>
            s.AddSingleton<IProviderDeclarationAdapter>(new SampleDeclarationAdapter("sample.impl/v2", compatible: true))))
        {
            Assert.Equal(ProviderOutcome.RevisionContentConflict, (await Apply(compatible, updated.Replace("\"revision\":2", "\"revision\":1", StringComparison.Ordinal))).Outcome);
            Assert.Equal(ProviderOutcome.Created, (await Apply(compatible, updated)).Outcome);
            Assert.Equal(ProviderOutcome.ExistingEquivalent, (await Apply(compatible, updated)).Outcome);
            Assert.Equal(ProviderOutcome.ProviderKindConflict, (await Apply(compatible, Json(3, name: "sample"))).Outcome);
        }
    }

    [Fact]
    public async Task Scoped_names_and_bounded_ordinal_pages_include_unnamed_instances_without_fabricating_names()
    {
        var connection = await Database();
        await using var services = Services(connection, sample: true);
        var admin = services.GetRequiredService<ProviderAdministration>();
        var sample = """
            {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"p-000","environment":"pilot"},
            "spec":{"revision":1,"providerKind":"sample/v1","configurationContract":"sample.configuration/v1",
            "implementationId":"sample.impl/v1","properties":{"Target":"sample-target","ImplementationId":"sample.impl/v1"},"secretRefs":{}}}
            """;
        var ids = new List<ProviderInstanceId>();
        for (var n = 0; n < 103; n++)
        {
            var result = await Apply(services, sample.Replace("p-000", $"p-{n:D3}", StringComparison.Ordinal));
            Assert.Equal(ProviderOutcome.Created, result.Outcome);
            ids.Add(result.Provider!.InstanceId);
        }
        var other = await Apply(services, sample.Replace("\"pilot\"", "\"other\"", StringComparison.Ordinal));
        Assert.Equal(ProviderOutcome.Created, other.Outcome);
        Assert.NotEqual(ids[0], other.Provider!.InstanceId);
        var env = (await services.GetRequiredService<IEnvironmentQuery>().FindAsync(EnvironmentId.Create("pilot"), default))!;
        var unnamed = await services.GetRequiredService<IProviderConfigurationStore>().CreateInstanceAsync(env,
            new SampleConfiguration("unnamed", "sample.impl/v1"), default);
        foreach (var size in new[] { 1, 2, 50, 100 })
        {
            string? cursor = null;
            var found = new List<ProviderInstanceId>();
            do
            {
                var page = await admin.ListAsync("pilot", size, cursor, default);
                Assert.Equal(ProviderOutcome.Listed, page.Outcome);
                Assert.InRange(page.Items.Count, 1, size);
                found.AddRange(page.Items.Select(x => x.InstanceId));
                cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.Equal(ids.Append(unnamed), found);
        }
        var first = await admin.ListAsync("pilot", 1, null, default);
        Assert.Equal(ProviderOutcome.InvalidCursor, (await admin.ListAsync("other", 1, first.NextCursor, default)).Outcome);
        Assert.Equal(ProviderOutcome.InvalidCursor, (await admin.ListAsync("pilot", 1, "secret1:bad", default)).Outcome);
        Assert.Equal(ProviderOutcome.InvalidPageSize, (await admin.ListAsync("pilot", 101, null, default)).Outcome);
        Assert.Equal(50, (await admin.ListAsync("pilot", 0, null, default)).Items.Count);
        Assert.Equal(ProviderOutcome.NotFound, (await admin.GetAsync("pilot", "absent", null, default)).Outcome);
        Assert.Equal(ProviderOutcome.NotFound, (await admin.GetAsync("pilot", "p-000", 2, default)).Outcome);
        // Assert the actual database guard independently of the serialized application path.
        await using var duplicate = Context(connection);
        duplicate.ProviderInstances.Add(new ProviderInstanceRow
        {
            Id = Guid.CreateVersion7(),
            EnvironmentRecordId = Guid.Parse(env.ToString()),
            Name = "p-000",
            ProviderKindId = "sample/v1",
            TargetFormatVersion = 1,
            TargetIdentity = "another"
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
    }

    [Fact]
    public async Task Additive_migration_keeps_legacy_unnamed_identity_payload_and_pinned_reads()
    {
        var connection = await Database(migrate: false);
        ProviderInstanceId instance = ProviderInstanceId.New();
        EnvironmentRecordId env;
        string payload;
        await using (var services = Services(connection))
        {
            var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            await using (var migration = await factory.CreateDbContextAsync())
                await migration.GetService<IMigrator>().MigrateAsync("20261029084704_AddNamedAdministrativeSecrets");
            env = (await services.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("pilot"), default)).Environment.RecordId;
            using var material = new SecretMaterial([1]);
            var secret = (await services.GetRequiredService<SecretAdministration>().CreateAsync("pilot", "admin", Guid.NewGuid(), material, "actor", default)).Secret!;
            var exact = new SecretRef(secret.SecretId, secret.Versions.Single().VersionId);
            var prepared = new PostgreSqlProviderConfigurationCodec(new PostgreSqlDatabaseCheckpointCapability()).Encode(
                new PostgreSqlProviderConfiguration(1, "old.invalid", 5432, "postgres", "admin", exact, "haby.postgresql.database/v1alpha1"));
            payload = prepared.PayloadJson;
            // Historical columns only. Today's EF model must not arrange the previous schema.
            await using (var arrange = await factory.CreateDbContextAsync())
            {
                await using var transaction = await arrange.Database.BeginTransactionAsync();
                var id = Guid.Parse(instance.ToString());
                var environment = Guid.Parse(env.ToString());
                var secretId = Guid.Parse(exact.SecretId.ToString());
                var versionId = Guid.Parse(exact.SecretVersionId.ToString());
                await arrange.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO provider_instances (id, environment_record_id, provider_kind_id, target_format_version, target_identity) VALUES ({id}, {environment}, {prepared.ProviderKindId}, {prepared.TargetIdentity.FormatVersion}, {prepared.TargetIdentity.Value})");
                await arrange.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO provider_configuration_revisions (provider_instance_id, revision_number, environment_record_id, implementation_id, configuration_shape_id, payload_format_version, payload_json, is_sealed) VALUES ({id}, 1, {environment}, {prepared.ImplementationId}, {prepared.ShapeId}, {prepared.PayloadFormatVersion}, {payload}, false)");
                await arrange.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO provider_revision_secret_refs (provider_instance_id, revision_number, environment_record_id, slot, secret_id, secret_version_id) VALUES ({id}, 1, {environment}, 'administrator-password', {secretId}, {versionId})");
                await arrange.Database.ExecuteSqlInterpolatedAsync($"UPDATE provider_configuration_revisions SET is_sealed = true WHERE provider_instance_id = {id}");
                await transaction.CommitAsync();
            }
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        }
        await using (var restarted = Services(connection))
        {
            var page = await restarted.GetRequiredService<ProviderAdministration>().ListAsync("pilot", 50, null, default);
            Assert.Equal(instance, Assert.Single(page.Items).InstanceId);
            Assert.Null(page.Items[0].Name);
            var pinned = await restarted.GetRequiredService<IProviderConfigurationStore>().FindRevisionAsync<PostgreSqlProviderConfiguration>(env, instance,
                ProviderConfigurationRevisionNumber.Create(1), default);
            Assert.Equal("old.invalid", pinned!.Configuration.Host);
        }
        await using var check = Context(connection);
        Assert.Null((await check.ProviderInstances.AsNoTracking().SingleAsync()).Name);
        var row = await check.ProviderConfigurationRevisions.AsNoTracking().SingleAsync();
        Assert.Equal(payload, row.PayloadJson);
        Assert.Null(row.ConfirmedPreviousRevision);
    }

    [Fact]
    public async Task Transfer_does_not_follow_alias_or_invalidate_pinned_plans_generations_or_new_revision_planning()
    {
        var connection = await Database();
        await using var services = Services(connection);
        var first = (await Apply(services)).Provider!;
        var id = Guid.Parse(first.InstanceId.ToString());
        await using (var arrange = Context(connection))
        {
            arrange.ProviderAliases.Add(new ProviderAliasRow
            { EnvironmentRecordId = Guid.Parse(first.Environment.ToString()), Alias = "postgresql.primary", ProviderInstanceId = id, RevisionNumber = 1 });
            await arrange.SaveChangesAsync();
        }
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var committer = services.GetRequiredService<IDurableDesiredStateCommitter>();
        var oldChangeSet = await ChangeSet("old-app");
        var oldPlan = (await planner.CreatePlanAsync(oldChangeSet, "actor", default)).Plan!;
        var persistedOldPlan = await planner.FindPlanAsync(oldPlan.Id, default);
        Assert.Equal(ProviderOutcome.Created, (await Apply(services, Json(3, host: "new.invalid"), true, 1)).Outcome);
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(oldPlan.Id, default));
        var accepted = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(oldChangeSet, oldPlan.Id, "actor", default));
        // With alias still at 1, going back to the old endpoint is a transfer relative to highest 3.
        Assert.Equal(ProviderOutcome.IdentityConfirmationRequired, (await Apply(services, Json(4))).Outcome);
        var store = services.GetRequiredService<IProviderConfigurationStore>();
        var transferred = await store.FindRevisionAsync<PostgreSqlProviderConfiguration>(first.Environment,
            first.InstanceId, ProviderConfigurationRevisionNumber.Create(3), default);
        // The compatibility store compares against the new highest endpoint, not the original instance value.
        await store.AddRevisionAsync(first.Environment, first.InstanceId, ProviderConfigurationRevisionNumber.Create(4), transferred!.Configuration, default);
        var required = await Apply(services, Json(5));
        Assert.Equal(ProviderOutcome.IdentityConfirmationRequired, required.Outcome);
        Assert.Equal(4, required.PreviousRevision);
        await using (var arrange = Context(connection))
        {
            var alias = await arrange.ProviderAliases.SingleAsync();
            Assert.Equal(1, alias.RevisionNumber);
            alias.RevisionNumber = 3;
            await arrange.SaveChangesAsync();
        }
        var newPlan = await planner.CreatePlanAsync(await ChangeSet("new-app"), "actor", default);
        Assert.Equal(DurablePlanStatus.Planned, newPlan.Status);
        Assert.Equal(3, newPlan.Plan!.Inputs.ConfigurationRevision.Value);
        var generation = await committer.FindGenerationAsync(accepted.Commit.DeploymentId, accepted.Commit.Generation, default);
        Assert.Equal(oldPlan.Inputs, generation!.Inputs);
        Assert.Equal(1, generation.Inputs.ConfigurationRevision.Value);
        Assert.Equal(persistedOldPlan, await planner.FindPlanAsync(oldPlan.Id, default));
        await using var check = Context(connection);
        var operation = await check.DurableCreateOperations.AsNoTracking().SingleAsync();
        Assert.Equal("Pending", operation.State);
        Assert.Null(operation.DispatchedAt);

        async Task<DeploymentChangeSetId> ChangeSet(string name)
        {
            var bytes = Encoding.UTF8.GetBytes("""
                {"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"APP"},
                "spec":{"resources":{"db":{"type":"postgresql.database/v1alpha1","providerRef":"postgresql.primary","properties":{"name":"sample_db"}}}}}}
                """.Replace("APP", name, StringComparison.Ordinal));
            var catalog = new ManifestContractCatalog([PostgreSqlDatabaseContract.Definition], [], [], []);
            var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                services.GetRequiredService<IManifestImportStore>(), catalog, TimeProvider.System).ExecuteAsync(bytes, "actor"));
            return (await planner.CreateChangeSetAsync(first.Environment, imported.ManifestRevisionRef.ApplicationRecordId,
                imported.ManifestRevisionRef, new DeploymentPrecondition.ExpectedAbsence(), "actor", default)).ChangeSet!.Id;
        }
    }

    internal static string Json(int revision, string host = "unreachable.invalid", string database = "postgres", string name = "pg-main") =>
        Declaration.Replace("\"revision\":1", "\"revision\":" + revision, StringComparison.Ordinal)
            .Replace("unreachable.invalid", host, StringComparison.Ordinal).Replace("\"postgres\"", "\"" + database + "\"", StringComparison.Ordinal)
            .Replace("pg-main", name, StringComparison.Ordinal);
    private static ValueTask<ProviderResult> Apply(ServiceProvider services, string? json = null, bool confirm = false, long? previous = null) =>
        services.GetRequiredService<ProviderAdministration>().ApplyAsync(Encoding.UTF8.GetBytes(json ?? Declaration), null, confirm, previous, "actor", default);

    private async Task<string> Database(bool migrate = true)
    {
        var name = "haby_declarations_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        // Bound physical connections during intensive keyset/race tests. DbContexts and units of
        // work remain independent; sharing a pool does not share tracking or transactions.
        var result = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        { Database = name, Pooling = true, MaxPoolSize = 16 }.ConnectionString;
        if (!migrate) return result;
        await using var services = Services(result);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await services.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("pilot"), default);
        await services.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("other"), default);
        using var material = new SecretMaterial([1, 2, 3]);
        await services.GetRequiredService<SecretAdministration>().CreateAsync("pilot", "admin", Guid.NewGuid(), material, "actor", default);
        return result;
    }
    private static ServiceProvider Services(string connection, bool sample = false, IInterceptor? intercept = null, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection().AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore()
            .AddPostgreSqlProviderConfigurationStore().AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>()
            .AddPostgreSqlProviderConfigurationCodec().AddSingleton(TimeProvider.System)
            .AddSingleton<IProviderAliasResolver, PostgreSqlProviderAliasResolver>().AddPostgreSqlDurableDesiredStateCommitter();
        if (sample) services.AddSingleton<IProviderConfigurationCodec, SampleConfigurationCodec>().AddSingleton<IProviderDeclarationAdapter, SampleDeclarationAdapter>();
        if (intercept is not null) services.AddDbContextFactory<ManifestCatalogDbContext>(o => o.UseNpgsql(connection).AddInterceptors(intercept));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
    private static ManifestCatalogDbContext Context(string connection) => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
    private sealed class NoSecretMaterial : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command,
            CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Assert.DoesNotContain("secret_payloads", command.CommandText, StringComparison.OrdinalIgnoreCase);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class RejectSealing : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE provider_configuration_revisions", StringComparison.Ordinal))
                throw new NpgsqlException("Test-only seal failure.");
            return ValueTask.FromResult(result);
        }
    }
}

internal sealed class SampleDeclarationAdapter(string implementation = "sample.impl/v1", bool compatible = false) : IProviderDeclarationAdapter
{
    public ProviderAdapterKey Key { get; } = new("sample/v1", "sample.configuration/v1", implementation);
    public IProviderConfigurationCodec Codec { get; } = new SampleConfigurationCodec();
    public IReadOnlyList<string> RequiredSecretSlots => [];
    public object ParseProperties(JsonElement properties) => properties.Deserialize(SampleConfigurationJsonContext.Default.SampleConfiguration)
        ?? throw new ArgumentException("Invalid sample.");
    public object Bind(object properties, IReadOnlyDictionary<string, SecretRef> secrets) => properties;
    public bool CanReplace(ProviderAdapterKey previous) => previous == Key || (compatible && previous == new ProviderAdapterKey("sample/v1", "sample.configuration/v1", "sample.impl/v1"));
    public bool Equivalent(object previous, object candidate) => Equals(previous, candidate);
    public bool SameEndpoint(object previous, object candidate) => ((SampleConfiguration)previous).Target == ((SampleConfiguration)candidate).Target;
}
