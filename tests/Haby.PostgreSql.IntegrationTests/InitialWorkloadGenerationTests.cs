using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Kubernetes.Contracts;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    private static readonly ProviderInput InitialSubject = new ProviderInput.Workload(ManifestLocalId.Create("api"), ManifestLocalId.Create("main"));

    [Fact]
    public async Task Initial_workload_accepts_empty_checkpoint_once_without_Resource_Provider_or_Applied_and_public_shortcuts_refuse()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, subMicrosecondCommit: true);
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var handler = new InternalDeploymentAction(services.GetRequiredService<ISecretStore>());
        var actions = Actions(services, handler);
        var keys = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            await actions.AcceptAsync(scenario.Commit, InitialSubject, InternalDeploymentAction.Create, "initial", default)));
        Assert.All(keys, key => Assert.Equal(keys[0], key));
        var key = keys[0];
        var committer = (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>();
        Assert.Equal(scenario.Commit, Assert.IsType<DurableCommitResult.Committed>(await committer.CommitInternalAsync(scenario.Commit.ChangeSetId,
            scenario.Commit.PlanId, "replay", default)).Commit);
        Assert.IsType<DurableCommitResult.UnsupportedShape>(await committer.CommitAsync(scenario.Commit.ChangeSetId, scenario.Commit.PlanId, "public", default));
        Assert.Equal(DurableExecutionOutcome.OperationNotFound, (await services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(key, default)).Outcome);
        Assert.Null(await services.GetRequiredService<IDurableOperationJournal>().ProgressAsync(key.DeploymentId, key.Generation, default));
        await using (var act = await factory.CreateDbContextAsync())
        {
            Assert.False(await DurableAggregateCompletion.TryAdvanceAsync(act, Guid.Parse(key.DeploymentId.ToString()), 1, default));
            var generation = await act.DeploymentGenerations.AsNoTracking().SingleAsync();
            await Assert.ThrowsAsync<InvalidDataException>(() => DurableAggregateCompletion.ReadAsync(act, generation, default));
        }
        await using var verify = await factory.CreateDbContextAsync();
        var owner = await verify.ApplicationDeployments.AsNoTracking().SingleAsync();
        Assert.Equal(1, owner.DesiredGeneration); Assert.Null(owner.AppliedGeneration);
        var source = await verify.DeploymentGenerations.AsNoTracking().SingleAsync();
        var graph = DurableOperationGraphCodec.Decode(source.GraphJson);
        Assert.Null(graph.Baseline); Assert.Empty(graph.Nodes); Assert.Empty(graph.ReusedResources); Assert.Empty(graph.RetainedResources);
        Assert.Empty(source.Inputs().Configuration!.Manifest.Spec.Resources);
        Assert.All(source.Inputs().Providers.Entries.Keys, subject => Assert.IsType<ProviderInput.Workload>(subject));
        Assert.Equal(KubernetesProviderConfigurationCodec.Kind, (await verify.ProviderInstances.AsNoTracking().SingleAsync()).ProviderKindId);
        Assert.Empty(await verify.ResourceManagement.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.ActionIntents.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
        var proof = (await DurableAggregateCompletion.ReadAsync(verify, source, default, true))!;
        Assert.Empty(proof.Resources); Assert.Equal(source.Fingerprint, proof.InputFingerprint);
        Assert.Equal((await verify.GenerationStageCheckpoints.AsNoTracking().SingleAsync()).Id, proof.Id);
        Assert.Equal(proof.Id, (await verify.ActionIntents.AsNoTracking().SingleAsync()).CheckpointId);
    }

    [Fact]
    public async Task Initial_ExpectedAbsence_commit_race_and_equivalent_replay_accept_one_identity()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, acceptGeneration: async (services, environment, revision) =>
        {
            var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
            var committer = (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>();
            var handlers = services.GetServices<IInternalActionHandler>().ToArray();
            var requests = new List<(DeploymentChangeSetId Change, DeploymentPlanId Plan)>();
            for (var i = 0; i < 2; i++)
            {
                var change = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, revision, new DeploymentPrecondition.ExpectedAbsence(), "race", default)).ChangeSet!;
                Assert.Equal(DurablePlanStatus.UnsupportedShape, (await planner.CreatePlanAsync(change.Id, "public", default)).Status);
                requests.Add((change.Id, (await planner.CreateInternalPlanAsync(change.Id, "race", default, handlers)).Plan!.Id));
            }
            var results = await Task.WhenAll(requests.Select(async request => await committer.CommitInternalAsync(request.Change, request.Plan, "race", default, handlers)));
            var accepted = Assert.Single(results.OfType<DurableCommitResult.Committed>()).Commit;
            Assert.Single(results.OfType<DurableCommitResult.ChangeSetStale>());
            Assert.Equal(accepted, Assert.IsType<DurableCommitResult.Committed>(await committer.CommitInternalAsync(accepted.ChangeSetId, accepted.PlanId, "replay", default)).Commit);
            return accepted;
        });
        await using var verifyServices = InternalServices(scenario.Connection, scenario.Time);
        await using var verify = await verifyServices.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Single(await verify.ChangeSetCommits.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Initial_Placement_freshness_and_frozen_inputs_keep_exact_revisions_after_selection_changes()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, acceptGeneration: async (services, environment, revision) =>
        {
            var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
            var committer = (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>();
            var handlers = services.GetServices<IInternalActionHandler>().ToArray();
            var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            var change = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, revision, new DeploymentPrecondition.ExpectedAbsence(), "freshness", default)).ChangeSet!;
            var stale = (await planner.CreateInternalPlanAsync(change.Id, "freshness", default, handlers)).Plan!;
            var codec = new KubernetesPlacementCodec();
            var later = codec.Decode("""{"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"later","revision":1},"spec":{"namespace":"later"}}"""u8.ToArray());
            var alias = ProviderAlias.Create("kubernetes.primary");
            var reference = new PlacementRevisionKey(environment, alias, later.Name, later.Revision);
            await new PostgreSqlPlacementRevisionStore(factory, codec).CompareAndCreateAsync(reference, later, new("tester", DateTimeOffset.UtcNow), default);
            var selections = new PostgreSqlPlacementSelectionStore(factory, codec);
            var target = PlacementSelectionTarget.Create("environment", "", "", "");
            var old = (await selections.FindAsync(environment, alias, target, default))!;
            await selections.ApplyAsync(target, reference, new PlacementSelectionPrecondition.ExpectedToken(old.Token), new("tester", DateTimeOffset.UtcNow, null), default);
            Assert.IsType<DurableCommitResult.PlanInputUnavailable>(await committer.CommitInternalAsync(change.Id, stale.Id, "stale", default, handlers));
            var fresh = (await planner.CreateInternalPlanAsync(change.Id, "freshness", default, handlers)).Plan!;
            return Assert.IsType<DurableCommitResult.Committed>(await committer.CommitInternalAsync(change.Id, fresh.Id, "freshness", default, handlers)).Commit;
        });
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var codec = new KubernetesPlacementCodec();
        var selections = new PostgreSqlPlacementSelectionStore(factory, codec);
        var alias = ProviderAlias.Create("kubernetes.primary"); var target = PlacementSelectionTarget.Create("environment", "", "", "");
        var current = (await selections.FindAsync(scenario.Environment, alias, target, default))!;
        await selections.ApplyAsync(target, new(scenario.Environment, alias, ManifestLocalId.Create("standard"), 1),
            new PlacementSelectionPrecondition.ExpectedToken(current.Token), new("tester", DateTimeOffset.UtcNow, null), default);
        var actions = Actions(services, new InternalDeploymentAction(services.GetRequiredService<ISecretStore>()));
        var key = await actions.AcceptAsync(scenario.Commit, InitialSubject, InternalDeploymentAction.Create, "frozen", default);
        Assert.Equal("later", DeploymentIntents.Decode((await actions.HistoryAsync(key, default))!.Intent.Payload).Namespace);
        Guid? placement;
        await using (var read = await factory.CreateDbContextAsync())
        {
            var generation = await read.DeploymentGenerations.AsNoTracking().SingleAsync();
            placement = (await read.InternalPlanInputs.AsNoTracking().SingleAsync(x => x.PlanId == generation.PlanId)).PlacementId;
        }
        await using (var act = await factory.CreateDbContextAsync())
            await Assert.ThrowsAsync<PostgresException>(async () => await act.PlacementRevisions.Where(x => x.Id == placement).ExecuteDeleteAsync());
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Fact]
    public async Task Initial_second_unsupported_subject_or_missing_exact_handler_refuses_before_desired_acceptance()
    {
        foreach (var invalid in new[] { false, true })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => InternalScenarioAsync(initialOnly: true, invalidSecondWorkload: invalid,
                acceptGeneration: async (services, environment, revision) =>
                {
                    var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
                    var change = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, revision, new DeploymentPrecondition.ExpectedAbsence(), "refuse", default)).ChangeSet!;
                    Assert.Equal(DurablePlanStatus.UnsupportedShape, (await planner.CreateInternalPlanAsync(change.Id, "refuse", default,
                        invalid ? services.GetServices<IInternalActionHandler>().ToArray() : [])).Status);
                    await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
                    Assert.Empty(await verify.ApplicationDeployments.AsNoTracking().ToArrayAsync());
                    Assert.Empty(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync());
                    Assert.Empty(await verify.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
                    throw new InvalidDataException("Expected refusal observed.");
                }));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_missing_handler_or_foreign_current_secret_cannot_confirm_or_replay(bool foreignSecret)
    {
        var scenario = await InternalScenarioAsync(initialOnly: true);
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = Actions(services, new InternalDeploymentAction(services.GetRequiredService<ISecretStore>()));
        var key = await actions.AcceptAsync(scenario.Commit, InitialSubject, InternalDeploymentAction.Create, "credentials", default);
        if (!foreignSecret)
            await Assert.ThrowsAsync<InvalidDataException>(async () => await Actions(services, new SyntheticAction()).ExecuteAsync(key, default));
        else
        {
            var other = await services.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("foreign"), default);
            await using (var edit = new NpgsqlConnection(scenario.Connection))
            {
                await edit.OpenAsync();
                await new NpgsqlCommand("SET session_replication_role = replica", edit).ExecuteNonQueryAsync();
                await using var command = new NpgsqlCommand("UPDATE secrets SET environment_record_id = @environment", edit);
                command.Parameters.AddWithValue("environment", Guid.Parse(other.ToString()));
                await command.ExecuteNonQueryAsync();
            }
            Assert.Equal(InternalActionOutcome.Refused, (await actions.ExecuteAsync(key, default)).Outcome);
            Assert.Equal(InternalActionOutcome.Unavailable, (await actions.RecoverByObservationAsync(key, default)).Outcome);
        }
        var history = (await actions.HistoryAsync(key, default))!;
        Assert.Null(history.Output);
        Assert.Equal(foreignSecret ? 1 : 0, history.Attempts.Count);
        if (foreignSecret) Assert.Equal("Uncertain", history.Attempts[0].Outcome);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("manifest")]
    [InlineData("checkpoint")]
    [InlineData("missing-checkpoint")]
    [InlineData("nonempty-proof")]
    [InlineData("resource-action")]
    [InlineData("undeclared-workload")]
    [InlineData("action-provider")]
    [InlineData("action-placement")]
    [InlineData("action-fingerprint")]
    [InlineData("orphan-operation")]
    [InlineData("expected-generation")]
    public async Task Initial_empty_stage_refuses_corrupt_or_forged_authority_without_repair(string corruption)
    {
        var scenario = await InternalScenarioAsync(initialOnly: true);
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = Actions(services, new InternalDeploymentAction(services.GetRequiredService<ISecretStore>()));
        var key = await actions.AcceptAsync(scenario.Commit, InitialSubject, InternalDeploymentAction.Create, "initial", default);
        GenerationStageCheckpointRow saved;
        await using (var read = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            saved = await read.GenerationStageCheckpoints.AsNoTracking().SingleAsync();
        await using (var edit = new NpgsqlConnection(scenario.Connection))
        {
            await edit.OpenAsync();
            await new NpgsqlCommand("SET session_replication_role = replica", edit).ExecuteNonQueryAsync();
            var sql = corruption switch
            {
                "fingerprint" => "UPDATE application_deployment_generations SET fingerprint = 'v4:' || repeat('0',64)",
                "manifest" => "UPDATE manifest_revisions SET snapshot = replace(snapshot, 'registry.example/app:v1', 'registry.example/other:v2')",
                "checkpoint" => "UPDATE generation_stage_checkpoints SET evidence_fingerprint = repeat('0',64)",
                "missing-checkpoint" => "DELETE FROM generation_stage_checkpoints",
                "nonempty-proof" => "UPDATE generation_stage_checkpoints SET evidence_json = @evidence, evidence_fingerprint = @hash",
                "resource-action" => "UPDATE durable_action_intents SET subject_kind = 'Resource', component = '', local_id = 'fabricated'",
                "undeclared-workload" => "UPDATE durable_action_intents SET component = 'undeclared'",
                "action-provider" => "UPDATE durable_action_intents SET provider_id = '00000000-0000-0000-0000-000000000001'",
                "action-placement" => "UPDATE durable_action_intents SET placement_id = NULL",
                "action-fingerprint" => "UPDATE durable_action_intents SET input_fingerprint = 'v4:' || repeat('0',64)",
                "orphan-operation" => "UPDATE durable_create_operations SET operation_id = 'unattributed'",
                _ => "UPDATE deployment_change_sets SET expected_absence = false, expected_generation = 1"
            };
            await using var command = new NpgsqlCommand(sql, edit);
            if (corruption == "nonempty-proof")
            {
                var json = ResourcesReadyCheckpointCodec.Encode(new(saved.Id, saved.DeploymentId, saved.Generation, saved.InputFingerprint, saved.ConfirmedAt,
                    [new ResourceReadyResult("fabricated", Guid.CreateVersion7(), 1, saved.InputFingerprint, false, false,
                        [new ResourceOperationResult("fabricated", Guid.CreateVersion7(), 1, null, [])])]));
                command.Parameters.AddWithValue("evidence", json);
                command.Parameters.AddWithValue("hash", ResourcesReadyCheckpointCodec.Hash(json));
            }
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await actions.AcceptAsync(scenario.Commit, InitialSubject, InternalDeploymentAction.Create, "initial", default));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await actions.ExecuteAsync(key, default));
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        var source = await verify.DeploymentGenerations.AsNoTracking().SingleAsync();
        if (corruption == "missing-checkpoint") Assert.Null(await DurableAggregateCompletion.ReadAsync(verify, source, default, true));
        else await Assert.ThrowsAsync<InvalidDataException>(() => DurableAggregateCompletion.ReadAsync(verify, source, default, true));
        Assert.Equal(corruption == "missing-checkpoint" ? 0 : 1, await verify.GenerationStageCheckpoints.CountAsync());
        Assert.Single(await verify.ActionIntents.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }
}
