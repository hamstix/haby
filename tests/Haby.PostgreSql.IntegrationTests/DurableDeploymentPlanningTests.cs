using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class DurableDeploymentPlanningTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Implementation = PostgreSqlDatabaseCheckpointCapability.ImplementationId;
    private const string AnyObject = """{"type":"object"}""";
    private static readonly ManifestContractCatalog Catalog = new(
        [new(ResourceTypeId.Create("postgresql.database/v1alpha1"), AnyObject, AnyObject)],
        [], [], []);
    private static readonly byte[] Envelope = Encoding.UTF8.GetBytes(
        """{"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"operator-example"},"spec":{"resources":{"db":{"type":"postgresql.database/v1alpha1","providerRef":"postgresql.primary","properties":{"name":"sample_db"}}}}}}""");

    [Fact]
    public async Task Commit_is_atomic_restartable_and_returns_original_plan_on_retry()
    {
        var setup = await PrepareCommitAsync();
        var (connection, time, environment, revision, firstBinding, secondBinding, providerPassword) = setup;
        DeploymentChangeSetId changeSetId;
        DurablePlan first;
        DurablePlan alternative;
        DurablePlan retryPlan;
        DurableChangeSetCommit original;
        await using (var services = Services(connection, time))
        {
            var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
            var committer = services.GetRequiredService<IDurableDesiredStateCommitter>();
            changeSetId = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId,
                revision, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!.Id;
            first = (await planner.CreatePlanAsync(changeSetId, "tester", default)).Plan!;
            alternative = (await planner.CreatePlanAsync(changeSetId, "tester", default)).Plan!;
            var competing = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId,
                revision, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!.Id;
            var competingPlan = (await planner.CreatePlanAsync(competing, "tester", default)).Plan!;
            var competingAlternative = (await planner.CreatePlanAsync(competing, "tester", default)).Plan!;
            Assert.IsType<DurableCommitResult.PlanDoesNotBelongToChangeSet>(
                await committer.CommitAsync(changeSetId, competingPlan.Id, "tester", default));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await committer.CommitAsync(changeSetId, first.Id, "tester", canceled.Token));
            var results = await Task.WhenAll(
                committer.CommitAsync(changeSetId, first.Id, "winner", default).AsTask(),
                committer.CommitAsync(competing, competingPlan.Id, "competitor", default).AsTask());
            Assert.Single(results.OfType<DurableCommitResult.Committed>());
            var stale = Assert.Single(results.OfType<DurableCommitResult.ChangeSetStale>());
            Assert.Equal(DeploymentGeneration.Create(1), stale.ActualGeneration);
            var firstWon = results[0] is DurableCommitResult.Committed;
            var winner = Assert.IsType<DurableCommitResult.Committed>(results[firstWon ? 0 : 1]);
            changeSetId = firstWon ? changeSetId : competing;
            var winningPlan = firstWon ? first : competingPlan;
            retryPlan = firstWon ? alternative : competingAlternative;
            original = winner.Commit;
            Assert.Equal(winningPlan.Id, original.PlanId);
            Assert.Equal(original, Assert.IsType<DurableCommitResult.Committed>(
                await committer.CommitAsync(changeSetId, retryPlan.Id, "retry", default)).Commit);
            Assert.Equal(DeploymentGeneration.Create(1), original.Generation);
            Assert.Equal(winningPlan.Inputs, (await committer.FindGenerationAsync(original.DeploymentId,
                original.Generation, default))!.Inputs);
            Assert.Equal(DesiredPresence.Present, (await committer.FindGenerationAsync(original.DeploymentId,
                original.Generation, default))!.Presence);
            Assert.Equal(ProviderAliasChangeOutcome.Changed,
                (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment,
                    ProviderAlias.Create("postgresql.primary"), firstBinding, secondBinding,
                    "tester", default)).Outcome);
            Assert.Equal(winningPlan.Inputs, (await committer.FindGenerationAsync(original.DeploymentId,
                original.Generation, default))!.Inputs);
        }
        await using (var restarted = Services(connection, time))
        {
            var committer = restarted.GetRequiredService<IDurableDesiredStateCommitter>();
            Assert.Equal(original, await committer.FindCommitAsync(changeSetId, default));
            Assert.Equal(firstBinding.RevisionNumber, (await committer.FindGenerationAsync(original.DeploymentId,
                original.Generation, default))!.Inputs.ConfigurationRevision);
            Assert.Equal(original, Assert.IsType<DurableCommitResult.Committed>(
                await committer.CommitAsync(changeSetId, retryPlan.Id, "after-restart", default)).Commit);
        }
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        Assert.Equal(1L, (long)(await new NpgsqlCommand("SELECT count(*) FROM application_deployments", database).ExecuteScalarAsync())!);
        Assert.Equal(1L, (long)(await new NpgsqlCommand("SELECT count(*) FROM application_deployment_generations", database).ExecuteScalarAsync())!);
        Assert.Equal(1L, (long)(await new NpgsqlCommand("SELECT count(*) FROM change_set_commits", database).ExecuteScalarAsync())!);
        Assert.Equal(1L, (long)(await new NpgsqlCommand("SELECT desired_generation FROM application_deployments", database).ExecuteScalarAsync())!);
        Assert.Equal(DBNull.Value, await new NpgsqlCommand("SELECT applied_generation FROM application_deployments", database).ExecuteScalarAsync());
        Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = 'sample_db'", database).ExecuteScalarAsync())!);
        await AssertImmutableRejected(database,
            "UPDATE application_deployment_generations SET actor_id = 'replacement' WHERE deployment_id = @id",
            Guid.Parse(original.DeploymentId.ToString()));
        await AssertImmutableRejected(database,
            "DELETE FROM application_deployment_generations WHERE deployment_id = @id",
            Guid.Parse(original.DeploymentId.ToString()));
        await AssertImmutableRejected(database,
            "UPDATE change_set_commits SET actor_id = 'replacement' WHERE change_set_id = @id",
            Guid.Parse(changeSetId.ToString()));
        await AssertImmutableRejected(database,
            "DELETE FROM change_set_commits WHERE change_set_id = @id",
            Guid.Parse(changeSetId.ToString()));
        Assert.Equal(0L, (long)(await new NpgsqlCommand("""
            SELECT count(*) FROM information_schema.columns
            WHERE table_name IN ('application_deployments', 'application_deployment_generations', 'change_set_commits')
              AND column_name ~ '(secret|password|payload|credential)'
            """, database).ExecuteScalarAsync())!);
        await using var redaction = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM application_deployments AS t WHERE to_jsonb(t)::text LIKE '%' || @secret || '%')
                 + (SELECT count(*) FROM application_deployment_generations AS t WHERE to_jsonb(t)::text LIKE '%' || @secret || '%')
                 + (SELECT count(*) FROM change_set_commits AS t WHERE to_jsonb(t)::text LIKE '%' || @secret || '%')
            """, database);
        redaction.Parameters.AddWithValue("secret", providerPassword);
        Assert.Equal(0L, (long)(await redaction.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Alias_update_ordered_before_commit_makes_plan_stale_without_partial_rows()
    {
        var (connection, time, environment, revision, _, secondBinding, _) = await PrepareCommitAsync();
        await using var services = Services(connection, time);
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var committer = services.GetRequiredService<IDurableDesiredStateCommitter>();
        var changeSetId = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId,
            revision, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!.Id;
        var oldPlan = (await planner.CreatePlanAsync(changeSetId, "tester", default)).Plan!;
        await using (var blocker = new NpgsqlConnection(connection))
        {
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var update = new NpgsqlCommand("""
                UPDATE provider_aliases SET revision_number = @revision
                WHERE environment_record_id = @environment AND alias = 'postgresql.primary'
                """, blocker, transaction))
            {
                update.Parameters.AddWithValue("revision", secondBinding.RevisionNumber.Value);
                update.Parameters.AddWithValue("environment", Guid.Parse(environment.ToString()));
                Assert.Equal(1, await update.ExecuteNonQueryAsync());
            }
            var pending = committer.CommitAsync(changeSetId, oldPlan.Id, "tester", default).AsTask();
            await Task.Delay(100);
            Assert.False(pending.IsCompleted);
            await transaction.CommitAsync();
            Assert.IsType<DurableCommitResult.PlanStale>(await pending);
        }
        await using (var database = new NpgsqlConnection(connection))
        {
            await database.OpenAsync();
            Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM application_deployments", database).ExecuteScalarAsync())!);
            Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM application_deployment_generations", database).ExecuteScalarAsync())!);
            Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM change_set_commits", database).ExecuteScalarAsync())!);
        }
        var newPlan = (await planner.CreatePlanAsync(changeSetId, "tester", default)).Plan!;
        Assert.Equal(secondBinding.RevisionNumber, newPlan.Inputs.ConfigurationRevision);
        Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(changeSetId, newPlan.Id, "tester", default));
    }

    [Fact]
    public async Task Alias_switch_reports_unavailable_while_commit_style_share_lock_is_held()
    {
        var (connection, time, environment, revision, firstBinding, secondBinding, _) = await PrepareCommitAsync();
        await using var services = Services(connection, time);
        var aliasStore = services.GetRequiredService<IProviderAliasStore>();
        var alias = ProviderAlias.Create("postgresql.primary");
        await using (var blocker = new NpgsqlConnection(connection))
        {
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var guard = new NpgsqlCommand("""
                SELECT environment_record_id FROM provider_aliases
                WHERE environment_record_id = @environment AND alias = 'postgresql.primary' FOR SHARE
                """, blocker, transaction))
            {
                guard.Parameters.AddWithValue("environment", Guid.Parse(environment.ToString()));
                Assert.NotNull(await guard.ExecuteScalarAsync());
            }
            Assert.Equal(ProviderAliasChangeOutcome.StoreUnavailable,
                (await aliasStore.ChangeAsync(environment, alias, firstBinding, secondBinding,
                    "tester", default)).Outcome);
            await transaction.CommitAsync();
        }
        Assert.Equal(firstBinding, (await services.GetRequiredService<IProviderAliasResolver>()
            .ResolveAsync(environment, alias, default))!.Binding);
        Assert.Equal(ProviderAliasChangeOutcome.Changed,
            (await aliasStore.ChangeAsync(environment, alias, firstBinding, secondBinding,
                "tester", default)).Outcome);
    }

    [Fact]
    public async Task Closed_expired_and_unavailable_inputs_fail_without_committing()
    {
        var (connection, time, environment, revision, firstBinding, secondBinding, _) = await PrepareCommitAsync();
        await using var services = Services(connection, time);
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var committer = services.GetRequiredService<IDurableDesiredStateCommitter>();
        var changeSetId = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId,
            revision, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!.Id;
        var closed = (await planner.CreatePlanAsync(changeSetId, "tester", default)).Plan!;
        Assert.Equal(DurablePlanCloseStatus.Closed, await planner.ClosePlanAsync(closed.Id, "tester", default));
        Assert.IsType<DurableCommitResult.Closed>(await committer.CommitAsync(changeSetId, closed.Id,
            "tester", default));
        var expired = (await planner.CreatePlanAsync(changeSetId, "tester", default)).Plan!;
        time.Advance(TimeSpan.FromHours(24));
        Assert.IsType<DurableCommitResult.Expired>(await committer.CommitAsync(changeSetId, expired.Id,
            "tester", default));
        var unavailable = (await planner.CreatePlanAsync(changeSetId, "tester", default)).Plan!;
        await using (var noCodec = Services(connection, time, registerCodec: false))
            Assert.IsType<DurableCommitResult.PlanInputUnavailable>(await noCodec
                .GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(changeSetId, unavailable.Id, "tester", default));
        await using (var database = new NpgsqlConnection(connection))
        {
            await database.OpenAsync();
            Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM application_deployments", database).ExecuteScalarAsync())!);
            Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM application_deployment_generations", database).ExecuteScalarAsync())!);
            Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM change_set_commits", database).ExecuteScalarAsync())!);
        }
        Assert.Equal(ProviderAliasChangeOutcome.Changed,
            (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment,
                ProviderAlias.Create("postgresql.primary"), firstBinding, secondBinding,
                "tester", default)).Outcome);
        Assert.IsType<DurableCommitResult.PlanStale>(await committer.CommitAsync(changeSetId,
            unavailable.Id, "tester", default));
        var replacement = (await planner.CreatePlanAsync(changeSetId, "tester", default)).Plan!;
        Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(changeSetId,
            replacement.Id, "tester", default));
        Assert.Equal(DurablePlanStatus.ChangeSetStale,
            (await planner.CreatePlanAsync(changeSetId, "tester", default)).Status);
    }

    [Fact]
    public async Task Cancellation_after_save_rolls_back_all_desired_state_rows()
    {
        var (connection, time, environment, revision, _, _, _) = await PrepareCommitAsync();
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelAfterCommitRowsSaved(cancellation);
        await using var services = Services(connection, time, saveInterceptor: interceptor);
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var changeSet = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId,
            revision, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
        var plan = (await planner.CreatePlanAsync(changeSet.Id, "tester", default)).Plan!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await services.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(changeSet.Id, plan.Id, "tester", cancellation.Token));
        Assert.True(interceptor.Triggered);
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM application_deployments", database).ExecuteScalarAsync())!);
        Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM application_deployment_generations", database).ExecuteScalarAsync())!);
        Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM change_set_commits", database).ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Restart_alias_switch_replan_close_and_expiry_preserve_immutable_secret_free_plans()
    {
        var connection = await NewDatabaseAsync();
        var time = new AdjustableTimeProvider();
        var alias = ProviderAlias.Create("postgresql.primary");
        EnvironmentRecordId environment;
        ManifestRevisionRef revision;
        DeploymentChangeSetId changeSetId;
        DurablePlan first;
        ProviderAliasBinding oldBinding;
        ProviderAliasBinding newBinding;
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var providerPassword = "haby-test-" + Guid.NewGuid().ToString("N");
        var providerUser = "haby_plan_" + Guid.NewGuid().ToString("N");
        await using (var adminDatabase = new NpgsqlConnection(fixture.ConnectionString))
        {
            await adminDatabase.OpenAsync();
            await new NpgsqlCommand($"CREATE ROLE \"{providerUser}\" LOGIN CREATEDB PASSWORD '{providerPassword}'", adminDatabase)
                .ExecuteNonQueryAsync();
        }
        var target = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        { Username = providerUser, Password = providerPassword };
        await using (var services = Services(connection, time))
        {
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var secrets = services.GetRequiredService<ISecretStore>();
            environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
            var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
            await secrets.CreateAsync(environment, secret, Encoding.UTF8.GetBytes(providerPassword), default);
            var provider = services.GetRequiredService<IProviderConfigurationStore>();
            var configuration = new PostgreSqlProviderConfiguration(1, target.Host!, target.Port,
                target.Database!, target.Username!, secret, Implementation);
            var instance = await provider.CreateInstanceAsync(environment, configuration, default);
            await provider.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1),
                configuration, default);
            await provider.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(2),
                configuration, default);
            oldBinding = new(instance, ProviderConfigurationRevisionNumber.Create(1));
            newBinding = new(instance, ProviderConfigurationRevisionNumber.Create(2));
            var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                services.GetRequiredService<IManifestImportStore>(), Catalog, time).ExecuteAsync(Envelope, "tester"));
            revision = imported.ManifestRevisionRef;
            var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
            var intent = await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, revision,
                new DeploymentPrecondition.ExpectedAbsence(), "tester", default);
            changeSetId = Assert.IsType<DeploymentChangeSet>(intent.ChangeSet).Id;
            Assert.Equal(DurableChangeSetCreateStatus.Created, intent.Status);
            var otherEnvironment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("other"), default);
            var otherSecret = new SecretRef(SecretId.New(), SecretVersionId.New());
            await secrets.CreateAsync(otherEnvironment, otherSecret, Encoding.UTF8.GetBytes(providerPassword), default);
            var otherConfiguration = new PostgreSqlProviderConfiguration(1, target.Host!, target.Port,
                target.Database!, target.Username!, otherSecret, Implementation);
            var otherInstance = await provider.CreateInstanceAsync(otherEnvironment, otherConfiguration, default);
            await provider.AddRevisionAsync(otherEnvironment, otherInstance,
                ProviderConfigurationRevisionNumber.Create(1), otherConfiguration, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed,
                (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(otherEnvironment, alias, null,
                    new ProviderAliasBinding(otherInstance, ProviderConfigurationRevisionNumber.Create(1)),
                    "tester", default)).Outcome);
            Assert.Equal(DurablePlanStatus.AliasNotResolved,
                (await planner.CreatePlanAsync(changeSetId, "tester", default)).Status);
            Assert.Equal(ProviderAliasChangeOutcome.Changed,
                (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment, alias, null,
                    oldBinding, "tester", default)).Outcome);
            first = Assert.IsType<DurablePlan>((await planner.CreatePlanAsync(changeSetId, "tester", default)).Plan);
            Assert.Equal(time.GetUtcNow().AddHours(24), first.ExpiresAt);
            Assert.Equal(oldBinding.InstanceId, first.Inputs.ProviderInstance);
            Assert.Equal(oldBinding.RevisionNumber, first.Inputs.ConfigurationRevision);
            Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(first.Id, default));
        }
        await using (var restarted = Services(connection, time))
        {
            var planner = restarted.GetRequiredService<IDurableDeploymentPlanner>();
            Assert.Equal(first, await planner.FindPlanAsync(first.Id, default));
            Assert.NotNull(await planner.FindChangeSetAsync(changeSetId, default));
            Assert.Equal(ProviderAliasChangeOutcome.Changed,
                (await restarted.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment, alias,
                    oldBinding, newBinding, "tester", default)).Outcome);
            Assert.Equal(DurablePlanCurrentStatus.PlanStale, await planner.CheckPlanAsync(first.Id, default));
            var second = Assert.IsType<DurablePlan>((await planner.CreatePlanAsync(changeSetId, "tester", default)).Plan);
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(newBinding.RevisionNumber, second.Inputs.ConfigurationRevision);
            Assert.Equal(oldBinding.RevisionNumber, (await planner.FindPlanAsync(first.Id, default))!.Inputs.ConfigurationRevision);
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
                await planner.CreatePlanAsync(changeSetId, "tester", default)));
            Assert.All(concurrent, result => Assert.Equal(DurablePlanStatus.Planned, result.Status));
            Assert.NotEqual(concurrent[0].Plan!.Id, concurrent[1].Plan!.Id);
            await using (var noCodecServices = Services(connection, time, registerCodec: false))
                Assert.Equal(DurablePlanCurrentStatus.PlanInputUnavailable,
                    await noCodecServices.GetRequiredService<IDurableDeploymentPlanner>().CheckPlanAsync(second.Id, default));
            await using (var noCapabilityServices = Services(connection, time, supportsCapability: false))
                Assert.Equal(DurablePlanCurrentStatus.PlanInputUnavailable,
                    await noCapabilityServices.GetRequiredService<IDurableDeploymentPlanner>().CheckPlanAsync(second.Id, default));
            var closes = await Task.WhenAll(
                planner.ClosePlanAsync(first.Id, "closer-a", default).AsTask(),
                planner.ClosePlanAsync(first.Id, "closer-b", default).AsTask());
            Assert.Contains(DurablePlanCloseStatus.Closed, closes);
            Assert.Contains(DurablePlanCloseStatus.AlreadyClosed, closes);
            Assert.Contains((await planner.FindPlanAsync(first.Id, default))!.ClosedByActorId,
                new[] { "closer-a", "closer-b" });
            Assert.Equal(DurablePlanCurrentStatus.Closed, await planner.CheckPlanAsync(first.Id, default));
            await using (var failureConnection = new NpgsqlConnection(connection))
            {
                await failureConnection.OpenAsync();
                await new NpgsqlCommand("""
                    CREATE FUNCTION reject_plan_close_for_test() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN RAISE EXCEPTION 'test close failure'; END $$;
                    CREATE TRIGGER reject_plan_close_for_test BEFORE INSERT ON deployment_plan_closures
                    FOR EACH ROW EXECUTE FUNCTION reject_plan_close_for_test();
                    """, failureConnection).ExecuteNonQueryAsync();
                await Assert.ThrowsAsync<DbUpdateException>(async () =>
                    await planner.ClosePlanAsync(second.Id, "tester", default));
                Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(second.Id, default));
                await new NpgsqlCommand("""
                    DROP TRIGGER reject_plan_close_for_test ON deployment_plan_closures;
                    DROP FUNCTION reject_plan_close_for_test();
                    """, failureConnection).ExecuteNonQueryAsync();
            }
            time.Advance(TimeSpan.FromHours(24).Add(TimeSpan.FromSeconds(-1)));
            Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(second.Id, default));
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(DurablePlanCurrentStatus.Expired, await planner.CheckPlanAsync(second.Id, default));
            Assert.Equal(DurablePlanCloseStatus.Closed,
                await planner.ClosePlanAsync(second.Id, "late-closer", default));
            Assert.Equal(DurablePlanCurrentStatus.Expired, await planner.CheckPlanAsync(second.Id, default));
            Assert.NotNull(await planner.FindPlanAsync(second.Id, default));
        }
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        var count = (long)(await new NpgsqlCommand("SELECT count(*) FROM deployment_plans", database).ExecuteScalarAsync())!;
        Assert.Equal(4, count);
        await AssertRejected("UPDATE deployment_plans SET actor_id = 'replacement' WHERE id = @id",
            Guid.Parse(first.Id.ToString()));
        await AssertRejected("DELETE FROM deployment_plans WHERE id = @id",
            Guid.Parse(first.Id.ToString()));
        await AssertRejected("UPDATE deployment_change_sets SET actor_id = 'replacement' WHERE id = @id",
            Guid.Parse(changeSetId.ToString()));
        await AssertRejected("DELETE FROM deployment_change_sets WHERE id = @id",
            Guid.Parse(changeSetId.ToString()));
        await AssertRejected("UPDATE deployment_plan_closures SET actor_id = 'replacement' WHERE plan_id = @id",
            Guid.Parse(first.Id.ToString()));
        await AssertRejected("DELETE FROM deployment_plan_closures WHERE plan_id = @id",
            Guid.Parse(first.Id.ToString()));
        var sensitiveColumns = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.columns WHERE table_name IN ('deployment_change_sets', 'deployment_plans', 'deployment_plan_closures') AND column_name ~ '(secret|password|payload|credential)'", database)
            .ExecuteScalarAsync())!;
        Assert.Equal(0, sensitiveColumns);
        await using (var redaction = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM deployment_change_sets AS t WHERE to_jsonb(t)::text LIKE '%' || @secret || '%')
                 + (SELECT count(*) FROM deployment_plans AS t WHERE to_jsonb(t)::text LIKE '%' || @secret || '%')
                 + (SELECT count(*) FROM deployment_plan_closures AS t WHERE to_jsonb(t)::text LIKE '%' || @secret || '%')
            """, database))
        {
            redaction.Parameters.AddWithValue("secret", providerPassword);
            Assert.Equal(0L, (long)(await redaction.ExecuteScalarAsync())!);
        }
        await using var noCodec = Services(connection, time, registerCodec: false);
        Assert.Equal(DurablePlanStatus.ProviderRevisionUnavailable,
            (await noCodec.GetRequiredService<IDurableDeploymentPlanner>()
                .CreatePlanAsync(changeSetId, "tester", default)).Status);
        await using var noCapability = Services(connection, time, supportsCapability: false);
        Assert.Equal(DurablePlanStatus.ProviderCapabilityMismatch,
            (await noCapability.GetRequiredService<IDurableDeploymentPlanner>()
                .CreatePlanAsync(changeSetId, "tester", default)).Status);

        async Task AssertRejected(string sql, Guid id)
        {
            await using var command = new NpgsqlCommand(sql, database);
            command.Parameters.AddWithValue("id", id);
            var error = await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync());
            Assert.Equal("P0001", error.SqlState);
        }
    }

    [Fact]
    public async Task Wrong_environment_missing_revision_and_generation_are_typed_without_writes()
    {
        var connection = await NewDatabaseAsync();
        await using var services = Services(connection, new AdjustableTimeProvider());
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var environment = await services.GetRequiredService<ISecretStore>()
            .CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), Catalog, new AdjustableTimeProvider())
            .ExecuteAsync(Envelope, "tester"));
        var reference = imported.ManifestRevisionRef;
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        Assert.Equal(DurableChangeSetCreateStatus.EnvironmentNotFound,
            (await planner.CreateChangeSetAsync(EnvironmentRecordId.New(), reference.ApplicationRecordId,
                reference, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).Status);
        Assert.Equal(DurableChangeSetCreateStatus.RevisionNotOwnedByApplication,
            (await planner.CreateChangeSetAsync(environment, ApplicationRecordId.New(), reference,
                new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).Status);
        Assert.Equal(DurableChangeSetCreateStatus.RevisionNotFound,
            (await planner.CreateChangeSetAsync(environment, reference.ApplicationRecordId,
                ManifestRevisionRef.Create(reference.ApplicationRecordId, ManifestRevisionNumber.Create(99)),
                new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).Status);
        Assert.Equal(DurableChangeSetCreateStatus.ExpectedGenerationUnsupported,
            (await planner.CreateChangeSetAsync(environment, reference.ApplicationRecordId, reference,
                new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(1)), "tester", default)).Status);
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        Assert.Equal(0L, (long)(await new NpgsqlCommand("SELECT count(*) FROM deployment_change_sets", database).ExecuteScalarAsync())!);
    }

    private static async Task AssertImmutableRejected(NpgsqlConnection database, string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, database);
        command.Parameters.AddWithValue("id", id);
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync());
        Assert.Equal("P0001", error.SqlState);
    }

    private async Task<(string Connection, AdjustableTimeProvider Time, EnvironmentRecordId Environment,
        ManifestRevisionRef Revision, ProviderAliasBinding First, ProviderAliasBinding Second,
        string Password)> PrepareCommitAsync()
    {
        var connection = await NewDatabaseAsync();
        var time = new AdjustableTimeProvider();
        await using var services = Services(connection, time);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = services.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var password = "haby-test-" + Guid.NewGuid().ToString("N");
        var user = "haby_commit_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE ROLE \"{user}\" LOGIN CREATEDB PASSWORD '{password}'", admin)
                .ExecuteNonQueryAsync();
        }
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        await secrets.CreateAsync(environment, secret, Encoding.UTF8.GetBytes(password), default);
        var target = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        { Username = user, Password = password };
        var configuration = new PostgreSqlProviderConfiguration(1, target.Host!, target.Port,
            target.Database!, target.Username!, secret, Implementation);
        var provider = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await provider.CreateInstanceAsync(environment, configuration, default);
        await provider.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1),
            configuration, default);
        await provider.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(2),
            configuration, default);
        var first = new ProviderAliasBinding(instance, ProviderConfigurationRevisionNumber.Create(1));
        var second = new ProviderAliasBinding(instance, ProviderConfigurationRevisionNumber.Create(2));
        Assert.Equal(ProviderAliasChangeOutcome.Changed,
            (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment,
                ProviderAlias.Create("postgresql.primary"), null, first, "tester", default)).Outcome);
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), Catalog, time).ExecuteAsync(Envelope, "tester"));
        return (connection, time, environment, imported.ManifestRevisionRef, first, second, password);
    }

    private async Task<string> NewDatabaseAsync()
    {
        var name = "haby_plan_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    private static ServiceProvider Services(string connection, TimeProvider clock, bool registerCodec = true,
        bool supportsCapability = true, SaveChangesInterceptor? saveInterceptor = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(clock)
            .AddPostgreSqlManifestCatalog(connection)
            .AddPostgreSqlPlaintextSecretStore()
            .AddPostgreSqlProviderConfigurationStore()
            .AddPostgreSqlProviderAliasStore();
        if (saveInterceptor is not null)
            services.AddDbContextFactory<ManifestCatalogDbContext>(options => options.AddInterceptors(saveInterceptor));
        if (supportsCapability) services.AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>();
        else services.AddSingleton<ICheckpointProviderCapability, RejectCapability>();
        if (registerCodec) services.AddPostgreSqlProviderConfigurationCodec();
        return services.AddPostgreSqlProviderRevisionPreflight()
            .AddPostgreSqlDurableDesiredStateCommitter().BuildServiceProvider();
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class RejectCapability : ICheckpointProviderCapability
    {
        public bool Supports(ProviderResolution selection, ResourceTypeId contract) => false;
    }

    private sealed class CancelAfterCommitRowsSaved(CancellationTokenSource source) : SaveChangesInterceptor
    {
        public bool Triggered { get; private set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries().Any(entry =>
                entry.Metadata.GetTableName() == "change_set_commits") == true)
            {
                Triggered = true;
                source.Cancel();
            }
            return new ValueTask<int>(result);
        }
    }
}
