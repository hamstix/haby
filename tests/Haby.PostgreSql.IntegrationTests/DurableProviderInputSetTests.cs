using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Fact]
    public async Task Singleton_catalog_refuses_startup_and_direct_migration_without_reinterpreting_or_resetting_history()
    {
        var setup = await PrepareCommitAsync();
        DeploymentChangeSetId change;
        await using (var arrange = Services(setup.Connection, setup.Time))
        {
            change = (await arrange.GetRequiredService<IDurableDeploymentPlanner>().CreateChangeSetAsync(setup.Environment,
                setup.Revision.ApplicationRecordId, setup.Revision, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!.Id;
            await using var db = await arrange.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            // Only empty durable history crosses Down. Populate the actual previous schema
            // afterward to prove both startup and direct migration guard the format boundary.
            await db.GetService<IMigrator>().MigrateAsync("20261111200023_AddPlacementSelections");
        }
        await using (var arrange = new NpgsqlConnection(setup.Connection))
        {
            await arrange.OpenAsync();
            await using var insert = new NpgsqlCommand("""
                INSERT INTO deployment_plans (id, change_set_id, environment_record_id, environment_key, application_record_id,
                    revision_number, provider_alias, provider_instance_id, configuration_revision_number, implementation_id,
                    snapshot_format_version, fingerprint, graph_json, configuration_json, naming_json, created_at, expires_at, actor_id)
                VALUES (@id, @change, @environment, 'pilot', @application, @revision, 'postgresql.primary', @provider, 1, @implementation,
                    3, @fingerprint, '{}', '{}', '{}', now(), now() + interval '1 hour', 'old-format-fixture')
                """, arrange);
            insert.Parameters.AddWithValue("id", Guid.Parse(DeploymentPlanId.New().ToString()));
            insert.Parameters.AddWithValue("change", Guid.Parse(change.ToString()));
            insert.Parameters.AddWithValue("environment", Guid.Parse(setup.Environment.ToString()));
            insert.Parameters.AddWithValue("application", Guid.Parse(setup.Revision.ApplicationRecordId.ToString()));
            insert.Parameters.AddWithValue("revision", setup.Revision.RevisionNumber.Value);
            insert.Parameters.AddWithValue("provider", Guid.Parse(setup.First.InstanceId.ToString()));
            insert.Parameters.AddWithValue("implementation", Implementation);
            insert.Parameters.AddWithValue("fingerprint", "v3:" + new string('A', 64));
            await insert.ExecuteNonQueryAsync();
        }
        await using (var act = Services(setup.Connection, setup.Time))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => act.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync());
            Assert.Contains("fresh catalog", error.Message);
            await using var db = await act.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            var direct = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, direct.SqlState);
        }
        await using var verify = new NpgsqlConnection(setup.Connection);
        await verify.OpenAsync();
        Assert.Equal(28L, await new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\"", verify).ExecuteScalarAsync());
        Assert.Equal(1L, await new NpgsqlCommand("SELECT count(*) FROM deployment_plans", verify).ExecuteScalarAsync());
        Assert.Equal(0L, await new NpgsqlCommand("SELECT count(*) FROM application_deployment_generations", verify).ExecuteScalarAsync());
        Assert.Equal(0L, await new NpgsqlCommand("SELECT count(*) FROM information_schema.columns WHERE table_name = 'deployment_plans' AND column_name = 'provider_inputs_json'", verify).ExecuteScalarAsync());
        Assert.Equal(1L, await new NpgsqlCommand("SELECT count(*) FROM information_schema.columns WHERE table_name = 'deployment_plans' AND column_name = 'provider_instance_id'", verify).ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("snapshot-version")]
    [InlineData("instance")]
    [InlineData("graph-owner")]
    public async Task Corrupt_committed_provider_inputs_refuse_before_dispatch_or_attempt_creation(string fault)
    {
        var setup = await PrepareCommitAsync();
        DurableChangeSetCommit receipt;
        await using (var arrange = Services(setup.Connection, setup.Time))
        {
            var planner = arrange.GetRequiredService<IDurableDeploymentPlanner>();
            var change = (await planner.CreateChangeSetAsync(setup.Environment, setup.Revision.ApplicationRecordId, setup.Revision,
                new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
            var plan = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
            receipt = Assert.IsType<DurableCommitResult.Committed>(await arrange.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(change.Id, plan.Id, "tester", default)).Commit;
        }
        await using (var arrange = new NpgsqlConnection(setup.Connection))
        {
            await arrange.OpenAsync();
            await new NpgsqlCommand("SET session_replication_role = replica", arrange).ExecuteNonQueryAsync();
            var sql = fault switch
            {
                "snapshot-version" => "UPDATE application_deployment_generations SET snapshot_format_version = 3",
                "instance" => "UPDATE application_deployment_generations SET provider_inputs_json = jsonb_set(provider_inputs_json::jsonb, '{Entries,0,Instance}', to_jsonb(@instance::text))::text",
                _ => "UPDATE application_deployment_generations SET graph_json = replace(graph_json, '\"Resource\":\"db\"', '\"Resource\":\"other\"')"
            };
            await using var update = new NpgsqlCommand(sql, arrange);
            if (fault == "instance") update.Parameters.AddWithValue("instance", ProviderInstanceId.New().ToString());
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        await using (var act = Services(setup.Connection, setup.Time))
        {
            var key = new DurableOperationKey(receipt.DeploymentId, receipt.Generation, OperationId.Create("db-create"));
            await Assert.ThrowsAsync<InvalidDataException>(async () => await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(key, default));
            await Assert.ThrowsAsync<InvalidDataException>(async () => await act.GetRequiredService<IDurableDesiredStateCommitter>().FindGenerationAsync(receipt.DeploymentId, receipt.Generation, default));
        }
        await using var verifyServices = Services(setup.Connection, setup.Time);
        await using var verify = await verifyServices.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(nameof(DurableOperationState.Pending), (await verify.DurableOperations.AsNoTracking().SingleAsync()).State);
        Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("environment")]
    [InlineData("alias")]
    [InlineData("instance")]
    [InlineData("revision")]
    [InlineData("implementation")]
    [InlineData("set-version")]
    [InlineData("naming-version")]
    [InlineData("snapshot-version")]
    [InlineData("key-kind")]
    [InlineData("key-id")]
    [InlineData("key-component")]
    [InlineData("naming-payload")]
    public async Task Corrupt_durable_provider_set_refuses_read_check_commit_and_accepts_no_desired_work(string fault)
    {
        var setup = await PrepareCommitAsync();
        DurablePlan original;
        await using (var arrange = Services(setup.Connection, setup.Time))
        {
            var planner = arrange.GetRequiredService<IDurableDeploymentPlanner>();
            var change = (await planner.CreateChangeSetAsync(setup.Environment, setup.Revision.ApplicationRecordId, setup.Revision,
                new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
            original = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
        }
        var document = JsonNode.Parse(ExactProviderResolutionSetCodec.Encode(original.Inputs.Providers))!;
        var entries = document["Entries"]!.AsArray();
        switch (fault)
        {
            case "missing": entries.Clear(); break;
            case "extra": var extra = entries[0]!.DeepClone(); extra["Id"] = "extra"; entries.Add(extra); break;
            case "duplicate": entries.Add(entries[0]!.DeepClone()); break;
            case "environment": entries[0]!["Environment"] = EnvironmentRecordId.New().ToString(); break;
            case "alias": entries[0]!["Alias"] = "different"; break;
            case "instance": entries[0]!["Instance"] = ProviderInstanceId.New().ToString(); break;
            case "revision": entries[0]!["Configuration"] = 99; break;
            case "implementation": entries[0]!["Implementation"] = "unavailable/v2"; break;
            case "set-version": document["Version"] = 99; break;
            case "naming-version": entries[0]!["Naming"]!["Contract"] = "haby.resource-naming/v2"; break;
            case "key-kind": entries[0]!["Kind"] = "display-name"; break;
            case "key-id": entries[0]!["Id"] = "undeclared"; break;
            case "key-component": entries[0]!["Component"] = "unrelated"; break;
            case "naming-payload": entries[0]!["Naming"]!["Json"] = "{\"snapshot\":\"{}\"}"; break;
        }
        // Adversarial corruption only in this disposable catalog. Production immutability
        // remains enabled; no application connection receives an exception to its guards.
        await using (var corrupt = new NpgsqlConnection(setup.Connection))
        {
            await corrupt.OpenAsync();
            await new NpgsqlCommand("SET session_replication_role = replica", corrupt).ExecuteNonQueryAsync();
            await using var update = new NpgsqlCommand("UPDATE deployment_plans SET provider_inputs_json = @json, snapshot_format_version = @version WHERE id = @id", corrupt);
            update.Parameters.AddWithValue("json", document.ToJsonString());
            update.Parameters.AddWithValue("version", fault == "snapshot-version" ? 3 : DurableInputStorage.Version);
            update.Parameters.AddWithValue("id", Guid.Parse(original.Id.ToString()));
            await update.ExecuteNonQueryAsync();
        }
        await using (var act = Services(setup.Connection, setup.Time))
        {
            var planner = act.GetRequiredService<IDurableDeploymentPlanner>();
            await Assert.ThrowsAsync<InvalidDataException>(async () => await planner.FindPlanAsync(original.Id, default));
            Assert.Equal(DurablePlanCurrentStatus.PlanInputUnavailable, await planner.CheckPlanAsync(original.Id, default));
            Assert.IsType<DurableCommitResult.PlanInputUnavailable>(await act.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(original.ChangeSetId, original.Id, "tester", default));
        }
        await using var verifyServices = Services(setup.Connection, setup.Time);
        await using var verify = await verifyServices.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verify.ApplicationDeployments.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.DurableOperations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Test_only_multi_alias_plan_storage_roundtrips_all_keys_after_restart_without_enabling_admission()
    {
        var setup = await PrepareCommitAsync();
        var envelope = JsonNode.Parse(Envelope)!.AsObject();
        envelope["applicationVersion"] = "multi-input-storage";
        var resources = envelope["manifest"]!["spec"]!["resources"]!.AsObject();
        resources["other"] = resources["db"]!.DeepClone();
        resources["other"]!["providerRef"] = "postgresql.secondary";
        resources["other"]!["properties"]!["name"] = "other_db";
        DurablePlan saved;
        ProviderAliasBinding secondary;
        await using (var arrange = Services(setup.Connection, setup.Time))
        {
            var providers = arrange.GetRequiredService<IProviderConfigurationStore>();
            var original = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(setup.Environment, setup.First.InstanceId, setup.Second.RevisionNumber, default))!.Configuration;
            var instance = await providers.CreateInstanceAsync(setup.Environment, original, default);
            await providers.AddRevisionAsync(setup.Environment, instance, ProviderConfigurationRevisionNumber.Create(1), original, default);
            secondary = new(instance, ProviderConfigurationRevisionNumber.Create(1));
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await arrange.GetRequiredService<IProviderAliasStore>().ChangeAsync(setup.Environment,
                ProviderAlias.Create("postgresql.secondary"), null, secondary, "tester", default)).Outcome);
            var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                arrange.GetRequiredService<IManifestImportStore>(), Catalog, setup.Time).ExecuteAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), "tester"));
            var revision = (await arrange.GetRequiredService<IManifestRevisionQuery>().FindAsync(imported.ManifestRevisionRef))!;
            var change = (await arrange.GetRequiredService<IDurableDeploymentPlanner>().CreateChangeSetAsync(setup.Environment,
                revision.Reference.ApplicationRecordId, revision.Reference, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
            Assert.Equal(DurablePlanStatus.UnsupportedShape, (await arrange.GetRequiredService<IDurableDeploymentPlanner>().CreatePlanAsync(change.Id, "tester", default)).Status);
            var entries = revision.Declaration.Spec.Resources.Select(r =>
            {
                var binding = r.Key.Value == "db" ? setup.First : secondary;
                return new ExactProviderResolution(new ProviderInput.Resource(r.Key), setup.Environment, r.Value.ProviderRef,
                    binding.InstanceId, binding.RevisionNumber, ProviderImplementationId.Create(Implementation), ResourceNamingCodec.Exact(ResourceNamingSnapshot.Default));
            }).ToArray();
            var configuration = ConfigurationPlanningSnapshot.Prepare(revision.Reference, revision.Declaration,
                ConfigurationPlanningSnapshot.Targets(revision.Declaration).Select(t => new ConfigurationPlanningScope(t, null, new(t, []))), []).Snapshot!;
            var inputs = new DurablePlanningInputs(setup.Environment, EnvironmentId.Create("pilot"), revision.Reference,
                new(revision.Declaration, setup.Environment, entries))
            { Configuration = configuration };
            var nodes = entries.Select(x => new DurableOperationNode(OperationId.Create(((ProviderInput.Resource)x.Input).Id.Value + "-create"),
                ((ProviderInput.Resource)x.Input).Id, Haby.PostgreSql.PostgreSqlDatabaseCreateAction.RegistrationKey,
                new(x.Environment, x.Instance, x.Configuration, x.Implementation.Value), new("storage-only/v1", "{}"), [])).ToArray();
            var graph = new DurableOperationGraph(nodes);
            saved = new(DeploymentPlanId.New(), change.Id, inputs, DurableOperationGraphCodec.Fingerprint(inputs, graph),
                setup.Time.GetUtcNow(), setup.Time.GetUtcNow().AddHours(24), "storage-test", null, null)
            { Graph = graph };
            // Narrow storage seam: insert a Plan only. No alternate admission, executor,
            // generation, operation, external Provider effect or fake Kubernetes Provider.
            await using var db = await arrange.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            db.DurablePlans.Add(new DurablePlanRow
            {
                Id = Guid.Parse(saved.Id.ToString()),
                ChangeSetId = Guid.Parse(change.Id.ToString()),
                EnvironmentRecordId = Guid.Parse(setup.Environment.ToString()),
                EnvironmentKey = "pilot",
                ApplicationRecordId = Guid.Parse(revision.Reference.ApplicationRecordId.ToString()),
                RevisionNumber = revision.Reference.RevisionNumber.Value,
                SnapshotFormatVersion = DurableInputStorage.Version,
                ProviderInputsJson = ExactProviderResolutionSetCodec.Encode(inputs.Providers),
                ConfigurationJson = ConfigurationPlanningSnapshotCodec.Encode(configuration),
                GraphJson = DurableOperationGraphCodec.Encode(graph),
                Fingerprint = saved.Fingerprint,
                CreatedAt = saved.CreatedAt,
                ExpiresAt = saved.ExpiresAt,
                ActorId = saved.ActorId
            });
            await db.SaveChangesAsync();
        }
        await using (var act = Services(setup.Connection, setup.Time))
        {
            var planner = act.GetRequiredService<IDurableDeploymentPlanner>();
            var read = await planner.FindPlanAsync(saved.Id, default);
            Assert.NotNull(read);
            Assert.Equal(saved.Inputs, read.Inputs);
            Assert.Equal(setup.First.RevisionNumber, read.Inputs.Providers.Resource(ManifestLocalId.Create("db")).Configuration);
            Assert.Equal(secondary.InstanceId, read.Inputs.Providers.Resource(ManifestLocalId.Create("other")).Instance);
            Assert.Equal(secondary.RevisionNumber, read.Inputs.Providers.Resource(ManifestLocalId.Create("other")).Configuration);
            Assert.Throws<InvalidDataException>(() => read.Inputs.Providers.RequireUniformResourceProvider());
            var description = new DescribeDurablePlan(act.GetRequiredService<IManifestRevisionQuery>(), [new PostgreSqlPlanActionDescriber()]);
            Assert.Equal(PlanDescriptionStatus.OperationGraph, (await description.ExecuteAsync(read, default)).Status);
            Assert.Equal(2, read.Graph.Nodes.Count);
            Assert.Equal(DurablePlanCurrentStatus.PlanInputUnavailable, await planner.CheckPlanAsync(saved.Id, default));
            Assert.IsType<DurableCommitResult.PlanInputUnavailable>(await act.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(saved.ChangeSetId, saved.Id, "tester", default));
        }
        await using var verifyServices = Services(setup.Connection, setup.Time);
        await using var verify = await verifyServices.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Single(await verify.DurablePlans.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.DurableOperations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.ResourceManagement.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
    }
}
