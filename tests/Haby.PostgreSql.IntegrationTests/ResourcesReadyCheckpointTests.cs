using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ExactActionAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_mismatched_current_output_metadata_refuses_before_dispatch_without_downgrading_saved_obligation(bool mismatch)
    {
        var boundary = new QueueBoundary { ExternalEvidence = true };
        var scenario = await GraphCase(boundary);
        string original;
        await using (var read = OutputContext(scenario.Connection))
        {
            original = (await read.DeploymentGenerations.AsNoTracking().SingleAsync()).GraphJson;
            var producer = scenario.Plan.Graph.Nodes.Single(x => x.Id == QueueEvidenceProducerId);
            Assert.True(producer.RequiresExternalOutput); Assert.Equal(new(QueueEvidenceContract.Contract, "receipt"), producer.RequiredOutput);
        }
        if (mismatch) boundary.OutputIdentityOverride = "sample.incompatible-result/v99";
        else boundary.OutputContractRegistrations = 0;
        await using (var act = Services(scenario.Connection, boundary))
        {
            var result = await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(
                new(scenario.Commit.DeploymentId, scenario.Commit.Generation, QueueEvidenceProducerId), default);
            Assert.Equal(mismatch ? DurableExecutionOutcome.InputUnavailable : DurableExecutionOutcome.HandlerUnavailable, result.Outcome);
        }
        await using var verify = OutputContext(scenario.Connection);
        Assert.Equal(original, (await verify.DeploymentGenerations.AsNoTracking().SingleAsync()).GraphJson);
        Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync()); Assert.Equal(0, boundary.Calls);
    }

    private async Task<(string Connection, DurableChangeSetCommit Commit, DurablePlan Plan)> QueueUpdate(QueueBoundary boundary, bool mixed)
    {
        var original = await GraphCase(boundary);
        await using var services = Services(original.Connection, boundary);
        Assert.Null((await services.GetRequiredService<DurableGenerationRunner>().RunAsync(original.Commit.DeploymentId, original.Commit.Generation, default)).StopOutcome);
        var envelope = System.Text.Json.Nodes.JsonNode.Parse("""
            {"applicationVersion":"v2","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application",
            "metadata":{"id":"graph-app"},"spec":{"resources":{"jobs":{"type":"sample.queue/v1",
            "providerRef":"queues.primary","properties":{"queueName":"jobs"}}}}}}
            """u8)!;
        if (mixed) envelope["manifest"]!["spec"]!["resources"]!["extra"] = envelope["manifest"]!["spec"]!["resources"]!["jobs"]!.DeepClone();
        var catalog = new ManifestContractCatalog([new ResourceTypeDefinition(QueueContract, "{\"type\":\"object\"}")], [], [], []);
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), catalog, TimeProvider.System).ExecuteAsync(System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString()), "tester"));
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(original.Plan.Inputs.Environment, imported.ManifestRevisionRef.ApplicationRecordId,
            imported.ManifestRevisionRef, new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(1)), "tester", default)).ChangeSet!;
        var plan = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
        var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, plan.Id, "tester", default)).Commit;
        return (original.Connection, commit, plan);
    }

    private static async Task WaitForCompletionLock(string connection, int count)
    {
        await using var observer = new NpgsqlConnection(connection); await observer.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var query = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", observer);
            if ((long)(await query.ExecuteScalarAsync(deadline.Token))! >= count) return;
            await Task.Delay(10, deadline.Token);
        }
    }

    [Fact]
    public async Task Canceled_completion_then_competing_finalizers_create_one_original_checkpoint_without_new_attempts()
    {
        var boundary = new QueueBoundary { ExternalEvidence = true };
        var scenario = await QueueUpdate(boundary, mixed: false);
        await using var services = Services(scenario.Connection, boundary);
        var journal = services.GetRequiredService<IDurableOperationJournal>();
        var admission = services.GetRequiredService<DurableActionAdmission>();
        await using var blocker = new NpgsqlConnection(scenario.Connection); await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await new NpgsqlCommand("SELECT id FROM application_deployments FOR UPDATE", blocker, transaction).ExecuteNonQueryAsync();
        using (var cancel = new CancellationTokenSource())
        {
            var pending = journal.TryCompleteGenerationAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, admission, cancel.Token).AsTask();
            await WaitForCompletionLock(scenario.Connection, 1);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        await using (var verifyCanceled = OutputContext(scenario.Connection))
        {
            Assert.False(await verifyCanceled.GenerationStageCheckpoints.AnyAsync(x => x.Generation == 2));
            Assert.Equal(1, (await verifyCanceled.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        var first = journal.TryCompleteGenerationAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, admission, default).AsTask();
        var second = journal.TryCompleteGenerationAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, admission, default).AsTask();
        await WaitForCompletionLock(scenario.Connection, 2);
        Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        await transaction.CommitAsync();
        Assert.All(await Task.WhenAll(first, second), Assert.True);
        await using var verify = OutputContext(scenario.Connection);
        var checkpoint = await verify.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.Generation == 2);
        Assert.Equal(1, Assert.Single(ResourcesReadyCheckpointCodec.Decode(checkpoint.EvidenceJson).Resources).SourceGeneration);
        Assert.Equal(2, await verify.GenerationStageCheckpoints.CountAsync());
        Assert.Equal(2, await verify.DurableOperationAttempts.CountAsync()); Assert.Equal(1, boundary.Calls);
        Assert.Equal(2, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Fact]
    public async Task Confirmed_new_output_survives_reused_peer_loss_at_completion_lock_and_resume_replays_no_effects()
    {
        var boundary = new QueueBoundary { ExternalEvidence = true };
        var scenario = await QueueUpdate(boundary, mixed: true);
        var key = new DurableOperationKey(scenario.Commit.DeploymentId, scenario.Commit.Generation, OperationId.Create("extra-create"));
        await using var services = Services(scenario.Connection, boundary);
        var journal = services.GetRequiredService<IDurableOperationJournal>();
        var attempt = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(key, DateTimeOffset.UtcNow, default));
        Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(key, attempt.AttemptId, DateTimeOffset.UtcNow, default));
        // The test adapter's actual counted external-result boundary has returned.
        boundary.Create(new("jobs"));
        var output = QueueEvidenceContract.Encode(new("jobs", "queue-broker", "accepted-2"));
        string originalPayload;
        await using (var read = OutputContext(scenario.Connection))
            originalPayload = (await read.OperationOutputs.AsNoTracking().SingleAsync(x => x.Generation == 1)).Payload;
        await using (var blocker = new NpgsqlConnection(scenario.Connection))
        {
            await blocker.OpenAsync(); await using var transaction = await blocker.BeginTransactionAsync();
            await new NpgsqlCommand("SELECT id FROM application_deployments FOR UPDATE", blocker, transaction).ExecuteNonQueryAsync();
            var publication = services.GetRequiredService<IExternalOperationOutputs>().CompleteAsync(key, attempt.AttemptId, output, DateTimeOffset.UtcNow, default).AsTask();
            await WaitForCompletionLock(scenario.Connection, 1);
            await new NpgsqlCommand("SET LOCAL session_replication_role = replica; DELETE FROM operation_outputs WHERE generation = 1", blocker, transaction).ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            Assert.True(await publication);
        }
        await using (var verifyBlocked = OutputContext(scenario.Connection))
        {
            Assert.Equal("Succeeded", (await verifyBlocked.DurableOperations.AsNoTracking().SingleAsync(x => x.Generation == 2)).State);
            Assert.Equal("Succeeded", (await verifyBlocked.DurableOperationAttempts.AsNoTracking().SingleAsync(x => x.Generation == 2)).Outcome);
            Assert.Equal(output.Payload, (await verifyBlocked.OperationOutputs.AsNoTracking().SingleAsync(x => x.Generation == 2)).Payload);
            Assert.False(await verifyBlocked.GenerationStageCheckpoints.AnyAsync(x => x.Generation == 2));
            Assert.Equal(1, (await verifyBlocked.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        var blocked = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(key.DeploymentId, key.Generation, default);
        Assert.Equal(DurableExecutionOutcome.InputUnavailable, blocked.StopOutcome);
        // Restore only the independently saved original result in this adversarial fixture.
        await using (var repair = new NpgsqlConnection(scenario.Connection))
        {
            await repair.OpenAsync();
            await using var insert = new NpgsqlCommand("INSERT INTO operation_outputs(deployment_id,generation,operation_id,output_contract,output_key,payload) VALUES ($1,1,'10-external',$2,'receipt',$3)", repair);
            insert.Parameters.AddWithValue(Guid.Parse(key.DeploymentId.ToString())); insert.Parameters.AddWithValue(QueueEvidenceContract.Contract); insert.Parameters.AddWithValue(originalPayload);
            await insert.ExecuteNonQueryAsync();
        }
        var result = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(key.DeploymentId, key.Generation, default);
        Assert.Null(result.StopOutcome); Assert.Equal(2, result.Progress!.AppliedGeneration);
        Assert.Equal(new long[] { 2, 1 }, result.Progress.ResourcesReady!.Resources.Select(x => x.SourceGeneration));
        Assert.Equal(2, boundary.Calls);
        await using var final = OutputContext(scenario.Connection);
        Assert.Equal(3, await final.DurableOperationAttempts.CountAsync()); Assert.Equal(2, await final.OperationOutputs.CountAsync());
    }

    [Fact]
    public async Task Persisted_checkpoint_keeps_exact_original_attempts_identity_and_time_across_restart_and_concurrent_replay()
    {
        var boundary = new QueueBoundary { Graph = true };
        var scenario = await GraphCase(boundary);
        await using (var act = Services(scenario.Connection, boundary))
        {
            var result = await act.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
            Assert.Null(result.StopOutcome);
            Assert.NotNull(result.Progress!.ResourcesReady);
        }
        string original;
        await using (var verify = OutputContext(scenario.Connection))
        {
            var row = await verify.GenerationStageCheckpoints.AsNoTracking().SingleAsync();
            original = row.EvidenceJson;
            var proof = ResourcesReadyCheckpointCodec.Decode(original);
            Assert.Equal(row.Id, proof.Id); Assert.Equal(row.ConfirmedAt, proof.ConfirmedAt);
            Assert.Equal(scenario.Plan.Fingerprint, proof.InputFingerprint);
            var resource = Assert.Single(proof.Resources);
            Assert.Equal("jobs", resource.Resource); Assert.Equal(1, resource.SourceGeneration);
            Assert.False(resource.Retained); Assert.False(resource.Reintroduced);
            Assert.Equal((await verify.ResourceManagement.AsNoTracking().SingleAsync()).Id, resource.ManagementId);
            Assert.Equal(new[] { "10-first", "20-dependent", "30-independent" }, resource.Operations.Select(x => x.OperationId));
            var attempts = await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync();
            foreach (var operation in resource.Operations)
            {
                Assert.Null(operation.RecoveryDecisionId); Assert.Empty(operation.Outputs);
                var attempt = Assert.Single(attempts, x => x.OperationId == operation.OperationId);
                Assert.Equal(attempt.AttemptId, operation.AttemptId); Assert.Equal(1, operation.AttemptNumber);
                Assert.Equal("Succeeded", attempt.Outcome);
            }
        }
        // Terminal replay remains catalog-only when the adapter and codec are absent.
        await using (var restart = Services(scenario.Connection, boundary, installed: false, codec: false))
        {
            var journal = restart.GetRequiredService<IDurableOperationJournal>();
            var admission = new DurableActionAdmission(new DurableActionRegistry([], []));
            Assert.All(await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => journal.TryCompleteGenerationAsync(
                scenario.Commit.DeploymentId, scenario.Commit.Generation, admission, default).AsTask())), Assert.True);
        }
        await using var final = OutputContext(scenario.Connection);
        Assert.Equal(original, (await final.GenerationStageCheckpoints.AsNoTracking().SingleAsync()).EvidenceJson);
        Assert.Equal(3, await final.DurableOperationAttempts.CountAsync()); Assert.Equal(3, boundary.Calls);
        Assert.Equal(1, (await final.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("attempt")]
    [InlineData("management")]
    [InlineData("deployment")]
    [InlineData("missing")]
    public async Task Conflicting_checkpoint_is_refused_without_rewrite_repair_or_effect_replay(string fault)
    {
        var boundary = new QueueBoundary { Graph = true };
        var scenario = await GraphCase(boundary);
        await using (var act = Services(scenario.Connection, boundary))
            await act.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
        string corrupt;
        await using (var read = OutputContext(scenario.Connection))
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse((await read.GenerationStageCheckpoints.AsNoTracking().SingleAsync()).EvidenceJson)!;
            switch (fault)
            {
                case "version": json["Version"] = 99; break;
                case "attempt": json["Resources"]![0]!["Operations"]![0]!["Attempt"] = Guid.CreateVersion7().ToString(); break;
                case "management": json["Resources"]![0]!["Management"] = Guid.CreateVersion7().ToString(); break;
                case "deployment": json["Deployment"] = Guid.CreateVersion7().ToString(); break;
            }
            corrupt = fault == "hash" ? "{}" : json.ToJsonString();
        }
        await using (var mutate = new NpgsqlConnection(scenario.Connection))
        {
            await mutate.OpenAsync();
            await using var update = new NpgsqlCommand(fault == "missing" ? "DELETE FROM generation_stage_checkpoints" :
                "UPDATE generation_stage_checkpoints SET evidence_json = $1, evidence_fingerprint = $2", mutate);
            if (fault != "missing")
            {
                update.Parameters.AddWithValue(corrupt);
                update.Parameters.AddWithValue(fault == "hash" ? new string('A', 64) : ResourcesReadyCheckpointCodec.Hash(corrupt));
            }
            await update.ExecuteNonQueryAsync();
        }
        await using (var restart = Services(scenario.Connection, boundary))
        {
            var journal = restart.GetRequiredService<IDurableOperationJournal>();
            await Assert.ThrowsAsync<InvalidDataException>(() => journal.ProgressAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default).AsTask());
            Assert.False(await journal.TryCompleteGenerationAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation,
                new DurableActionAdmission(new DurableActionRegistry([], [])), default));
        }
        await using var verify = OutputContext(scenario.Connection);
        if (fault == "missing") Assert.Empty(await verify.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
        else Assert.Equal(corrupt, (await verify.GenerationStageCheckpoints.AsNoTracking().SingleAsync()).EvidenceJson);
        Assert.Equal(3, boundary.Calls); Assert.Equal(3, await verify.DurableOperationAttempts.CountAsync());
    }

    [Fact]
    public async Task Missing_registration_cannot_turn_a_saved_output_producer_into_plain_journal_success()
    {
        var boundary = new QueueBoundary { ExternalEvidence = true };
        var scenario = await GraphCase(boundary);
        var key = new DurableOperationKey(scenario.Commit.DeploymentId, scenario.Commit.Generation, QueueEvidenceProducerId);
        Guid attempt;
        await using (var arrange = Services(scenario.Connection, boundary))
        {
            var journal = arrange.GetRequiredService<IDurableOperationJournal>();
            attempt = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(key, DateTimeOffset.UtcNow, default)).AttemptId;
            Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(key, attempt, DateTimeOffset.UtcNow, default));
        }
        await using (var act = Services(scenario.Connection, boundary, installed: false))
            Assert.False(await act.GetRequiredService<IDurableOperationJournal>().TryRecordSuccessAsync(key, attempt, DateTimeOffset.UtcNow, default));
        await using var verify = Services(scenario.Connection, boundary);
        await using var db = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal("Dispatching", (await db.DurableOperations.AsNoTracking().SingleAsync(x => x.OperationId == QueueEvidenceProducerId.Value)).State);
        Assert.Empty(await db.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Aggregate_completion_refuses_succeeded_rows_without_confirmed_attempts()
    {
        var scenario = await GraphCase(new QueueBoundary { Graph = true });
        // Deliberate catalog corruption: a terminal state alone is not result evidence.
        await GraphSql(scenario.Connection, """
            ALTER TABLE durable_create_operations DISABLE TRIGGER USER;
            UPDATE durable_create_operations SET state = 'Succeeded', finalized_at = now();
            ALTER TABLE durable_create_operations ENABLE TRIGGER USER;
            """);
        await using (var act = Services(scenario.Connection, new QueueBoundary { Graph = true }))
        await using (var db = await act.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await DurableAggregateCompletion.TryAdvanceAsync(db, Guid.Parse(scenario.Commit.DeploymentId.ToString()),
                scenario.Commit.Generation.Value, default);
            await transaction.CommitAsync();
        }
        await using var verify = Services(scenario.Connection, new QueueBoundary { Graph = true });
        await using var context = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Null((await context.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Empty(await context.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
    }
}
