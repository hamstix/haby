using System.Data.Common;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ExactActionAdmissionTests
{
    private static DurableOperationKey MaterialKey(DurableChangeSetCommit commit) =>
        new(commit.DeploymentId, commit.Generation, OperationId.Create("10-material"));
    private static DurableOperationKey ConsumerKey(DurableChangeSetCommit commit) =>
        new(commit.DeploymentId, commit.Generation, OperationId.Create("20-dependent"));
    private static OperationOutputRef PasswordSelector => GeneratedCredentialContracts.PasswordFrom(OperationId.Create("10-material"));
    private static ManifestCatalogDbContext OutputContext(string connection) =>
        new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
    private static async Task<byte[]> CredentialBytes(ServiceProvider services, DurableOperationKey key, SecretRef reference)
    {
        var input = await services.GetRequiredService<IGeneratedCredentialOperations>().FindInputAsync(key, default);
        using var material = await services.GetRequiredService<ISecretStore>().ResolveAsync(
            new(new SecretOwner.Resource(input!.Owner), SecretPurpose.ApplicationLogin), reference, default);
        Assert.NotNull(material);
        return material.CopyBytes();
    }

    [Fact]
    public async Task Non_database_producer_crosses_plan_check_commit_exact_dispatch_and_real_restore_without_provider_effects()
    {
        var boundary = new QueueBoundary { Generated = true };
        var scenario = await GraphCase(boundary);
        var producer = MaterialKey(scenario.Commit);
        var consumer = ConsumerKey(scenario.Commit);
        GeneratedCredentialOutput output;
        byte[] bytes;
        await using (var act = Services(scenario.Connection, boundary))
        {
            var executor = act.GetRequiredService<IDurableOperationExecutor>();
            var outputs = act.GetRequiredService<IGeneratedCredentialOperations>();
            Assert.Null(await outputs.ConsumeAsync(consumer, PasswordSelector, default));
            Assert.Equal(DurableExecutionOutcome.Blocked, (await executor.ExecuteAsync(consumer, default)).Outcome);
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => executor.ExecuteAsync(producer, default).AsTask()));
            Assert.All(results, r => Assert.Equal(DurableExecutionOutcome.Succeeded, r.Outcome));
            output = (await outputs.ConsumeAsync(consumer, PasswordSelector, default))!;
            Assert.NotNull(output);
            bytes = await CredentialBytes(act, producer, output.Reference);
            Assert.Equal(0, boundary.Calls); // Only catalog persistence. No external target is registered for this action.
            await using var verify = OutputContext(scenario.Connection);
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Single(await verify.OperationOutputs.AsNoTracking().ToListAsync());
            Assert.Single(await verify.GeneratedCredentialCreationAudits.AsNoTracking().ToListAsync());
            var attempt = await verify.DurableOperationAttempts.AsNoTracking().SingleAsync();
            Assert.Equal("Succeeded", attempt.Outcome);
            Assert.Null(attempt.DispatchedAt);
            Assert.Equal(scenario.Plan.Fingerprint, (await verify.DeploymentGenerations.AsNoTracking().SingleAsync()).Fingerprint);
            Assert.Equal(DurableOperationGraphCodec.Encode(scenario.Plan.Graph),
                (await verify.DeploymentGenerations.AsNoTracking().SingleAsync()).GraphJson);
        }
        // Dump the current build with downstream work still pending; restore must preserve that distinction.
        var targetName = "output_restore_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{targetName}\"", admin).ExecuteNonQueryAsync();
        }
        var target = new NpgsqlConnectionStringBuilder(scenario.Connection) { Database = targetName }.ConnectionString;
        var dump = "/tmp/haby-outputs-" + Guid.NewGuid().ToString("N") + ".dump";
        try
        {
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump,
                "--dbname=" + new NpgsqlConnectionStringBuilder(scenario.Connection).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges",
                "--dbname=" + targetName, dump);
            await using var restored = Services(target, boundary);
            await restored.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await restored.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(producer, default)).Outcome);
            Assert.Equal(output, await restored.GetRequiredService<IGeneratedCredentialOperations>().ConsumeAsync(consumer, PasswordSelector, default));
            Assert.True(Enumerable.SequenceEqual(bytes, await CredentialBytes(restored, producer, output.Reference)), "Exact material must survive restore.");
            Assert.Equal(0, boundary.Calls);
            Assert.Equal(2, (await restored.GetRequiredService<DurableGenerationRunner>().RunAsync(producer.DeploymentId, producer.Generation, default)).Progress!.Completed);
            Assert.Equal(1, boundary.Calls); // Test-only downstream action, never production admission.
            await using var verify = OutputContext(target);
            Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Equal(1, await verify.OperationOutputs.CountAsync());
            Assert.Equal(1, await verify.GeneratedCredentialCreationAudits.CountAsync());
            Assert.Equal(1, await verify.DurableOperationAttempts.CountAsync(r => r.OperationId == producer.OperationId.Value));
        }
        finally { Array.Clear(bytes); await fixture.RunClientUtilityAsync("rm", "-f", dump); }
    }

    [Theory]
    [InlineData("publication")]
    [InlineData("attempt")]
    [InlineData("aggregate")]
    public async Task Failed_publication_or_finalization_rolls_back_output_success_and_attempt_then_reuses_slot(string mode)
    {
        var boundary = new QueueBoundary { Generated = true };
        var scenario = await GraphCase(boundary);
        var producer = MaterialKey(scenario.Commit);
        // Make the material node last for aggregate failure, using the real journal for the sibling.
        if (mode == "aggregate")
        {
            await using var prepare = Services(scenario.Connection, boundary);
            var journal = prepare.GetRequiredService<IDurableOperationJournal>();
            var other = ConsumerKey(scenario.Commit);
            var start = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(other, DateTimeOffset.UtcNow, default));
            Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(other, start.AttemptId, DateTimeOffset.UtcNow, default));
            Assert.True(await journal.TryRecordSuccessAsync(other, start.AttemptId, DateTimeOffset.UtcNow, default));
        }
        var constraint = mode switch
        {
            "publication" => "ALTER TABLE operation_outputs ADD CONSTRAINT test_output_failure CHECK (false)",
            "attempt" => "ALTER TABLE durable_create_operation_attempts ADD CONSTRAINT test_output_failure CHECK (outcome IS NULL)",
            _ => "ALTER TABLE application_deployments ADD CONSTRAINT test_output_failure CHECK (applied_generation IS NULL)"
        };
        if (mode == "attempt") constraint += " NOT VALID";
        await GraphSql(scenario.Connection, constraint);
        await using (var failed = Services(scenario.Connection, boundary))
            Assert.Equal(DurableExecutionOutcome.RetryableFailure,
                (await failed.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(producer, default)).Outcome);
        SecretRef reference;
        byte[] bytes;
        await using (var verify = OutputContext(scenario.Connection))
        {
            Assert.Equal(1, await verify.GeneratedCredentialSlots.CountAsync());
            Assert.Equal(0, await verify.OperationOutputs.CountAsync());
            var operation = await verify.DurableOperations.AsNoTracking().SingleAsync(r => r.OperationId == producer.OperationId.Value);
            Assert.Equal("Preparing", operation.State);
            Assert.Null(operation.FinalizedAt);
            var attempt = await verify.DurableOperationAttempts.AsNoTracking().SingleAsync(r => r.AttemptId == operation.CurrentAttemptId);
            Assert.Null(attempt.FinishedAt); Assert.Null(attempt.Outcome); Assert.Null(attempt.DispatchedAt);
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            var slot = await verify.GeneratedCredentialSlots.AsNoTracking().SingleAsync();
            reference = new(SecretId.Parse(slot.SecretId.ToString()), SecretVersionId.Parse(slot.VersionId.ToString()));
        }
        await using (var read = Services(scenario.Connection, boundary)) bytes = await CredentialBytes(read, producer, reference);
        await GraphSql(scenario.Connection, "ALTER TABLE " + (mode == "publication" ? "operation_outputs" :
            mode == "attempt" ? "durable_create_operation_attempts" : "application_deployments") + " DROP CONSTRAINT test_output_failure");
        await using (var restart = Services(scenario.Connection, boundary))
        {
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await restart.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(producer, default)).Outcome);
            Assert.True(Enumerable.SequenceEqual(bytes, await CredentialBytes(restart, producer, reference)), "Retry must retain exact material.");
            var output = await restart.GetRequiredService<IGeneratedCredentialOperations>().ConsumeAsync(ConsumerKey(scenario.Commit), PasswordSelector, default);
            Assert.Equal(reference, output!.Reference);
        }
        Array.Clear(bytes);
        await using var check = OutputContext(scenario.Connection);
        Assert.Equal(1, await check.GeneratedCredentialCreationAudits.CountAsync());
        Assert.Equal(1, await check.OperationOutputs.CountAsync());
        Assert.Equal(1, await check.DurableOperationAttempts.CountAsync(r => r.OperationId == producer.OperationId.Value && r.Outcome == "Succeeded"));
        Assert.Equal(1, await check.DurableOperationAttempts.CountAsync(r => r.OperationId == producer.OperationId.Value && r.Outcome == "AbandonedBeforeDispatch"));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Failed")]
    [InlineData("NeedsAttention")]
    public async Task Other_required_node_prevents_aggregate_completion(string state)
    {
        var boundary = new QueueBoundary { Generated = true, Mode = state == "Failed" ? "failure" : "uncertain" };
        var scenario = await GraphCase(boundary);
        if (state != "Pending")
        {
            await using var act = Services(scenario.Connection, boundary);
            var journal = act.GetRequiredService<IDurableOperationJournal>();
            var other = ConsumerKey(scenario.Commit);
            var begin = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(other, DateTimeOffset.UtcNow, default));
            if (state == "NeedsAttention") await journal.TryCommitDispatchMarkerAsync(other, begin.AttemptId, DateTimeOffset.UtcNow, default);
            Assert.True(await journal.TryFinalizeFailureAsync(other, begin.AttemptId, Enum.Parse<DurableOperationState>(state),
                state == "Failed" ? DurableOperationReason.InputInvalid : DurableOperationReason.UncertainAfterDispatch, DateTimeOffset.UtcNow, default));
        }
        await using (var act = Services(scenario.Connection, boundary))
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(MaterialKey(scenario.Commit), default)).Outcome);
        await using var verify = OutputContext(scenario.Connection);
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Fact]
    public async Task Storage_consumption_is_exact_and_owner_scoped_and_metadata_alone_is_insufficient()
    {
        var boundary = new QueueBoundary { Generated = true };
        var scenario = await GraphCase(boundary);
        var producer = MaterialKey(scenario.Commit);
        var consumer = ConsumerKey(scenario.Commit);
        await using var services = Services(scenario.Connection, boundary);
        var outputs = services.GetRequiredService<IGeneratedCredentialOperations>();
        var input = (await outputs.FindInputAsync(producer, default))!;
        var slots = services.GetRequiredService<IGeneratedCredentialSlots>();
        var result = await slots.GetOrCreateAsync(new(input.Owner, input.SlotId), input.GenerationProfile, default);
        var output = new GeneratedCredentialOutput(result.Reference!);
        // Direct privileged fixture insert simulates incomplete metadata; never sufficient to consume.
        await using (var arrange = OutputContext(scenario.Connection))
        {
            arrange.OperationOutputs.Add(new()
            {
                DeploymentId = Guid.Parse(producer.DeploymentId.ToString()),
                Generation = producer.Generation.Value,
                OperationId = producer.OperationId.Value,
                Contract = PasswordSelector.OutputContract,
                Key = PasswordSelector.OutputKey,
                Payload = GeneratedCredentialOutputCodec.Encode(output)
            });
            await arrange.SaveChangesAsync();
        }
        Assert.Null(await outputs.ConsumeAsync(consumer, PasswordSelector, default));
        Assert.Null(await outputs.ConsumeAsync(consumer with { Generation = DeploymentGeneration.Create(2) }, PasswordSelector, default));
        Assert.Null(await outputs.ConsumeAsync(consumer with { DeploymentId = ApplicationDeploymentId.New() }, PasswordSelector, default));
        Assert.Null(await outputs.ConsumeAsync(consumer, PasswordSelector with { OutputKey = "other" }, default));
        Assert.Null(await outputs.ConsumeAsync(consumer, PasswordSelector with { OutputContract = "other/v1" }, default));
        Assert.Null(await outputs.ConsumeAsync(consumer, PasswordSelector with { ProducerOperationId = consumer.OperationId }, default));
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(producer, default)).Outcome);
        Assert.Equal(output, await outputs.ConsumeAsync(consumer, PasswordSelector, default));
        Assert.True(await outputs.CompleteAsync(producer, Guid.NewGuid(), output, DateTimeOffset.UtcNow, default)); // identical accepted retry
        Assert.False(await outputs.CompleteAsync(producer, Guid.NewGuid(), new(new(SecretId.New(), SecretVersionId.New())), DateTimeOffset.UtcNow, default));

        // A genuine secret for a different Resource/Environment is still unauthorized.
        var otherEnvironment = await services.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("other"), default);
        foreach (var owner in new[] { input.Owner with { ResourceLocalId = ManifestLocalId.Create("other-user") },
            input.Owner with { EnvironmentRecordId = otherEnvironment } })
        {
            var foreign = await slots.GetOrCreateAsync(new(owner, input.SlotId), input.GenerationProfile, default);
            var payload = GeneratedCredentialOutputCodec.Encode(new(foreign.Reference!));
            await using var corrupt = OutputContext(scenario.Connection);
            await corrupt.OperationOutputs.ExecuteUpdateAsync(s => s.SetProperty(r => r.Payload, payload));
            Assert.Null(await outputs.ConsumeAsync(consumer, PasswordSelector, default));
            Assert.False(await outputs.CompleteAsync(producer, Guid.NewGuid(), new(foreign.Reference!), DateTimeOffset.UtcNow, default));
        }
        await using (var corrupt = OutputContext(scenario.Connection))
            await corrupt.OperationOutputs.ExecuteUpdateAsync(s => s.SetProperty(r => r.Payload, "{broken"));
        Assert.Null(await outputs.ConsumeAsync(consumer, PasswordSelector, default));
    }

    [Fact]
    public async Task Conflicting_publication_is_not_overwritten_and_generic_external_completion_is_refused()
    {
        var boundary = new QueueBoundary { Generated = true };
        var scenario = await GraphCase(boundary);
        var producer = MaterialKey(scenario.Commit);
        var conflicting = new GeneratedCredentialOutput(new(SecretId.New(), SecretVersionId.New()));
        await using (var arrange = OutputContext(scenario.Connection))
        {
            arrange.OperationOutputs.Add(new()
            {
                DeploymentId = Guid.Parse(producer.DeploymentId.ToString()),
                Generation = producer.Generation.Value,
                OperationId = producer.OperationId.Value,
                Contract = PasswordSelector.OutputContract,
                Key = PasswordSelector.OutputKey,
                Payload = GeneratedCredentialOutputCodec.Encode(conflicting)
            });
            await arrange.SaveChangesAsync();
        }
        await using var services = Services(scenario.Connection, boundary);
        var journal = services.GetRequiredService<IDurableOperationJournal>();
        var started = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(producer, DateTimeOffset.UtcNow, default));
        Assert.IsType<DurableMarkerResult.Rejected>(await journal.TryCommitDispatchMarkerAsync(producer, started.AttemptId, DateTimeOffset.UtcNow, default));
        Assert.False(await journal.TryRecordSuccessAsync(producer, started.AttemptId, DateTimeOffset.UtcNow, default));
        Assert.Equal(DurableExecutionOutcome.RetryableFailure, (await services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(producer, default)).Outcome);
        await using var verify = OutputContext(scenario.Connection);
        Assert.Equal(GeneratedCredentialOutputCodec.Encode(conflicting), (await verify.OperationOutputs.AsNoTracking().SingleAsync()).Payload);
        Assert.Equal(0, await verify.DurableOperations.CountAsync(r => r.State == "Succeeded"));
        var tracked = await verify.OperationOutputs.SingleAsync();
        tracked.Payload = "{}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => verify.SaveChangesAsync());
    }

    private sealed class OutputFactory(string connection, IInterceptor? interceptor = null, bool failReread = false)
        : IDbContextFactory<ManifestCatalogDbContext>
    {
        private int count;
        public ManifestCatalogDbContext CreateDbContext()
        {
            if (Interlocked.Increment(ref count) > 1 && failReread) throw new IOException("Private storage details.");
            var builder = new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection);
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return new(builder.Options);
        }
    }

    [Fact]
    public async Task Independent_attempts_converge_without_duplicate_success_or_creation()
    {
        var boundary = new QueueBoundary { Generated = true };
        var scenario = await GraphCase(boundary);
        var producer = MaterialKey(scenario.Commit);
        await using var services = Services(scenario.Connection, boundary);
        var journal = services.GetRequiredService<IDurableOperationJournal>();
        var outputs = services.GetRequiredService<IGeneratedCredentialOperations>();
        var input = (await outputs.FindInputAsync(producer, default))!;
        var material = await services.GetRequiredService<IGeneratedCredentialSlots>().GetOrCreateAsync(new(input.Owner, input.SlotId), input.GenerationProfile, default);
        var first = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(producer, DateTimeOffset.UtcNow, default));
        var second = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(producer, DateTimeOffset.UtcNow, default));
        var output = new GeneratedCredentialOutput(material.Reference!);
        var original = await CredentialBytes(services, producer, output.Reference);
        var left = new PostgreSqlGeneratedCredentialOperations(new OutputFactory(scenario.Connection), services.GetRequiredService<ISecretStore>());
        var right = new PostgreSqlGeneratedCredentialOperations(new OutputFactory(scenario.Connection), services.GetRequiredService<ISecretStore>());
        var results = await Task.WhenAll(left.CompleteAsync(producer, first.AttemptId, output, DateTimeOffset.UtcNow, default).AsTask(),
            right.CompleteAsync(producer, second.AttemptId, output, DateTimeOffset.UtcNow, default).AsTask());
        Assert.True(results[1]);
        Assert.True(await left.CompleteAsync(producer, first.AttemptId, output, DateTimeOffset.UtcNow, default));
        Assert.True(Enumerable.SequenceEqual(original, await CredentialBytes(services, producer, output.Reference)), "Concurrent attempts retain exact material.");
        Array.Clear(original);
        await using var verify = OutputContext(scenario.Connection);
        Assert.Equal(1, await verify.OperationOutputs.CountAsync());
        Assert.Equal(1, await verify.GeneratedCredentialCreationAudits.CountAsync());
        Assert.Equal(1, await verify.DurableOperationAttempts.CountAsync(r => r.Outcome == "Succeeded"));
        Assert.Equal(1, await verify.DurableOperationAttempts.CountAsync(r => r.Outcome == "AbandonedBeforeDispatch"));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("dependency")]
    [InlineData("owner")]
    public async Task Check_and_commit_reject_corrupt_frozen_output_inputs_without_accepting_partial_state(string mode)
    {
        var boundary = new QueueBoundary { Generated = true };
        await GraphCase(boundary, async (connection, plan) =>
        {
            var original = DurableOperationGraphCodec.Encode(plan.Graph);
            var document = System.Text.Json.Nodes.JsonNode.Parse(original)!;
            var nodes = document["Nodes"]!.AsArray();
            var producer = nodes.Single(n => n!["Id"]!.GetValue<string>() == "10-material")!;
            var consumer = nodes.Single(n => n!["Id"]!.GetValue<string>() == "20-dependent")!;
            if (mode == "key") consumer["Outputs"]![0]!["Key"] = "other";
            if (mode == "dependency") consumer["Dependencies"] = new System.Text.Json.Nodes.JsonArray();
            if (mode == "owner")
            {
                var input = System.Text.Json.Nodes.JsonNode.Parse(producer["Input"]!.GetValue<string>())!;
                input["Application"] = ApplicationRecordId.New().ToString();
                producer["Input"] = input.ToJsonString();
            }
            async Task Store(string json)
            {
                await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
                // Only disposable corruption fixtures bypass the previously merged immutable-plan trigger.
                await new NpgsqlCommand("SET session_replication_role = replica", db).ExecuteNonQueryAsync();
                await using var command = new NpgsqlCommand("UPDATE deployment_plans SET graph_json = @graph WHERE id = @id", db);
                command.Parameters.AddWithValue("graph", json); command.Parameters.AddWithValue("id", Guid.Parse(plan.Id.ToString()));
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            await Store(document.ToJsonString());
            await using (var act = Services(connection, boundary))
            {
                Assert.Equal(DurablePlanCurrentStatus.PlanInputUnavailable,
                    await act.GetRequiredService<IDurableDeploymentPlanner>().CheckPlanAsync(plan.Id, default));
                Assert.IsType<DurableCommitResult.PlanInputUnavailable>(await act.GetRequiredService<IDurableDesiredStateCommitter>()
                    .CommitAsync(plan.ChangeSetId, plan.Id, "tester", default));
            }
            await using (var verify = OutputContext(connection))
            {
                Assert.Equal(0, await verify.ApplicationDeployments.CountAsync());
                Assert.Equal(0, await verify.GeneratedCredentialSlots.CountAsync());
            }
            await Store(original);
        });
    }

    [Fact]
    public async Task Later_generation_storage_cannot_implicitly_consume_preceding_generation_output()
    {
        var boundary = new QueueBoundary { Generated = true };
        var scenario = await GraphCase(boundary);
        await using var services = Services(scenario.Connection, boundary);
        var producer = MaterialKey(scenario.Commit);
        var outputs = services.GetRequiredService<IGeneratedCredentialOperations>();
        Assert.Equal(DurableExecutionOutcome.Succeeded,
            (await services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(producer, default)).Outcome);
        var accepted = await outputs.ConsumeAsync(ConsumerKey(scenario.Commit), PasswordSelector, default);
        Assert.NotNull(accepted);
        // Generation-2 admission is deliberately unavailable. Relax only this fixture's CHECK
        // and clone accepted identities to test the output storage key, as in existing journal tests.
        await GraphSql(scenario.Connection, """
            ALTER TABLE application_deployment_generations DROP CONSTRAINT "CK_deployment_generations_first";
            INSERT INTO application_deployment_generations
                (deployment_id, generation, presence, plan_id, change_set_id, application_record_id, revision_number,
                 environment_record_id, environment_key, provider_alias, provider_instance_id, configuration_revision_number,
                 implementation_id, fingerprint, graph_json, accepted_at, actor_id)
            SELECT deployment_id, 2, presence, plan_id, change_set_id, application_record_id, revision_number,
                 environment_record_id, environment_key, provider_alias, provider_instance_id, configuration_revision_number,
                 implementation_id, fingerprint, graph_json, accepted_at, actor_id
            FROM application_deployment_generations WHERE generation = 1;
            """);
        var later = ConsumerKey(scenario.Commit) with { Generation = DeploymentGeneration.Create(2) };
        Assert.NotNull(await outputs.FindInputAsync(producer with { Generation = later.Generation }, default));
        Assert.Null(await outputs.ConsumeAsync(later, PasswordSelector, default));
        Assert.Equal(accepted, await outputs.ConsumeAsync(ConsumerKey(scenario.Commit), PasswordSelector, default));
        await using var verify = OutputContext(scenario.Connection);
        Assert.Equal(1, await verify.OperationOutputs.CountAsync());
        Assert.Equal(1, (await verify.OperationOutputs.AsNoTracking().SingleAsync()).Generation);
    }
    private sealed class OutputLostCommit : DbTransactionInterceptor
    {
        public bool Thrown;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!Thrown) { Thrown = true; throw new IOException("Private lost response."); }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Publication_and_scoped_resolution_share_a_single_catalog_connection()
    {
        var boundary = new QueueBoundary { Generated = true };
        var scenario = await GraphCase(boundary);
        var connection = new NpgsqlConnectionStringBuilder(scenario.Connection)
        { Pooling = true, MaxPoolSize = 1, Timeout = 5 }.ConnectionString;
        await using var services = Services(connection, boundary);
        var producer = MaterialKey(scenario.Commit);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Assert.Equal(DurableExecutionOutcome.Succeeded,
            (await services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(producer, timeout.Token)).Outcome);
        Assert.NotNull(await services.GetRequiredService<IGeneratedCredentialOperations>()
            .ConsumeAsync(ConsumerKey(scenario.Commit), PasswordSelector, timeout.Token));
        using var pool = new NpgsqlConnection(connection);
        NpgsqlConnection.ClearPool(pool);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambiguous_commit_rereads_success_or_reports_retryable_then_restart_converges(bool failReread)
    {
        var boundary = new QueueBoundary { Generated = true };
        var scenario = await GraphCase(boundary);
        var producer = MaterialKey(scenario.Commit);
        await using var services = Services(scenario.Connection, boundary);
        var journal = services.GetRequiredService<IDurableOperationJournal>();
        var ordinary = services.GetRequiredService<IGeneratedCredentialOperations>();
        var input = (await ordinary.FindInputAsync(producer, default))!;
        var slot = await services.GetRequiredService<IGeneratedCredentialSlots>().GetOrCreateAsync(new(input.Owner, input.SlotId), input.GenerationProfile, default);
        var output = new GeneratedCredentialOutput(slot.Reference!);
        var original = await CredentialBytes(services, producer, output.Reference);
        var started = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(producer, DateTimeOffset.UtcNow, default));
        var lost = new OutputLostCommit();
        var outputs = new PostgreSqlGeneratedCredentialOperations(new OutputFactory(scenario.Connection, lost, failReread),
            services.GetRequiredService<ISecretStore>());
        Assert.Equal(!failReread, await outputs.CompleteAsync(producer, started.AttemptId, output, DateTimeOffset.UtcNow, default));
        Assert.True(lost.Thrown);
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(producer, default)).Outcome);
        Assert.Equal(output, await ordinary.ConsumeAsync(ConsumerKey(scenario.Commit), PasswordSelector, default));
        Assert.True(Enumerable.SequenceEqual(original, await CredentialBytes(services, producer, output.Reference)), "Ambiguous commit retains exact material.");
        Array.Clear(original);
        await using var verify = OutputContext(scenario.Connection);
        Assert.Equal(1, await verify.OperationOutputs.CountAsync());
        Assert.Equal(1, await verify.GeneratedCredentialCreationAudits.CountAsync());
        Assert.Equal(1, await verify.DurableOperationAttempts.CountAsync());
    }
}
