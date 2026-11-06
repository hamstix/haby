using Haby.Application;
using Haby.Domain;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    [Fact]
    public async Task Final_owner_access_preserves_unrelated_role_objects_settings_and_creator_defaults()
    {
        var scenario = await Prepare(full: true); await AccessReady(scenario);
        var unrelated = "owner_unrelated_" + Guid.NewGuid().ToString("N");
        await Sql(AdministrativeTarget(scenario), $"""
            CREATE ROLE "{unrelated}" NOLOGIN;
            CREATE SCHEMA outsider_schema AUTHORIZATION "{unrelated}";
            CREATE TABLE outsider_schema.untouched(id integer);
            ALTER TABLE outsider_schema.untouched OWNER TO "{unrelated}";
            ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON ROUTINES FROM PUBLIC;
            ALTER DEFAULT PRIVILEGES FOR ROLE "{unrelated}" REVOKE USAGE ON TYPES FROM PUBLIC;
            ALTER ROLE "{unrelated}" IN DATABASE "{scenario.Database}" SET work_mem TO '8MB';
            """);
        async Task<string> Evidence()
        {
            await using var session = new NpgsqlConnection(AdministrativeTarget(scenario)); await session.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT jsonb_build_array(
                  (SELECT jsonb_build_array(oid, rolname, rolcanlogin, rolsuper, rolcreatedb, rolcreaterole, rolreplication,
                    rolbypassrls, rolconfig) FROM pg_roles WHERE rolname = $1),
                  (SELECT jsonb_agg(to_jsonb(a) ORDER BY defaclrole, defaclnamespace, defaclobjtype)
                    FROM pg_default_acl a WHERE defaclrole <> to_regrole($2)),
                  (SELECT jsonb_build_array(oid, nspname, nspowner, nspacl) FROM pg_namespace WHERE nspname = 'outsider_schema'),
                  (SELECT jsonb_agg(jsonb_build_array(oid, relname, relowner, relacl) ORDER BY oid)
                    FROM pg_class WHERE relnamespace = 'outsider_schema'::regnamespace),
                  (SELECT jsonb_agg(to_jsonb(s) ORDER BY setdatabase) FROM pg_db_role_setting s WHERE setrole = to_regrole($1)))::text
                """, session);
            command.Parameters.AddWithValue(unrelated); command.Parameters.AddWithValue(scenario.Role);
            return (string)(await command.ExecuteScalarAsync())!;
        }
        var before = await Evidence();
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await AccessExecute(scenario)).Outcome);
        Assert.Equal(before, await Evidence());
    }

    private sealed class CommittedAccessDrift(string connection, string role, bool authentication) : IPostgreSqlAccessFaultHooks
    {
        public ValueTask BeforeVerificationAsync(CancellationToken token) => authentication ? ValueTask.CompletedTask :
            new(Sql(connection, "ALTER SCHEMA app OWNER TO postgres"));
        public ValueTask BeforeAuthenticationAsync(CancellationToken token) => authentication ?
            new(Sql(connection, $"ALTER ROLE \"{role}\" NOLOGIN")) : ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_postcommit_evidence_or_saved_login_authentication_failure_retains_uncertainty(bool authentication)
    {
        var scenario = await Prepare(full: true); await AccessReady(scenario);
        var fault = new CommittedAccessDrift(AdministrativeTarget(scenario), scenario.Role, authentication);
        Assert.Equal(DurableExecutionOutcome.NeedsAttention, (await AccessExecute(scenario, fault: fault)).Outcome);
        Assert.Equal(DurableExecutionOutcome.NeedsAttention, (await AccessExecute(scenario, fault: new AccessFault("trap"))).Outcome);
        await using var verify = Context(scenario.Connection);
        Assert.False(await verify.OperationOutputs.AnyAsync(r => r.OperationId == "app-login-access"));
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Fact]
    public async Task Final_action_uses_pinned_endpoint_after_alias_moves_and_supports_a_single_catalog_connection_pool()
    {
        var scenario = await Prepare(full: true);
        await using (var services = OwnerServices(scenario))
        {
            var providers = services.GetRequiredService<IProviderConfigurationStore>();
            var configuration = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(scenario.Plan.Inputs.Environment,
                scenario.Plan.Inputs.ProviderInstance, scenario.Plan.Inputs.ConfigurationRevision, default))!.Configuration;
            var next = ProviderConfigurationRevisionNumber.Create(1);
            var different = new PostgreSqlProviderConfiguration(1, configuration.Host == "127.0.0.1" ? "localhost" : "127.0.0.1", configuration.Port, configuration.MaintenanceDatabase,
                configuration.AdministratorUserName, configuration.AdministratorPassword, configuration.ImplementationId);
            var otherInstance = await providers.CreateInstanceAsync(scenario.Plan.Inputs.Environment, different, default);
            await providers.AddRevisionAsync(scenario.Plan.Inputs.Environment, otherInstance, next, different, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(
                scenario.Plan.Inputs.Environment, scenario.Plan.Inputs.Alias, new(scenario.Plan.Inputs.ProviderInstance, scenario.Plan.Inputs.ConfigurationRevision),
                new(otherInstance, next), "tester", default)).Outcome);
        }
        // Adapter accepted-context validation must not hold a catalog connection while resolving
        // other immutable inputs through their own contexts.
        scenario = scenario with { Connection = _catalogPools.Register(new NpgsqlConnectionStringBuilder(scenario.Connection) { Timeout = 5 }, maximumPoolSize: 1) };
        await AccessReady(scenario);
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await AccessExecute(scenario)).Outcome);
        Assert.Equal(new NpgsqlConnectionStringBuilder(fixture.ConnectionString).Host, (await ConnectionOutput(scenario)).Host);
        var output = await ConnectionOutput(scenario);
        await using var servicesAfter = OwnerServices(scenario);
        var outputs = servicesAfter.GetRequiredService<IExternalOperationOutputs>();
        var attempt = Assert.Single(await servicesAfter.GetRequiredService<IDurableOperationJournal>().FindAttemptsAsync(scenario.Key("app-login-access"), default)).AttemptId;
        Assert.True(await outputs.CompleteAsync(scenario.Key("app-login-access"), attempt, PostgreSqlApplicationConnectionContract.Encode(output), DateTimeOffset.UtcNow, default));
        Assert.False(await outputs.CompleteAsync(scenario.Key("app-login-access"), attempt, PostgreSqlApplicationConnectionContract.Encode(output with { Host = "wrong" }), DateTimeOffset.UtcNow, default));
        await AssertComplete(scenario);
        _catalogPools.Dispose();
    }

    [Theory]
    [InlineData("database-missing")]
    [InlineData("database-uncertain")]
    [InlineData("database-name")]
    [InlineData("database-oid")]
    [InlineData("role-missing")]
    [InlineData("role-uncertain")]
    [InlineData("role-name")]
    [InlineData("role-oid")]
    [InlineData("restrict")]
    [InlineData("restrict-attempt")]
    [InlineData("profile")]
    [InlineData("audit")]
    [InlineData("payload")]
    [InlineData("owner")]
    [InlineData("version")]
    [InlineData("provider-secret")]
    [InlineData("reservation")]
    [InlineData("reservation-owner")]
    [InlineData("preexisting-schema")]
    [InlineData("owner-drift")]
    [InlineData("role-flags")]
    [InlineData("role-membership")]
    [InlineData("public-acl")]
    public async Task Final_access_refuses_inexact_evidence_material_reservation_and_external_drift_before_mutation(string mode)
    {
        var scenario = await Prepare(full: true); await AccessReady(scenario);
        var catalog = mode switch
        {
            "database-missing" => "DELETE FROM operation_outputs WHERE operation_id = 'db-create'",
            "database-uncertain" => "UPDATE durable_create_operation_attempts SET outcome = 'Uncertain' WHERE operation_id = 'db-create'",
            "database-name" => "UPDATE operation_outputs SET payload = '{\"databaseName\":\"wrong\",\"databaseOid\":1}' WHERE operation_id = 'db-create'",
            "database-oid" => "UPDATE operation_outputs SET payload = jsonb_set(payload::jsonb, '{databaseOid}', '1')::text WHERE operation_id = 'db-create'",
            "role-missing" => "DELETE FROM operation_outputs WHERE operation_id = 'app-login-create'",
            "role-uncertain" => "UPDATE durable_create_operation_attempts SET outcome = 'Uncertain' WHERE operation_id = 'app-login-create'",
            "role-name" => "UPDATE operation_outputs SET payload = replace(payload, 'haby_a_', 'other_') WHERE operation_id = 'app-login-create'",
            "role-oid" => "UPDATE operation_outputs SET payload = jsonb_set(payload::jsonb, '{roleOid}', '1')::text WHERE operation_id = 'app-login-create'",
            "restrict" => "UPDATE durable_create_operations SET state = 'NeedsAttention' WHERE operation_id = 'db-restrict'",
            "restrict-attempt" => "UPDATE durable_create_operation_attempts SET outcome = 'Uncertain' WHERE operation_id = 'db-restrict'",
            "profile" => "ALTER TABLE generated_credential_slots DROP CONSTRAINT ck_generated_slot_shape; UPDATE generated_credential_slots SET generation_profile = 'unsupported/v1'",
            "audit" => "DELETE FROM generated_credential_creation_audits",
            "payload" => "DELETE FROM secret_payloads WHERE secret_version_id IN (SELECT version_id FROM generated_credential_slots)",
            "owner" => "UPDATE generated_credential_slots SET resource_local_id = 'different-login'",
            "version" => "UPDATE operation_outputs SET payload = replace(payload, (SELECT version_id::text FROM generated_credential_slots), '00000000-0000-7000-8000-000000000001') WHERE operation_id = 'app-login-material'",
            "provider-secret" => "DELETE FROM secret_payloads WHERE secret_version_id NOT IN (SELECT version_id FROM generated_credential_slots)",
            "reservation" => "DELETE FROM provider_resource_reservations",
            "reservation-owner" => "UPDATE provider_resource_reservations SET resource_local_id = 'different-login'",
            _ => "SELECT 1"
        };
        await Sql(scenario.Connection, "SET session_replication_role = replica; " + catalog);
        var external = mode switch
        {
            "preexisting-schema" => "CREATE SCHEMA app",
            "owner-drift" => $"ALTER DATABASE \"{scenario.Database}\" OWNER TO \"{scenario.Role}\"",
            "role-flags" => $"ALTER ROLE \"{scenario.Role}\" CREATEDB",
            "role-membership" => $"GRANT pg_read_all_data TO \"{scenario.Role}\"",
            "public-acl" => "GRANT USAGE ON SCHEMA public TO PUBLIC",
            _ => "SELECT 1"
        };
        await Sql(AdministrativeTarget(scenario), external);
        var before = await OwnershipAndObjects(scenario.Database); var policy = await Policy(scenario.Database);
        var trap = new AccessFault("trap");
        Assert.NotEqual(DurableExecutionOutcome.Succeeded, (await AccessExecute(scenario, fault: trap)).Outcome);
        Assert.Equal(0, trap.Markers);
        Assert.Equal(before, await OwnershipAndObjects(scenario.Database)); Assert.Equal(policy, await Policy(scenario.Database));
        await using var verify = Context(scenario.Connection);
        Assert.False(await verify.OperationOutputs.AnyAsync(r => r.OperationId == "app-login-access"));
        Assert.False(await verify.DurableOperationAttempts.AnyAsync(r => r.OperationId == "app-login-access" && r.DispatchedAt != null));
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        if (mode == "reservation") Assert.Empty(await verify.ProviderResourceReservations.ToArrayAsync());
    }

    [Fact]
    public async Task Final_connection_publication_is_mandatory_exact_pinned_and_write_once()
    {
        var scenario = await Prepare(full: true); await AccessReady(scenario);
        var reference = await Password(scenario);
        await using (var services = OwnerServices(scenario))
        {
            var journal = services.GetRequiredService<IDurableOperationJournal>();
            var started = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(scenario.Key("app-login-access"), DateTimeOffset.UtcNow, default));
            await journal.TryCommitDispatchMarkerAsync(scenario.Key("app-login-access"), started.AttemptId, DateTimeOffset.UtcNow, default);
            Assert.False(await journal.TryRecordSuccessAsync(scenario.Key("app-login-access"), started.AttemptId, DateTimeOffset.UtcNow, default));
            var endpoint = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
            var valid = new PostgreSqlApplicationConnection(endpoint.Host!, endpoint.Port, scenario.Database, "app", scenario.Role, reference);
            var outputs = services.GetRequiredService<IExternalOperationOutputs>();
            foreach (var invalid in new[]
            {
                valid with { Host = "other-host" }, valid with { Port = endpoint.Port + 1 }, valid with { DatabaseName = "wrong" },
                valid with { Schema = "wrong" }, valid with { UserName = "haby_a_" + new string('0', 48) },
                valid with { Password = new(SecretId.New(), SecretVersionId.New()) }
            }) Assert.False(await outputs.CompleteAsync(scenario.Key("app-login-access"), started.AttemptId,
                PostgreSqlApplicationConnectionContract.Encode(invalid), DateTimeOffset.UtcNow, default));
            Assert.False(await outputs.CompleteAsync(scenario.Key("app-login-access"), Guid.NewGuid(),
                PostgreSqlApplicationConnectionContract.Encode(valid), DateTimeOffset.UtcNow, default));
        }
        // The intentionally marked but undispatched effect remains uncertain, never inferred from names.
        Assert.Equal(DurableExecutionOutcome.NeedsAttention, (await AccessExecute(scenario)).Outcome);
        await using var verify = Context(scenario.Connection);
        Assert.False(await verify.OperationOutputs.AnyAsync(r => r.OperationId == "app-login-access"));
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_graph_real_dump_restore_retains_exact_outputs_material_attempts_and_never_replays(bool uncertain)
    {
        var scenario = await Prepare(full: true); await AccessReady(scenario);
        var reference = await Password(scenario); var bytes = await Material(scenario, reference);
        try
        {
            var expected = uncertain ? DurableExecutionOutcome.NeedsAttention : DurableExecutionOutcome.Succeeded;
            Assert.Equal(expected, (await AccessExecute(scenario, fault: uncertain ? new AccessFault("commit") : null)).Outcome);
            var saved = await AccessCatalogEvidence(scenario.Connection);
            var external = await OwnershipAndObjects(scenario.Database); var policy = await Policy(scenario.Database);
            var restoredConnection = await Database("owner_restore_"); var dump = "/tmp/owner-" + Guid.NewGuid().ToString("N") + ".dump";
            try
            {
                await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + new NpgsqlConnectionStringBuilder(scenario.Connection).Database);
                await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + new NpgsqlConnectionStringBuilder(restoredConnection).Database, dump);
                var restored = scenario with { Connection = restoredConnection };
                var trap = new AccessFault("trap");
                await using (var services = OwnerServices(restored, trap))
                {
                    await services.GetRequiredService<Haby.Persistence.PostgreSql.ManifestCatalogMigrationInitializer>().MigrateAsync();
                    Assert.Equal(saved, await AccessCatalogEvidence(restoredConnection));
                    var result = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(restored.Commit.DeploymentId, restored.Commit.Generation, default);
                    Assert.Equal(uncertain ? DurableExecutionOutcome.NeedsAttention : (DurableExecutionOutcome?)null, result.StopOutcome);
                }
                Assert.Equal(0, trap.Markers); Assert.Equal(saved, await AccessCatalogEvidence(restoredConnection));
                Assert.Equal(reference, await Password(restored));
                var material = await Material(restored, reference);
                try { Assert.True(bytes.SequenceEqual(material)); } finally { Array.Clear(material); }
                await AssertLogin(restored, bytes);
                Assert.Equal(external, await OwnershipAndObjects(scenario.Database)); Assert.Equal(policy, await Policy(scenario.Database));
                // Catalog restore retains evidence; the external database/role remain in the same disposable cluster.
                // It neither dumps cluster roles nor recreates external resources.
            }
            finally { await fixture.RunClientUtilityAsync("rm", "-f", dump); }
        }
        finally { Array.Clear(bytes); }
    }

    private static async Task<string> AccessCatalogEvidence(string connection)
    {
        await using var session = new NpgsqlConnection(connection); await session.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_array(
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY generation) FROM application_deployment_generations t),
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY operation_id, output_contract, output_key) FROM operation_outputs t),
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY operation_id) FROM durable_create_operations t),
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY operation_id, attempt_number) FROM durable_create_operation_attempts t),
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY secret_id) FROM generated_credential_slots t),
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY secret_id) FROM generated_credential_creation_audits t),
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY namespace, value) FROM provider_resource_reservations t),
              (SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM application_deployments t))::text
            """, session);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
