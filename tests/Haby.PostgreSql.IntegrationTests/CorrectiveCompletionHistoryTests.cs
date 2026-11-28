using Haby.Application;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Fact]
    public async Task Completed_UPDATE_proof_reads_immutable_receipts_after_latest_projection_changes()
    {
        var (scenario, envelope) = await AppliedUpdateBaseline(true, configured: false);
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:v2";
        var change = await UpdateChange(services, scenario, await ImportUpdate(services, envelope));
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var plan = (await planner.CreateInternalPlanAsync(change.Id, "tester", default, handlers)).Plan!;
        var accepted = Assert.IsType<DurableCommitResult.Committed>(await ((PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>())
            .CommitInternalAsync(change.Id, plan.Id, "tester", default, handlers)).Commit;
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var configuration = ConfigurationPreparation(services).Preparation;
        var update = new UpdateSubmissionFixture();
        var actions = new InternalDurableActions(contexts, [new ConfiguredSubmissionFixture(), new IdentityFixture(), update],
            services.GetServices<IProviderConfigurationCodec>(), services.GetServices<IExternalOperationOutputContract>(),
            services.GetRequiredService<DurableActionAdmission>(), services.GetRequiredService<DurableOperationSerialization>(), scenario.Time,
            configurationPreparation: configuration);
        Assert.Null((await configuration.PrepareAsync(accepted, "tester", default)).Failure);
        var key = await actions.AcceptAsync(accepted, RuntimeSubject, update.Contract.Action, "history-update", default);
        Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(key, default)).Outcome);
        var applied = await actions.WaitUpdateAsync(accepted, "history-complete", "tester", OneRound, new(), default);
        Assert.True(applied.Applied, applied.CompletionReason);
        await using (var mutate = await contexts.CreateDbContextAsync())
        {
            // Remove only the mutable latest projection. Original intents, outputs,
            // attempts and immutable completion evidence remain byte-for-byte intact.
            await mutate.ManagedObjectStates.ExecuteDeleteAsync();
        }
        await using var read = await contexts.CreateDbContextAsync();
        var source = await read.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == accepted.Generation.Value);
        var original = await UpdateRetirementCompletion.RequireProofAsync(read, source, default);
        Assert.Equal(applied.Retirement, original.Id);
    }
}
