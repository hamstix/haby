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

public sealed partial class PostgreSqlRestrictTests
{
    private static async Task Sql(string connection, string sql)
    {
        await using var database = new NpgsqlConnection(connection); await database.OpenAsync();
        await new NpgsqlCommand(sql, database).ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("output")]
    [InlineData("attempt")]
    public async Task Confirmed_CREATE_publication_failure_rolls_back_output_success_and_attempt_without_replay(string mode)
    {
        var scenario = await Prepare(standalone: true);
        var table = mode == "output" ? "operation_outputs" : "durable_create_operation_attempts";
        var condition = mode == "output" ? "false" : "outcome IS NULL";
        await Sql(scenario.Connection, $"ALTER TABLE {table} ADD CONSTRAINT test_publication_failure CHECK ({condition})");
        var result = await Execute(scenario, false);
        Assert.Equal(DurableExecutionOutcome.DispatchUncertain, result.Outcome);
        var external = await Policy(scenario.Database); // CREATE really committed; never synthesize success from existence.
        await using (var verify = Context(scenario.Connection))
        {
            Assert.Empty(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
            Assert.Equal("Dispatching", (await verify.DurableOperations.AsNoTracking().SingleAsync()).State);
            Assert.Null((await verify.DurableOperationAttempts.AsNoTracking().SingleAsync()).Outcome);
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        await Sql(scenario.Connection, $"ALTER TABLE {table} DROP CONSTRAINT test_publication_failure");
        Assert.Equal(DurableExecutionOutcome.NeedsAttention, (await Execute(scenario, false)).Outcome);
        Assert.Equal(DurableExecutionOutcome.NeedsAttention, (await Execute(scenario, false)).Outcome);
        Assert.Equal(external, await Policy(scenario.Database));
        await using var final = Context(scenario.Connection);
        Assert.Empty(await final.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Single(await final.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
        Assert.Equal("Uncertain", (await final.DurableOperationAttempts.AsNoTracking().SingleAsync()).Outcome);
    }

    [Fact]
    public async Task Confirmed_CREATE_aggregate_failure_preserves_exact_output_and_resumes_without_replay()
    {
        var scenario = await Prepare(standalone: true);
        await Sql(scenario.Connection, "ALTER TABLE application_deployments ADD CONSTRAINT test_publication_failure CHECK (applied_generation IS NULL)");
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, false)).Outcome);
        var external = await Policy(scenario.Database);
        string payload;
        await using (var verify = Context(scenario.Connection))
        {
            payload = (await verify.OperationOutputs.AsNoTracking().SingleAsync()).Payload;
            Assert.Equal("Succeeded", (await verify.DurableOperations.AsNoTracking().SingleAsync()).State);
            Assert.Equal("Succeeded", (await verify.DurableOperationAttempts.AsNoTracking().SingleAsync()).Outcome);
            Assert.Empty(await verify.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        await Sql(scenario.Connection, "ALTER TABLE application_deployments DROP CONSTRAINT test_publication_failure");
        await using (var restart = Services(scenario.Connection, standalone: true))
            Assert.Null((await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Create.DeploymentId, scenario.Create.Generation, default)).StopOutcome);
        Assert.Equal(external, await Policy(scenario.Database));
        await using var final = Context(scenario.Connection);
        Assert.Equal(payload, (await final.OperationOutputs.AsNoTracking().SingleAsync()).Payload);
        Assert.Single(await final.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
        Assert.Single(await final.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, (await final.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Fact]
    public async Task Restrict_catalog_aggregate_failure_preserves_confirmed_ACL_and_never_replays_SQL()
    {
        var scenario = await Prepare(); Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, false)).Outcome);
        await Sql(scenario.Connection, "ALTER TABLE application_deployments ADD CONSTRAINT test_restrict_failure CHECK (applied_generation IS NULL)");
        var fault = new Fault("none");
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, true, fault)).Outcome);
        await AssertPublicDenied(scenario.Database);
        await using (var blocked = Context(scenario.Connection))
        {
            Assert.Null((await blocked.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Empty(await blocked.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
            Assert.Equal("Succeeded", (await blocked.DurableOperationAttempts.AsNoTracking().SingleAsync(r => r.OperationId == "db-restrict")).Outcome);
        }
        await Sql(scenario.Connection, "ALTER TABLE application_deployments DROP CONSTRAINT test_restrict_failure");
        await using (var restart = Services(scenario.Connection))
            Assert.Null((await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Create.DeploymentId, scenario.Create.Generation, default)).StopOutcome);
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, true, fault)).Outcome);
        Assert.Equal(1, fault.Dispatches);
        await AssertPublicDenied(scenario.Database);
        await using var verify = Context(scenario.Connection);
        Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Single(await verify.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
        Assert.Equal("Succeeded", (await verify.DurableOperationAttempts.AsNoTracking().SingleAsync(r => r.OperationId == "db-restrict")).Outcome);
    }
    private sealed class LostPublication : DbTransactionInterceptor
    {
        public bool Thrown;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Thrown = true; throw new IOException("Injected lost catalog commit response.");
        }
    }
    private sealed class PublicationFactory(string connection, LostPublication lost) : IDbContextFactory<ManifestCatalogDbContext>
    {
        private int calls;
        public ManifestCatalogDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>()
            .UseNpgsql(connection).AddInterceptors(Interlocked.Increment(ref calls) == 1 ? [lost] : []).Options);
    }

    [Fact]
    public async Task Exact_publication_is_write_once_attempt_scoped_and_can_confirm_a_lost_catalog_commit_response()
    {
        var scenario = await Prepare();
        await using var services = Services(scenario.Connection);
        var journal = services.GetRequiredService<IDurableOperationJournal>();
        var started = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(scenario.Create, DateTimeOffset.UtcNow, default));
        var operation = started.Operation;
        Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(scenario.Create, started.AttemptId, DateTimeOffset.UtcNow, default));
        var input = new PostgreSqlDatabaseCreateInput(operation.Environment, operation.ProviderInstance, operation.ConfigurationRevision, operation.ImplementationId, scenario.Database);
        var target = services.GetRequiredService<IPostgreSqlDatabaseCreateTarget>();
        Assert.Equal(PostgreSqlDatabaseCreateDispatchOutcome.Created, await target.DispatchCreateAsync(input, default));
        var evidence = (await target.ReadDatabaseEvidenceAsync(input, default))!;
        var output = PostgreSqlDatabaseOutputContract.Encode(evidence);
        // Ordinary journal success cannot bypass the mandatory publication.
        Assert.False(await journal.TryRecordSuccessAsync(scenario.Create, started.AttemptId, DateTimeOffset.UtcNow, default));
        var lost = new LostPublication();
        var store = new PostgreSqlExternalOperationOutputs(new PublicationFactory(scenario.Connection, lost), [new PostgreSqlDatabaseOutputContract()], services.GetServices<IProviderConfigurationCodec>());
        Assert.True(await store.CompleteAsync(scenario.Create, started.AttemptId, output, DateTimeOffset.UtcNow, default)); Assert.True(lost.Thrown);
        Assert.True(await store.CompleteAsync(scenario.Create, started.AttemptId, output, DateTimeOffset.UtcNow, default));
        Assert.False(await store.CompleteAsync(scenario.Create, Guid.NewGuid(), output, DateTimeOffset.UtcNow, default));
        Assert.False(await store.CompleteAsync(scenario.Create, started.AttemptId,
            PostgreSqlDatabaseOutputContract.Encode(evidence with { DatabaseOid = evidence.DatabaseOid + 1 }), DateTimeOffset.UtcNow, default));
        Assert.Equal(output, await store.ConsumeAsync(scenario.Restrict, Selector, default));
        Assert.Null(await store.ConsumeAsync(scenario.Restrict with { Generation = DeploymentGeneration.Create(2) }, Selector, default));
        Assert.Null(await store.ConsumeAsync(scenario.Restrict with { DeploymentId = ApplicationDeploymentId.New() }, Selector, default));
        foreach (var selector in new[] { Selector with { ProducerOperationId = scenario.Restrict.OperationId },
            Selector with { OutputContract = "other/v1" }, Selector with { OutputKey = "other" } })
            Assert.Null(await store.ConsumeAsync(scenario.Restrict, selector, default));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("contract")]
    [InlineData("key")]
    [InlineData("name")]
    [InlineData("oid")]
    [InlineData("type")]
    [InlineData("producer-state")]
    [InlineData("attempt")]
    public async Task Missing_or_invalid_exact_producer_evidence_refuses_before_dispatch(string mode)
    {
        var scenario = await Prepare(); Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, false)).Outcome);
        var before = await Policy(scenario.Database);
        // Privileged corruption only in this disposable fixture, never an application repair path.
        var corruption = mode switch
        {
            "missing" => "DELETE FROM operation_outputs",
            "contract" => "UPDATE operation_outputs SET output_contract = 'other/v1'",
            "key" => "UPDATE operation_outputs SET output_key = 'other'",
            "name" => "UPDATE operation_outputs SET payload = '{\"databaseName\":\"different\",\"databaseOid\":42}'",
            "oid" => "UPDATE operation_outputs SET payload = replace(payload, substring(payload from '\"databaseOid\":([0-9]+)'), '42')",
            "type" => "UPDATE operation_outputs SET payload = '{\"databaseName\":42,\"databaseOid\":42}'",
            "producer-state" => "UPDATE durable_create_operations SET state = 'NeedsAttention', reason_code = 'UncertainAfterDispatch' WHERE operation_id = 'db-create'",
            _ => "UPDATE durable_create_operation_attempts SET outcome = 'Uncertain' WHERE operation_id = 'db-create'"
        };
        await Sql(scenario.Connection, "SET session_replication_role = replica; " + corruption);
        var fault = new Fault("trap");
        var result = await Execute(scenario, true, fault);
        Assert.Contains(result.Outcome, new[] { DurableExecutionOutcome.InputUnavailable, DurableExecutionOutcome.Failed, DurableExecutionOutcome.Blocked });
        Assert.Equal(0, fault.Dispatches); Assert.Equal(before, await Policy(scenario.Database));
        await using var verify = Context(scenario.Connection);
        Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().Where(r => r.OperationId == "db-restrict" && r.DispatchedAt != null).ToArrayAsync());
    }

    [Fact]
    public async Task Alias_switch_to_unusable_revision_does_not_redirect_create_or_restrict()
    {
        var scenario = await Prepare();
        await using (var arrange = Services(scenario.Connection))
        {
            var operation = (await arrange.GetRequiredService<IDurableOperationJournal>().FindAsync(scenario.Create, default))!;
            var providers = arrange.GetRequiredService<IProviderConfigurationStore>();
            var pinned = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(operation.Environment, operation.ProviderInstance, operation.ConfigurationRevision, default))!.Configuration;
            var next = ProviderConfigurationRevisionNumber.Create(2);
            var alternate = new SecretRef(SecretId.New(), SecretVersionId.New());
            var secrets = arrange.GetRequiredService<ISecretStore>();
            using (var material = await secrets.ResolveAsync(new(new SecretOwner.Environment(operation.Environment), SecretPurpose.ProviderAdministrator), pinned.AdministratorPassword, default))
            {
                var bytes = material!.CopyBytes();
                try { await secrets.CreateAsync(new(new SecretOwner.Environment(operation.Environment), SecretPurpose.ProviderAdministrator), alternate, bytes, default); }
                finally { Array.Clear(bytes); }
            }
            await providers.AddRevisionAsync(operation.Environment, operation.ProviderInstance, next,
                new PostgreSqlProviderConfiguration(1, pinned.Host, pinned.Port, new NpgsqlConnectionStringBuilder(scenario.Connection).Database!,
                    pinned.AdministratorUserName, alternate, pinned.ImplementationId), default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await arrange.GetRequiredService<IProviderAliasStore>().ChangeAsync(operation.Environment,
                ProviderAlias.Create("postgresql.primary"), new(operation.ProviderInstance, operation.ConfigurationRevision),
                new(operation.ProviderInstance, next), "tester", default)).Outcome);
            // Make only the new alias's credential unavailable after the real checked switch.
            await Sql(scenario.Connection, "SET session_replication_role = replica; DELETE FROM secret_payloads WHERE secret_version_id = '" + alternate.SecretVersionId + "'");
        }
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, false)).Outcome);
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, true)).Outcome);
        await AssertPublicDenied(scenario.Database);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("template")]
    [InlineData("secret")]
    [InlineData("configuration")]
    public async Task Unavailable_pinned_inputs_or_external_identity_conflict_never_cross_the_marker(string mode)
    {
        var scenario = await Prepare(); Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, false)).Outcome);
        if (mode == "owner")
        {
            var owner = "owner_" + Guid.NewGuid().ToString("N");
            await Sql(fixture.ConnectionString, $"CREATE ROLE \"{owner}\" NOLOGIN; ALTER DATABASE \"{scenario.Database}\" OWNER TO \"{owner}\"");
        }
        else if (mode == "template") await Sql(fixture.ConnectionString, $"ALTER DATABASE \"{scenario.Database}\" IS_TEMPLATE true");
        else await Sql(scenario.Connection, "SET session_replication_role = replica; " + (mode == "secret"
            ? "DELETE FROM secret_payloads" : "DELETE FROM provider_configuration_revisions"));
        var before = await Policy(scenario.Database); var fault = new Fault("trap");
        var result = await Execute(scenario, true, fault);
        Assert.Contains(result.Outcome, new[] { DurableExecutionOutcome.Failed, DurableExecutionOutcome.RetryableFailure, DurableExecutionOutcome.InputUnavailable });
        Assert.Equal(0, fault.Dispatches); Assert.Equal(before, await Policy(scenario.Database));
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("environment")]
    [InlineData("provider")]
    [InlineData("implementation")]
    [InlineData("expected-owner")]
    [InlineData("selector")]
    [InlineData("producer")]
    public async Task Tampered_committed_relationships_refuse_even_with_recomputed_fingerprint(string mode)
    {
        var scenario = await Prepare(); Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, false)).Outcome);
        await using (var read = Services(scenario.Connection))
        {
            var accepted = (await read.GetRequiredService<IExternalOperationOutputs>().FindAcceptedAsync(scenario.Restrict, default))!;
            var node = accepted.Node;
            var altered = mode switch
            {
                "resource" => node with { ResourceId = ManifestLocalId.Create("other") },
                "environment" => node with { References = node.References with { Environment = EnvironmentRecordId.New() } },
                "provider" => node with { References = node.References with { ProviderInstance = ProviderInstanceId.New() } },
                "implementation" => node with { References = node.References with { ImplementationId = "different/v1" } },
                "expected-owner" => node with { Input = node.Input with { Json = node.Input.Json.Replace("\"expectedOwnerUserName\":\"postgres\"", "\"expectedOwnerUserName\":\"other\"", StringComparison.Ordinal) } },
                "selector" => node with { OutputReferences = [Selector with { OutputKey = "other" }] },
                _ => node with { Input = node.Input with { Json = node.Input.Json.Replace("\"producerOperationId\":\"db-create\"", "\"producerOperationId\":\"db-restrict\"", StringComparison.Ordinal) } }
            };
            var graph = new DurableOperationGraph(accepted.Graph.Nodes.Select(n => n.Id == node.Id ? altered :
                mode is "environment" or "provider" or "implementation" ? n with { References = altered.References } : n));
            if (mode is "environment" or "provider" or "implementation")
            {
                // Pinned-reference disagreement cannot even acquire a valid fingerprint.
                Assert.Throws<InvalidDataException>(() => DurableOperationGraphCodec.Fingerprint(accepted.Inputs, graph));
                Assert.Empty((await read.GetRequiredService<IDurableOperationJournal>().FindAttemptsAsync(scenario.Restrict, default)));
                return;
            }
            if (mode == "resource")
                Assert.Throws<InvalidDataException>(() => DurableOperationGraphCodec.Fingerprint(accepted.Inputs, graph));
            await using var connection = new NpgsqlConnection(scenario.Connection); await connection.OpenAsync();
            await new NpgsqlCommand("SET session_replication_role = replica", connection).ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand("UPDATE application_deployment_generations SET graph_json = $1, fingerprint = $2", connection);
            command.Parameters.AddWithValue(DurableOperationGraphCodec.Encode(graph));
            // An undeclared owner has no valid fingerprint. Persist the corrupt graph
            // with the original fingerprint and retain the dispatch/effect refusal oracle.
            command.Parameters.AddWithValue(DurableOperationGraphCodec.Fingerprint(accepted.Inputs, mode == "resource" ? accepted.Graph : graph));
            await command.ExecuteNonQueryAsync();
        }
        var before = await Policy(scenario.Database); var fault = new Fault("trap");
        if (mode == "resource")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => Execute(scenario, true, fault));
            await using var verify = Services(scenario.Connection);
            Assert.Empty(await verify.GetRequiredService<IDurableOperationJournal>().FindAttemptsAsync(scenario.Restrict, default));
        }
        else
            Assert.Equal(DurableExecutionOutcome.InputUnavailable, (await Execute(scenario, true, fault)).Outcome);
        Assert.Equal(0, fault.Dispatches); Assert.Equal(before, await Policy(scenario.Database));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("success")]
    [InlineData("uncertain")]
    public async Task Current_build_pg_dump_restore_retains_exact_output_and_operation_attempt_states_without_replay(string state)
    {
        var scenario = await Prepare(); Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(scenario, false)).Outcome);
        if (state != "pending") Assert.Equal(state == "success" ? DurableExecutionOutcome.Succeeded : DurableExecutionOutcome.NeedsAttention,
            (await Execute(scenario, true, state == "success" ? null : new Fault("commit"))).Outcome);
        var policy = await Policy(scenario.Database);
        var restored = await Database("restrict_restore_");
        var dump = "/tmp/restrict-" + Guid.NewGuid().ToString("N") + ".dump";
        try
        {
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + new NpgsqlConnectionStringBuilder(scenario.Connection).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
            var restoredScenario = scenario with { Connection = restored };
            await using (var startup = Services(restored))
                await startup.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            await using (var source = Context(scenario.Connection))
            await using (var target = Context(restored))
            {
                Assert.Equal((await source.OperationOutputs.AsNoTracking().SingleAsync()).Payload, (await target.OperationOutputs.AsNoTracking().SingleAsync()).Payload);
                Assert.Equal((await source.DeploymentGenerations.AsNoTracking().SingleAsync()).GraphJson, (await target.DeploymentGenerations.AsNoTracking().SingleAsync()).GraphJson);
                Assert.Equal(await source.DurableOperationAttempts.AsNoTracking().OrderBy(a => a.OperationId).Select(a => a.Outcome).ToArrayAsync(),
                    await target.DurableOperationAttempts.AsNoTracking().OrderBy(a => a.OperationId).Select(a => a.Outcome).ToArrayAsync());
            }
            var fault = new Fault("trap");
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await Execute(restoredScenario, false)).Outcome);
            if (state != "pending") Assert.Equal(state == "success" ? DurableExecutionOutcome.Succeeded : DurableExecutionOutcome.NeedsAttention,
                (await Execute(restoredScenario, true, fault)).Outcome);
            Assert.Equal(policy, await Policy(scenario.Database));
            Assert.Equal(0, fault.Dispatches);
            await using var verify = Context(restored);
            Assert.Equal(state == "pending" ? 1 : 2, await verify.DurableOperationAttempts.CountAsync());
            Assert.Equal(state == "success" ? 1L : (long?)null, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        finally { await fixture.RunClientUtilityAsync("rm", "-f", dump); }
    }
}
