using System.Text;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Haby.Application;
using Haby.Domain;
using Haby.PostgreSql;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    private const string RenderingProfile = """
        {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"connection-layout","revision":1},"spec":{
        "documents":{"settings":{"fileName":"appsettings.json","format":"json","template":{}}},
        "staticContributions":{"logging":{"documentRef":"settings","path":"/Logging","value":{"Level":"Information"}}},
        "bindingRules":{"connection":{"slot":"main-database","rendererRef":"postgresql.npgsql-connection/v1alpha1","options":{"sslMode":"disable"},
        "documentProjections":[{"documentRef":"settings","path":"/PostgreSQL/DefaultConnection/ConnectionString"}]}}}}
        """;

    [Fact]
    public async Task Prior_configuration_format_requires_fresh_catalog_in_startup_and_direct_migration_without_reset()
    {
        var scenario = await Prepare(full: true, render: true);
        await using (var arrange = Context(scenario.Connection))
            await arrange.Database.ExecuteSqlRawAsync("""
                DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" LIKE '%PinConfigurationRendering' OR "MigrationId" LIKE '%MaterializeConfigurationDocuments' OR "MigrationId" LIKE '%AddConfigurationAccess' OR "MigrationId" LIKE '%AddOperatorAccess';
                """);
        await using (var services = OwnerServices(scenario))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync());
            Assert.Contains("fresh catalog", error.Message);
        }
        await using (var direct = Context(scenario.Connection))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => direct.Database.MigrateAsync());
            Assert.Contains("fresh catalog", error.MessageText);
        }
        await using var verify = Context(scenario.Connection);
        Assert.Equal(1, await verify.DurablePlans.AsNoTracking().CountAsync());
        Assert.Equal(1, await verify.DeploymentGenerations.AsNoTracking().CountAsync());
        Assert.Empty(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.Equal(20, (await verify.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task Frozen_api_worker_share_confirmed_output_and_secret_format_real_connections_and_survive_restore()
    {
        var scenario = await Prepare(full: true, render: true);
        var frozen = scenario.Plan.Inputs.Configuration!;
        Assert.Equal(2, frozen.RenderPins.Count);
        var catalog = PostgreSqlConnectionRendering.CreateCatalog();
        await using (var before = OwnerServices(scenario))
        {
            var renderer = new ConfigurationOutputRenderer(catalog, before.GetRequiredService<IConfigurationOutputEvidence>());
            Assert.Null(await renderer.RenderAsync(frozen.RenderPins[0], scenario.Commit.DeploymentId, scenario.Commit.Generation, scenario.Owner, default));
        }
        await AccessReady(scenario);
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await AccessExecute(scenario)).Outcome);
        var password = await Password(scenario); var bytes = await Material(scenario, password);
        var material = Encoding.UTF8.GetChars(bytes);
        try
        {
            await Verify(scenario);
            var restored = _catalogPools.Register(new NpgsqlConnectionStringBuilder(await Database("render_restore_")));
            var directory = "/tmp/haby-render-" + Guid.NewGuid().ToString("N"); var dump = directory + "/catalog.dump";
            Exception? failure = null;
            try
            {
                await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
                await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump,
                    "--dbname=" + new NpgsqlConnectionStringBuilder(scenario.Connection).Database);
                await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges",
                    "--dbname=" + new NpgsqlConnectionStringBuilder(restored).Database, dump);
                await Verify(scenario with { Connection = restored });
            }
            catch (Exception e) { failure = e; throw; }
            finally
            {
                try { await fixture.RunClientUtilityAsync("rm", "-f", dump); await fixture.RunClientUtilityAsync("rmdir", directory); }
                catch when (failure is not null) { }
            }
        }
        finally { Array.Clear(bytes); Array.Clear(material); }

        async Task Verify(Scenario current)
        {
            await using var services = OwnerServices(current);
            await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var saved = (await services.GetRequiredService<IDurableDeploymentPlanner>().FindPlanAsync(current.Plan.Id, default))!;
            Assert.Equal(current.Plan, saved);
            var committer = services.GetRequiredService<IDurableDesiredStateCommitter>();
            var generation = (await committer.FindGenerationAsync(current.Commit.DeploymentId, current.Commit.Generation, default))!;
            Assert.Equal(frozen, generation.Inputs.Configuration);
            Assert.Equal(current.Commit, Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(saved.ChangeSetId, saved.Id, "retry", default)).Commit);
            await using var readOnly = Services(current.Connection, full: true, access: true, queryTrap: new RenderingReadTrap());
            var renderer = new ConfigurationOutputRenderer(catalog, readOnly.GetRequiredService<IConfigurationOutputEvidence>());
            var references = new List<SecretRef>();
            foreach (var pin in generation.Inputs.Configuration!.RenderPins)
            {
                Assert.Equal(PostgreSqlConsumerOptions.Defaults, pin.Defaults);
                Assert.Equal(ProfileOptionScope.RendererDefault, pin.OptionOrigins["commandTimeout"]);
                Assert.Equal(ProfileOptionScope.Profile, pin.OptionOrigins["sslMode"]);
                Assert.Equal(pin.Component.Value == "api" ? ProfileOptionScope.Binding : ProfileOptionScope.RendererDefault, pin.OptionOrigins["timeout"]);
                var fragment = (await renderer.RenderAsync(pin, current.Commit.DeploymentId, current.Commit.Generation, current.Owner, default))!;
                var deferred = Assert.IsType<ConfigurationDeferredValue>(fragment.Value);
                references.Add(Assert.IsType<ConfigurationSecretReference>(deferred.Arguments.Members["password"]).Reference);
                var layout = frozen.Layouts.Single(x => x.Component == pin.Component);
                var request = layout.BindingRequests[pin.Request];
                var composed = new ConfigurationComposer(PostgreSqlConnectionRendering.FormatterCatalog()).Compose(layout.Documents.Single().Definition.Template,
                    layout.Contributions.Select(x => new ConfigurationProjection(x.Contribution.Path, new(x.Contribution.Value), x.Contribution.Id))
                    .Concat(request.Projections.Select(p => new ConfigurationProjection(p.Path, fragment, request.BindingId))));
                Assert.True(composed.IsSuccess); Assert.False(ConfigurationJson.Serialize(composed.Document!).IsSuccess);
                var preview = ConfigurationJson.Preview(composed.Document!);
                Assert.Equal(new[] { "/PostgreSQL/DefaultConnection/ConnectionString" }, preview.RedactedPaths);
                Assert.DoesNotContain(current.Role, preview.Json); Assert.DoesNotContain(password.SecretId.ToString(), preview.Json);
                Assert.Contains("Information", preview.Json);
                var formatted = catalog.Format(pin, deferred, material)!;
                var parsed = new NpgsqlConnectionStringBuilder(formatted.Reveal());
                Assert.Equal(pin.Component.Value == "api" ? 200 : 15, parsed.Timeout);
                Assert.Equal(30, parsed.CommandTimeout); Assert.Equal(SslMode.Disable, parsed.SslMode); Assert.Null(parsed.SearchPath);
                // Disable-only fixture: explicit TLS mappings have fast tests; no VerifyFull certificate rehearsal is claimed.
                await using var connection = new NpgsqlConnection(formatted.Reveal());
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT current_user = $1 AND current_database() = $2 AND current_schema() = 'app'", connection);
                command.Parameters.AddWithValue(current.Role); command.Parameters.AddWithValue(current.Database);
                Assert.Equal(30, command.CommandTimeout); Assert.True(await command.ExecuteScalarAsync() is true);
                var wrong = catalog.Format(pin, deferred, "explicitly-wrong-test-material")!;
                await using var rejected = new NpgsqlConnection(wrong.Reveal());
                var error = await Assert.ThrowsAsync<PostgresException>(() => rejected.OpenAsync());
                Assert.Equal(PostgresErrorCodes.InvalidPassword, error.SqlState);
                Assert.Null(await renderer.RenderAsync(pin, current.Commit.DeploymentId, current.Commit.Generation,
                    current.Owner with { EnvironmentRecordId = EnvironmentRecordId.New() }, default));
                Assert.Null(await renderer.RenderAsync(pin, current.Commit.DeploymentId, current.Commit.Generation,
                    current.Owner with { ApplicationRecordId = ApplicationRecordId.New() }, default));
                Assert.Null(await renderer.RenderAsync(pin, current.Commit.DeploymentId, DeploymentGeneration.Create(2), current.Owner, default));
                Assert.Null(await renderer.RenderAsync(pin, current.Commit.DeploymentId, current.Commit.Generation,
                    current.Owner with { ResourceLocalId = ManifestLocalId.Create("db") }, default));
            }
            Assert.Equal(new[] { password, password }, references);
            await AssertComplete(current);
        }
    }

    private sealed class RenderingReadTrap : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            foreach (var forbidden in new[] { "secret_payloads", "provider_aliases", "provider_configuration_revisions", "generated_credential_slots" })
                Assert.DoesNotContain(forbidden, command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData("uncertain")]
    [InlineData("missing")]
    [InlineData("key")]
    [InlineData("contract")]
    [InlineData("producer")]
    [InlineData("payload")]
    public async Task Render_selection_refuses_unconfirmed_inexact_or_invalid_final_evidence(string mode)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        var sql = mode switch
        {
            "uncertain" => "UPDATE durable_create_operation_attempts SET outcome = 'Uncertain' WHERE operation_id = 'app-login-access'",
            "missing" => "DELETE FROM operation_outputs WHERE operation_id = 'app-login-access'",
            "key" => "UPDATE operation_outputs SET output_key = 'wrong' WHERE operation_id = 'app-login-access'",
            "contract" => "UPDATE operation_outputs SET output_contract = 'wrong/v1' WHERE operation_id = 'app-login-access'",
            "producer" => "UPDATE operation_outputs SET operation_id = 'app-login-create' WHERE operation_id = 'app-login-access'",
            _ => "UPDATE operation_outputs SET payload = '{}' WHERE operation_id = 'app-login-access'"
        };
        await Sql(scenario.Connection, "SET session_replication_role = replica; " + sql);
        await using var services = OwnerServices(scenario);
        var renderer = new ConfigurationOutputRenderer(PostgreSqlConnectionRendering.CreateCatalog(), services.GetRequiredService<IConfigurationOutputEvidence>());
        Assert.Null(await renderer.RenderAsync(scenario.Plan.Inputs.Configuration!.RenderPins[0], scenario.Commit.DeploymentId, scenario.Commit.Generation, scenario.Owner, default));
    }
}
