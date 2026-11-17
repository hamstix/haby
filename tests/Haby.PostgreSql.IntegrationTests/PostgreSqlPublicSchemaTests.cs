using System.Text;
using Haby.Application;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("replaced")]
    [InlineData("foreign-owner")]
    [InlineData("public-acl")]
    [InlineData("foreign-acl")]
    [InlineData("database-oid")]
    [InlineData("role-oid")]
    [InlineData("schema-oid")]
    public async Task Explicit_public_refuses_inexact_identity_owner_or_acl_without_mutation(string mode)
    {
        var scenario = await Prepare(full: true, configureManifest: envelope =>
            envelope["manifest"]!["spec"]!["resources"]!["app-login"]!["properties"]!["schema"] = "public");
        await AccessReady(scenario);
        var sql = mode switch
        {
            "missing" => "DROP SCHEMA public",
            "replaced" => "DROP SCHEMA public; CREATE SCHEMA public AUTHORIZATION pg_database_owner",
            "foreign-owner" => "ALTER SCHEMA public OWNER TO postgres",
            "public-acl" => "GRANT USAGE ON SCHEMA public TO PUBLIC",
            "foreign-acl" => "GRANT USAGE ON SCHEMA public TO pg_read_all_data",
            _ => "SELECT 1"
        };
        await Sql(AdministrativeTarget(scenario), sql);
        if (mode is "database-oid" or "role-oid" or "schema-oid")
        {
            var operation = mode == "role-oid" ? "app-login-create" : "db-create";
            var field = mode switch { "role-oid" => "roleOid", "schema-oid" => "publicSchemaOid", _ => "databaseOid" };
            await Sql(scenario.Connection, $"SET session_replication_role = replica; UPDATE operation_outputs SET payload = jsonb_set(payload::jsonb, '{{{field}}}', '1')::text WHERE operation_id = '{operation}'");
        }
        var before = await OwnershipAndObjects(scenario.Database);
        var trap = new AccessFault("trap");
        Assert.NotEqual(DurableExecutionOutcome.Succeeded, (await AccessExecute(scenario, fault: trap)).Outcome);
        Assert.Equal(0, trap.Markers);
        Assert.Equal(before, await OwnershipAndObjects(scenario.Database));
        await using var verify = Context(scenario.Connection);
        Assert.False(await verify.OperationOutputs.AnyAsync(r => r.OperationId == "app-login-access"));
        Assert.False(await verify.DurableOperationAttempts.AnyAsync(r => r.OperationId == "app-login-access" && r.DispatchedAt != null));
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_public_restart_retains_identity_data_and_never_replays_a_lost_commit_response(bool uncertain)
    {
        var scenario = await Prepare(full: true, render: true, configureManifest: envelope =>
            envelope["manifest"]!["spec"]!["resources"]!["app-login"]!["properties"]!["schema"] = "public");
        await AccessReady(scenario);
        var reference = await Password(scenario);
        var bytes = await Material(scenario, reference);
        try
        {
            var expected = uncertain ? DurableExecutionOutcome.NeedsAttention : DurableExecutionOutcome.Succeeded;
            Assert.Equal(expected, (await AccessExecute(scenario, fault: uncertain ? new AccessFault("commit") : null)).Outcome);
            var application = new NpgsqlConnectionStringBuilder(AdministrativeTarget(scenario))
            { Username = scenario.Role, Password = Encoding.ASCII.GetString(bytes), Pooling = false };
            try
            {
                await Sql(application.ConnectionString, "CREATE TABLE public.preserved(id integer); INSERT INTO preserved VALUES (247)");
                var external = await OwnershipAndObjects(scenario.Database);
                var saved = await AccessCatalogEvidence(scenario.Connection);
                var trap = new AccessFault("trap");
                Assert.Equal(expected, (await AccessExecute(scenario, fault: trap)).Outcome);
                await using (var restart = OwnerServices(scenario, trap))
                {
                    var result = await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
                    Assert.Equal(uncertain ? DurableExecutionOutcome.NeedsAttention : (DurableExecutionOutcome?)null, result.StopOutcome);
                }
                Assert.Equal(0, trap.Markers);
                Assert.Equal(saved, await AccessCatalogEvidence(scenario.Connection));
                Assert.Equal(external, await OwnershipAndObjects(scenario.Database));
                Assert.Equal(reference, await Password(scenario));
                var after = await Material(scenario, reference);
                try { Assert.True(bytes.SequenceEqual(after)); } finally { Array.Clear(after); }
                await using var login = new NpgsqlConnection(application.ConnectionString);
                await login.OpenAsync();
                Assert.True(await new NpgsqlCommand("SELECT current_setting('search_path') = 'public' AND current_schema() = 'public' AND (SELECT id FROM preserved) = 247", login).ExecuteScalarAsync() is true);
                await using var verify = Context(scenario.Connection);
                Assert.Equal(!uncertain, await verify.OperationOutputs.AnyAsync(r => r.OperationId == "app-login-access"));
                Assert.Single(await verify.DurableOperationAttempts.Where(r => r.OperationId == "app-login-access").ToArrayAsync());
            }
            finally { application.Password = null; }
        }
        finally { Array.Clear(bytes); }
    }
}
