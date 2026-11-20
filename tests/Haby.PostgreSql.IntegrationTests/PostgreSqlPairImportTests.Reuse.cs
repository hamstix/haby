using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.PostgreSql;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlPairImportTests
{
    [Fact]
    public async Task Later_zero_node_generation_reverifies_import_and_preserves_receipt_with_irrelevant_acl_change()
    {
        var pair = await ExistingPair();
        var setup = await AcceptedPair(pair);
        // A real ACL representation change preserves all required effective privileges.
        await Sql(Control(), $"GRANT TEMPORARY ON DATABASE \"{pair.Database}\" TO \"{pair.Login}\"");
        var next = await Reuse(setup);
        var hook = new CountReuse();
        await using var services = Services(setup.Connection, hook);
        var result = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(next.DeploymentId, next.Generation, default);
        Assert.Null(result.StopOutcome);
        Assert.Equal(2, result.Progress!.AppliedGeneration);
        Assert.True(hook.Count > 0);
        await AssertOriginalReceipt(setup);
        // Historical completed observation does not turn into a continuous health probe.
        var count = hook.Count;
        Assert.Null((await services.GetRequiredService<DurableGenerationRunner>().RunAsync(next.DeploymentId, next.Generation, default)).StopOutcome);
        Assert.Equal(count, hook.Count);
    }

    [Fact]
    public async Task Later_import_reuse_refuses_owner_permission_identity_and_password_drift_without_advancing()
    {
        foreach (var drift in new[] { "owner", "permission", "identity", "password" })
        {
            var pair = await ExistingPair();
            var setup = await AcceptedPair(pair);
            var next = await Reuse(setup); // Plan/Check/Commit never probe the external state.
            switch (drift)
            {
                case "owner":
                    await Sql(new Npgsql.NpgsqlConnectionStringBuilder(Control()) { Database = pair.Database }.ConnectionString,
                        $"ALTER SCHEMA public OWNER TO \"{pair.Login}\""); break;
                case "permission":
                    await Sql(new Npgsql.NpgsqlConnectionStringBuilder(Control()) { Database = pair.Database }.ConnectionString,
                        "REVOKE CREATE ON SCHEMA public FROM PUBLIC"); break;
                case "identity":
                    await Sql(Control(), $"ALTER DATABASE \"{pair.Database}\" RENAME TO \"{pair.Database}_gone\"");
                    databases.Remove(pair.Database); databases.Add(pair.Database + "_gone"); break;
                default:
                    await Sql(Control(), $"ALTER ROLE \"{pair.Login}\" PASSWORD 'changed-owned-test-password'"); break;
            }
            await using var services = Services(setup.Connection);
            var result = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(next.DeploymentId, next.Generation, default);
            Assert.Equal(ResourceReuseValidationStatus.Conflict, result.ReuseStatus);
            Assert.Equal(DurableExecutionOutcome.Failed, result.StopOutcome);
            Assert.Equal(1, result.Progress!.AppliedGeneration);
            await AssertOriginalReceipt(setup);
        }
    }

    [Fact]
    public async Task Reuse_discards_verification_after_captured_catalog_input_changes_and_preserves_cancellation()
    {
        var pair = await ExistingPair();
        var setup = await AcceptedPair(pair);
        var next = await Reuse(setup);
        await using (var services = Services(setup.Connection, new MutateCapturedInput(setup)))
        {
            var result = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(next.DeploymentId, next.Generation, default);
            Assert.Equal(ResourceReuseValidationStatus.InputUnavailable, result.ReuseStatus);
            Assert.Equal(1, result.Progress!.AppliedGeneration);
        }
        await using (var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(setup.Connection).Options))
            await db.ApplicationDeployments.ExecuteUpdateAsync(s => s.SetProperty(x => x.DesiredGeneration, 2));
        using var cancellation = new CancellationTokenSource();
        await using (var services = Services(setup.Connection, new CancelReuse(cancellation)))
        {
            var canceled = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(next.DeploymentId, next.Generation, cancellation.Token);
            Assert.True(canceled.Canceled);
            Assert.Equal(1, canceled.Progress!.AppliedGeneration);
            Assert.Null(canceled.ReuseStatus);
        }
        await AssertOriginalReceipt(setup);
        // New invocation performs a new verification and can complete.
        await using var retry = Services(setup.Connection);
        Assert.Null((await retry.GetRequiredService<DurableGenerationRunner>().RunAsync(next.DeploymentId, next.Generation, default)).StopOutcome);
    }

    [Fact]
    public async Task Missing_exact_reuse_capability_refuses_and_connectivity_is_retryable_without_historical_rewrites()
    {
        var pair = await ExistingPair();
        var setup = await AcceptedPair(pair);
        var next = await Reuse(setup);
        await using (var missing = Services(setup.Connection, configure: s => s.RemoveAll<IDurableResourceReuseVerifier>()))
        {
            var result = await missing.GetRequiredService<DurableGenerationRunner>().RunAsync(next.DeploymentId, next.Generation, default);
            Assert.Equal(ResourceReuseValidationStatus.InputUnavailable, result.ReuseStatus);
            Assert.Equal(1, result.Progress!.AppliedGeneration);
        }
        // Deliberate transport-outcome stub isolates common retry classification. Real adapter drift/auth
        // and positive network verification are covered by the neighboring disposable-server cases.
        await using (var unavailable = Services(setup.Connection, configure: s =>
        {
            s.RemoveAll<IDurableResourceReuseVerifier>();
            s.AddSingleton<IDurableResourceReuseVerifier>(new UnavailableReuse(PostgreSqlImportContracts.Database.Admission));
            s.AddSingleton<IDurableResourceReuseVerifier>(new UnavailableReuse(PostgreSqlImportContracts.Login.Admission));
        }))
        {
            var result = await unavailable.GetRequiredService<DurableGenerationRunner>().RunAsync(next.DeploymentId, next.Generation, default);
            Assert.Equal(ResourceReuseValidationStatus.Connectivity, result.ReuseStatus);
            Assert.Equal(DurableExecutionOutcome.RetryableFailure, result.StopOutcome);
            Assert.Equal(1, result.Progress!.AppliedGeneration);
        }
        await AssertOriginalReceipt(setup);
        await using var retry = Services(setup.Connection);
        Assert.Null((await retry.GetRequiredService<DurableGenerationRunner>().RunAsync(next.DeploymentId, next.Generation, default)).StopOutcome);
    }
    private sealed class UnavailableReuse(DurableActionAdmissionKey key) : IDurableResourceReuseVerifier
    {
        public DurableActionAdmissionKey Key => key;
        public ValueTask<ResourceReuseValidationStatus> VerifyAsync(AcceptedOperation original, ResourceImportReceipt receipt, CancellationToken token) =>
            ValueTask.FromResult(ResourceReuseValidationStatus.Connectivity);
    }

    private async Task<Setup> AcceptedPair(Pair pair)
    {
        var setup = await Prepare(pair, PostgreSqlTransportMode.Disable);
        await using var services = Services(setup.Connection);
        Assert.Null((await services.GetRequiredService<DurableGenerationRunner>().RunAsync(setup.Commit.DeploymentId, setup.Commit.Generation, default)).StopOutcome);
        return setup;
    }

    private static async Task<DurableChangeSetCommit> Reuse(Setup setup)
    {
        await using var services = Services(setup.Connection);
        var envelope = Declaration(setup.Plan.Inputs.Providers.Declaration.ApplicationId.Value);
        envelope["applicationVersion"] = "v2";
        var revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(),
            new([PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition], [], [], [new(PostgreSqlConnectionRendering.Renderer)]), TimeProvider.System)
            .ExecuteAsync(PostgreSqlPairPlanningTests.Bytes(envelope), "tester")).ManifestRevisionRef;
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(setup.Owner.EnvironmentRecordId, revision.ApplicationRecordId, revision,
            new DeploymentPrecondition.ExpectedGeneration(setup.Commit.Generation), "tester", default)).ChangeSet!;
        var planned = await planner.CreatePlanAsync(change.Id, "tester", default);
        Assert.Equal(DurablePlanStatus.Planned, planned.Status);
        Assert.Empty(planned.Plan!.Graph.Nodes);
        Assert.Equal(2, planned.Plan.Graph.ReusedResources.Count);
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(planned.Plan.Id, default));
        return Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>()
            .CommitAsync(change.Id, planned.Plan.Id, "tester", default)).Commit;
    }

    private static async Task AssertOriginalReceipt(Setup setup)
    {
        await using var services = Services(setup.Connection);
        await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.NotNull(await services.GetRequiredService<IResourceImportAcceptance>().FindAsync(setup.Key, default));
        Assert.Single(await db.ResourceImportReceipts.AsNoTracking().ToArrayAsync());
        Assert.Equal(2, await db.OperationOutputs.CountAsync());
        Assert.Equal(2, await db.DurableOperationAttempts.CountAsync());
    }
    private sealed class CountReuse : IPostgreSqlImportFaultHooks
    {
        public int Count;
        public ValueTask AfterReuseVerificationAsync(CancellationToken token) { Count++; return ValueTask.CompletedTask; }
    }
    private sealed class CancelReuse(CancellationTokenSource source) : IPostgreSqlImportFaultHooks
    {
        public ValueTask AfterReuseVerificationAsync(CancellationToken token) { source.Cancel(); return ValueTask.CompletedTask; }
    }
    private sealed class MutateCapturedInput(Setup setup) : IPostgreSqlImportFaultHooks
    {
        public async ValueTask AfterReuseVerificationAsync(CancellationToken token)
        {
            await using var services = Services(setup.Connection);
            await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync(token);
            // Synthetic concurrent writer changes a captured owner input while Provider I/O has no catalog lock.
            await db.ApplicationDeployments.ExecuteUpdateAsync(s => s.SetProperty(x => x.DesiredGeneration, 3), token);
        }
    }
}
