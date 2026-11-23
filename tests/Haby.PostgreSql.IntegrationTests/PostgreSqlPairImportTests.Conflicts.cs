using Haby.Application;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlPairImportTests
{
    [Fact]
    public async Task Existing_public_schema_cannot_shadow_import_verification_builtins()
    {
        var pair = await ExistingPair();
        await using (var control = new Npgsql.NpgsqlConnection(Control()))
        {
            await control.OpenAsync();
            Assert.True(await new Npgsql.NpgsqlCommand("SELECT rolconfig IS NULL FROM pg_catalog.pg_roles WHERE rolname = 'postgres'", control).ExecuteScalarAsync() is true);
        }
        var database = new Npgsql.NpgsqlConnectionStringBuilder(Control()) { Database = pair.Database }.ConnectionString;
        await Sql(database, $"""
            SET ROLE "{pair.Login}";
            CREATE FUNCTION public.current_database() RETURNS name LANGUAGE sql AS 'SELECT ''shadowed_database''::name';
            RESET ROLE;
            """);
        resetAdministratorSearchPath = true;
        await Sql(Control(), "ALTER ROLE postgres SET search_path = public, pg_catalog");
        await Sql(Control(), $"ALTER ROLE \"{pair.Login}\" SET search_path = public, pg_catalog");
        var before = await Snapshot(pair);
        var setup = await Prepare(pair, PostgreSqlTransportMode.Disable);
        await using (var services = Services(setup.Connection))
            Assert.Null((await services.GetRequiredService<DurableGenerationRunner>().RunAsync(setup.Commit.DeploymentId, setup.Commit.Generation, default)).StopOutcome);
        Assert.True(before == await Snapshot(pair), "Verification rewrote existing role or schema settings.");
    }

    [Fact]
    public async Task Concurrent_imports_of_the_same_external_pair_accept_one_owner_without_partial_loser_outputs()
    {
        var pair = await ExistingPair();
        await using var passwordGuard = await PasswordGuard(pair);
        var before = await Snapshot(pair);
        var first = await Prepare(pair, PostgreSqlTransportMode.Disable);
        var second = await Prepare(pair, PostgreSqlTransportMode.Disable, existingCatalog: first.Connection);
        Assert.Equal(first.Plan.Inputs.Providers.RequireUniformResourceProvider().Instance, second.Plan.Inputs.Providers.RequireUniformResourceProvider().Instance);
        await using (var services = Services(first.Connection, new BothVerified()))
        {
            var runner = services.GetRequiredService<DurableGenerationRunner>();
            var results = await Task.WhenAll(runner.RunAsync(first.Commit.DeploymentId, first.Commit.Generation, default).AsTask(),
                runner.RunAsync(second.Commit.DeploymentId, second.Commit.Generation, default).AsTask());
            Assert.Single(results, x => x.StopOutcome is null);
            var loser = Assert.Single(results, x => x.StopOutcome == DurableExecutionOutcome.Failed);
            Assert.All(loser.Progress!.Nodes, x => Assert.Equal(DurableOperationReason.Conflict, x.Operation.Reason));
            var losingSetup = loser.Progress.DeploymentId == first.Commit.DeploymentId ? first : second;
            await AssertNoUsableCredential(losingSetup);
        }
        await using (var verify = Services(first.Connection))
        await using (var db = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.Single(await db.ResourceObservationReceipts.AsNoTracking().ToArrayAsync());
            Assert.Equal(2, await db.OperationOutputs.CountAsync());
            Assert.Equal(4, await db.ProviderResourceReservations.CountAsync());
            Assert.Equal(1, await db.ProtectedApplicationCredentials.CountAsync(x => x.ConfirmedReceipt != null));
            Assert.Equal(1, await db.ProtectedApplicationCredentials.CountAsync(x => x.ConfirmedReceipt == null));
        }
        Assert.True(before == await Snapshot(pair), "Conflicting imports changed the external pair.");
        Assert.True(await PasswordUnchanged(passwordGuard, pair), "Conflicting imports changed the existing password verifier.");
    }

    [Fact]
    public async Task Unsafe_role_missing_permission_wrong_owner_and_identity_drift_refuse_without_external_repair()
    {
        var pair = await ExistingPair();
        await using var passwordGuard = await PasswordGuard(pair);
        var database = new Npgsql.NpgsqlConnectionStringBuilder(Control()) { Database = pair.Database }.ConnectionString;
        foreach (var fault in new[] { "role", "permission", "owner", "identity" })
        {
            if (fault == "role") await Sql(Control(), $"ALTER ROLE \"{pair.Login}\" CREATEDB");
            if (fault == "permission") await Sql(database, "REVOKE CREATE ON SCHEMA public FROM PUBLIC");
            if (fault == "owner") await Sql(Control(), $"ALTER DATABASE \"{pair.Database}\" OWNER TO postgres");
            var before = await Snapshot(pair);
            var candidate = fault == "identity" ? pair with { Input = pair.Input with { DatabaseOid = pair.Input.DatabaseOid + 1 } } : pair;
            var setup = await Prepare(candidate, PostgreSqlTransportMode.Disable);
            await using (var services = Services(setup.Connection))
            {
                var result = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(setup.Commit.DeploymentId, setup.Commit.Generation, default);
                Assert.Equal(DurableExecutionOutcome.Failed, result.StopOutcome);
                Assert.All(result.Progress!.Nodes, x => Assert.Equal(DurableOperationReason.Conflict, x.Operation.Reason));
            }
            await AssertNoUsableCredential(setup);
            Assert.True(before == await Snapshot(pair), "Refused import repaired or mutated the unsupported external state.");
            Assert.True(await PasswordUnchanged(passwordGuard, pair), "Refused import changed the existing password verifier.");
            if (fault == "role") await Sql(Control(), $"ALTER ROLE \"{pair.Login}\" NOCREATEDB");
            if (fault == "permission") await Sql(database, "GRANT CREATE ON SCHEMA public TO PUBLIC");
            if (fault == "owner") await Sql(Control(), $"ALTER DATABASE \"{pair.Database}\" OWNER TO \"{pair.Login}\"");
        }
    }

    private sealed class BothVerified : IPostgreSqlImportFaultHooks
    {
        private int count;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask AfterVerificationAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref count) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
        }
    }
}
