using System.Data.Common;
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

public abstract partial class ApplicationLoginScenario
{
    private protected sealed class ReadinessMaterialTrap : DbCommandInterceptor
    {
        public int MaterialSelects;
        public int LockedMaterialSelects;
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader reader, CancellationToken token = default)
        {
            if (command.CommandText.Contains("secret_payloads", StringComparison.Ordinal) &&
                Enumerable.Range(0, reader.FieldCount).Any(i => reader.GetName(i) == "bytes"))
            {
                MaterialSelects++;
                if (command.Transaction is not null) LockedMaterialSelects++;
            }
            return ValueTask.FromResult(reader);
        }
    }
    private protected sealed class ReadinessFactory(string connection, ReadinessMaterialTrap trap) : IDbContextFactory<ManifestCatalogDbContext>
    {
        public ManifestCatalogDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>()
            .UseNpgsql(connection).AddInterceptors(trap).Options);
    }
    private protected sealed class ReadinessSecrets(ISecretStore inner) : ISecretStore
    {
        public int Resolves;
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition d, SecretRef r, CancellationToken t) { Resolves++; return inner.ResolveAsync(d, r, t); }
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition d, SecretRef r, int n, CancellationToken t) { Resolves++; return inner.ResolveBoundedAsync(d, r, n, t); }
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId e, CancellationToken t) => inner.CreateEnvironmentAsync(e, t);
        public ValueTask CreateAsync(SecretDefinition d, SecretRef r, byte[] b, CancellationToken t) => inner.CreateAsync(d, r, b, t);
        public ValueTask AddVersionAsync(EnvironmentRecordId e, SecretRef r, byte[] b, CancellationToken t) => inner.AddVersionAsync(e, r, b, t);
    }
    private protected sealed class MismatchedDatabaseResult : IExternalOperationOutputContract
    {
        public DurableActionExecutionKey Producer => PostgreSqlDatabaseCreateAction.RegistrationKey;
        public ExternalOperationOutputIdentity Identity => new("unsupported.database-result/v99", PostgreSqlDatabaseOutputContract.Key);
        public bool Validate(DurableOperationNode node, ExternalOperationOutput output) => false;
    }
    private protected static async Task<string> ReadinessHistory(string connection)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object(
              'operations', (SELECT jsonb_agg(to_jsonb(x) ORDER BY operation_id) FROM durable_create_operations x),
              'attempts', (SELECT jsonb_agg(to_jsonb(x) ORDER BY attempt_id) FROM durable_create_operation_attempts x),
              'outputs', (SELECT jsonb_agg(to_jsonb(x) ORDER BY operation_id, output_contract, output_key) FROM operation_outputs x),
              'slots', (SELECT jsonb_agg(to_jsonb(x) ORDER BY resource_local_id, slot_id) FROM generated_credential_slots x),
              'audits', (SELECT jsonb_agg(to_jsonb(x) ORDER BY secret_id) FROM generated_credential_creation_audits x))::text
            """, db);
        return (string)(await command.ExecuteScalarAsync())!;
    }

}

public sealed partial class ApplicationGenerationTests
{
    [Theory]
    [InlineData("missing-validator")]
    [InlineData("mismatched-validator")]
    [InlineData("ambiguous-validator")]
    [InlineData("invalid-output")]
    [InlineData("foreign-credential-ref")]
    [InlineData("missing-material")]
    [InlineData("corrupt-material")]
    [InlineData("valid")]
    public async Task All_succeeded_pair_resume_preflights_later_resource_before_material_and_preserves_confirmed_results(string mode)
    {
        var scenario = await Prepare(full: true);
        await AccessReady(scenario);
        await Sql(scenario.Connection, "ALTER TABLE generation_stage_checkpoints ADD CONSTRAINT test_block_checkpoint CHECK (false)");
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await AccessExecute(scenario)).Outcome);
        await Sql(scenario.Connection, "ALTER TABLE generation_stage_checkpoints DROP CONSTRAINT test_block_checkpoint");
        if (mode == "invalid-output") await Sql(scenario.Connection, "SET session_replication_role = replica; UPDATE operation_outputs SET output_key = 'wrong' WHERE operation_id = 'db-create'");
        if (mode == "missing-material") await Sql(scenario.Connection, "SET session_replication_role = replica; DELETE FROM secret_payloads WHERE secret_version_id IN (SELECT version_id FROM generated_credential_slots)");
        if (mode == "corrupt-material") await Sql(scenario.Connection, "SET session_replication_role = replica; UPDATE secret_payloads SET bytes = decode(repeat('00', 43), 'hex') WHERE secret_version_id IN (SELECT version_id FROM generated_credential_slots)");
        if (mode == "foreign-credential-ref")
        {
            var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
            var connection = (await ConnectionOutput(scenario)) with { Password = reference };
            await using var db = new NpgsqlConnection(scenario.Connection); await db.OpenAsync();
            await new NpgsqlCommand("SET session_replication_role = replica", db).ExecuteNonQueryAsync();
            await using var change = new NpgsqlCommand("UPDATE operation_outputs SET payload = CASE WHEN operation_id = 'app-login-material' THEN $1 ELSE $2 END WHERE operation_id IN ('app-login-material', 'app-login-access')", db);
            change.Parameters.AddWithValue(GeneratedCredentialOutputCodec.Encode(new(reference)));
            change.Parameters.AddWithValue(PostgreSqlApplicationConnectionContract.Encode(connection).Payload);
            Assert.Equal(2, await change.ExecuteNonQueryAsync());
        }
        var before = await ReuseHistory(scenario.Connection);
        var exactHistory = await ReadinessHistory(scenario.Connection);
        var trap = new ReadinessMaterialTrap();
        var factory = new ReadinessFactory(scenario.Connection, trap);
        var secrets = new ReadinessSecrets(new PostgreSqlPlaintextSecretStore(factory));
        await using var services = Services(scenario.Connection, full: true, access: true, queryTrap: trap, secretStore: secrets);
        var contracts = services.GetServices<IExternalOperationOutputContract>().ToArray();
        var database = Assert.Single(contracts, x => x.Producer == PostgreSqlDatabaseCreateAction.RegistrationKey);
        if (mode == "missing-validator") contracts = contracts.Except([database]).ToArray();
        if (mode == "mismatched-validator") contracts = contracts.Except([database]).Append(new MismatchedDatabaseResult()).ToArray();
        if (mode == "ambiguous-validator") contracts = contracts.Append(database).ToArray();
        var journal = new PostgreSqlDurableOperationJournal(factory, contracts, services.GetServices<IProviderConfigurationCodec>());
        var runner = new DurableGenerationRunner(services.GetRequiredService<ExactDurableOperationExecutor>(), journal,
            services.GetRequiredService<DurableOperationSerialization>(), services.GetRequiredService<DurableActionAdmission>());
        var result = await runner.RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
        Assert.Equal(before, await ReuseHistory(scenario.Connection));
        Assert.Equal(exactHistory, await ReadinessHistory(scenario.Connection));
        Assert.Equal(5, result.Progress!.Completed);
        if (mode is "missing-validator" or "mismatched-validator" or "ambiguous-validator" or "invalid-output" or "foreign-credential-ref" or "missing-material")
        { Assert.Equal(0, trap.MaterialSelects); Assert.Equal(0, secrets.Resolves); }
        else Assert.True(trap.MaterialSelects > 0, "Valid metadata must reach the necessary current material check.");
        Assert.Equal(0, trap.LockedMaterialSelects);
        await using var verify = Context(scenario.Connection);
        if (mode == "valid")
        { Assert.Null(result.StopOutcome); Assert.Equal(1, result.Progress.AppliedGeneration); Assert.Single(await verify.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync()); }
        else
        { Assert.Equal(DurableExecutionOutcome.InputUnavailable, result.StopOutcome); Assert.Null(result.Progress.AppliedGeneration); Assert.Empty(await verify.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync()); }
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("stale")]
    [InlineData("finished")]
    [InlineData("valid")]
    [InlineData("replay")]
    [InlineData("wrong-replay")]
    public async Task Generated_candidate_checks_exact_attempt_before_material_and_exact_publication_replay_is_read_only(string mode)
    {
        var scenario = await Prepare(full: true);
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await AccessExecute(scenario, "db-create")).Outcome);
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await AccessExecute(scenario, "db-restrict")).Outcome);
        var producer = scenario.Key("app-login-material");
        GeneratedCredentialOutput output;
        var expected = Guid.CreateVersion7();
        await using (var arrange = OwnerServices(scenario))
        {
            var operations = arrange.GetRequiredService<IGeneratedCredentialOperations>();
            var input = (await operations.FindInputAsync(producer, default))!;
            var slot = await arrange.GetRequiredService<IGeneratedCredentialSlots>().GetOrCreateAsync(new(input.Owner, input.SlotId), input.GenerationProfile, default);
            output = new(slot.Reference!);
            var journal = arrange.GetRequiredService<IDurableOperationJournal>();
            if (mode != "missing")
            {
                var begin = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(producer, DateTimeOffset.UtcNow, default));
                expected = mode == "wrong" ? Guid.CreateVersion7() : begin.AttemptId;
                if (mode == "stale") Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(producer, DateTimeOffset.UtcNow, default));
                if (mode == "finished") Assert.True(await journal.TryCloseAttemptBeforeDispatchAsync(producer, begin.AttemptId,
                    DurableAttemptOutcome.CanceledBeforeDispatch, null, DateTimeOffset.UtcNow, default));
                if (mode is "replay" or "wrong-replay")
                {
                    Assert.True(await operations.CompleteAsync(producer, begin.AttemptId, output, DateTimeOffset.UtcNow, default));
                    if (mode == "wrong-replay") expected = Guid.CreateVersion7();
                }
            }
        }
        var before = await ReadinessHistory(scenario.Connection);
        var trap = new ReadinessMaterialTrap();
        var factory = new ReadinessFactory(scenario.Connection, trap);
        var secrets = new ReadinessSecrets(new PostgreSqlPlaintextSecretStore(factory));
        await using var services = Services(scenario.Connection, full: true, access: true, queryTrap: trap, secretStore: secrets);
        var operationsUnderTest = new PostgreSqlGeneratedCredentialOperations(factory, secrets,
            services.GetServices<IExternalOperationOutputContract>(), services.GetServices<IProviderConfigurationCodec>());
        var result = await operationsUnderTest.CompleteAsync(producer, expected, output, DateTimeOffset.UtcNow, default);
        Assert.Equal(mode is "valid" or "replay", result);
        if (mode == "valid")
        {
            Assert.True(trap.MaterialSelects > 0); Assert.True(secrets.Resolves > 0);
            await using var verify = Context(scenario.Connection);
            Assert.Equal("Succeeded", (await verify.DurableOperations.AsNoTracking().SingleAsync(x => x.OperationId == producer.OperationId.Value)).State);
            Assert.Equal(expected, (await verify.DurableOperations.AsNoTracking().SingleAsync(x => x.OperationId == producer.OperationId.Value)).CurrentAttemptId);
        }
        else
        {
            Assert.Equal(0, trap.MaterialSelects); Assert.Equal(0, secrets.Resolves);
            Assert.Equal(before, await ReadinessHistory(scenario.Connection));
        }
        Assert.Equal(0, trap.LockedMaterialSelects);
        await using var final = Context(scenario.Connection);
        Assert.Empty(await final.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
        Assert.Null((await final.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

}
