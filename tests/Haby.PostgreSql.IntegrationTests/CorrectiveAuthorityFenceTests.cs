using System.Data.Common;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Fact]
    public async Task Correction_winner_fences_stale_instance_before_attempt_and_dispatch_marker()
    {
        JsonNode? envelope = null;
        var scenario = await InternalScenarioAsync(combinedInitial: true, configureManifest: e => envelope = e.DeepClone());
        await using var owner = InternalServices(scenario.Connection, scenario.Time);
        Assert.NotNull((await owner.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default)).Progress!.ResourcesReady);
        var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        var initial = await PrepareRollout(owner, scenario, deployment, identity, configured: false);
        var pending = await initial.AcceptAsync(scenario.Commit, RuntimeSubject, deployment.Contract.Action, "stale-pending", default);
        var pause = new PauseCorrectiveOwnerLock();
        await using var stale = Services(scenario.Connection, scenario.Time, internalActions: true, commandInterceptor: pause);
        var old = ConfiguredActions(stale, deployment, identity).ExecuteAsync(pending, default).AsTask();
        DurableChangeSetCommit accepted;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            accepted = await AcceptFenceCorrectionAsync(owner, scenario, envelope!);
        }
        finally { pause.Release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => old);
        Assert.Equal(0, deployment.Writes);
        await using var read = await owner.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        var operation = await read.DurableOperations.AsNoTracking().SingleAsync(x => x.Generation == scenario.Commit.Generation.Value && x.OperationId == pending.OperationId.Value);
        Assert.Equal("Pending", operation.State); Assert.Equal(0, operation.AttemptNumber); Assert.Null(operation.CurrentAttemptId); Assert.Null(operation.DispatchedAt);
        Assert.False(await read.DurableOperationAttempts.AnyAsync(x => x.Generation == scenario.Commit.Generation.Value && x.OperationId == pending.OperationId.Value));
        Assert.Equal(accepted.Generation.Value, (await read.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
    }

    [Fact]
    public async Task Correction_winner_allows_old_read_closure_without_stale_ready_or_Applied_promotion()
    {
        JsonNode? envelope = null;
        var scenario = await InternalScenarioAsync(combinedInitial: true, configureManifest: e => envelope = e.DeepClone());
        await using var owner = InternalServices(scenario.Connection, scenario.Time);
        Assert.NotNull((await owner.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default)).Progress!.ResourcesReady);
        var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        var initial = await PrepareRollout(owner, scenario, deployment, identity, configured: false);
        await SubmitRollout(initial, scenario, deployment);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        deployment.BeforeRead = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var old = initial.ObserveRolloutAsync(scenario.Commit, "inflight-old-read", OneRound, default).AsTask();
        DurableChangeSetCommit accepted;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var independent = InternalServices(scenario.Connection, scenario.Time);
            accepted = await AcceptFenceCorrectionAsync(independent, scenario, envelope!);
        }
        finally { release.TrySetResult(); }
        var closed = await old;
        Assert.False(closed.Ready); Assert.False(closed.Applied);
        await using var read = await owner.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.False(await read.GenerationStageCheckpoints.AnyAsync(x => x.Generation == scenario.Commit.Generation.Value &&
            (x.Stage == "RolloutReady" || x.Stage == InitialRetirementCompletion.Stage)));
        Assert.True(await read.ObservationRecords.AnyAsync(x => x.Generation == scenario.Commit.Generation.Value && x.Kind == "Read" && x.FinishedAt != null));
        var current = await read.ApplicationDeployments.AsNoTracking().SingleAsync();
        Assert.Equal(accepted.Generation.Value, current.DesiredGeneration); Assert.Null(current.AppliedGeneration);
    }

    [Theory]
    [InlineData("preparation")]
    [InlineData("publication")]
    public async Task Correction_winner_fences_stale_preparation_and_publication_acceptance(string stage)
    {
        JsonNode? envelope = null;
        var scenario = await InternalScenarioAsync(combinedInitial: true, configureManifest: e =>
        { ConfiguredDocuments(e); envelope = e.DeepClone(); });
        await using var owner = InternalServices(scenario.Connection, scenario.Time);
        Assert.NotNull((await owner.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default)).Progress!.ResourcesReady);
        var identity = new IdentityFixture();
        if (stage == "publication")
        {
            Assert.Null((await ConfigurationPreparation(owner).Preparation.PrepareAsync(scenario.Commit, "operator", default)).Failure);
            Assert.All(await IdentityPreparation(owner, identity).PrepareAsync(scenario.Commit, default), x => Assert.Equal(InternalActionOutcome.Confirmed, x.Result.Outcome));
        }
        var pause = new PauseCorrectiveOwnerLock();
        await using var stale = Services(scenario.Connection, scenario.Time, internalActions: true, commandInterceptor: pause);
        async Task OldAcceptanceAsync()
        {
            if (stage == "preparation")
            {
                var result = await ConfigurationPreparation(stale).Preparation.PrepareAsync(scenario.Commit, "operator", default);
                Assert.NotNull(result.Failure);
            }
            else
            {
                var result = await new InternalRuntimePublicationPreparation(Publications(stale, identity)).PrepareAsync(scenario.Commit, "operator", default);
                Assert.NotNull(result.Reason);
            }
        }
        var old = OldAcceptanceAsync();
        DurableChangeSetCommit accepted;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            accepted = await AcceptFenceCorrectionAsync(owner, scenario, envelope!);
        }
        finally { pause.Release.TrySetResult(); }
        await old;
        await using var read = await owner.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.False(await read.RuntimeConfigurationPermissions.AnyAsync());
        var receipts = await read.RuntimePublicationReceipts.AsNoTracking().ToArrayAsync();
        Assert.DoesNotContain(receipts, x => InternalRuntimePublication.ReadReceipt(x).Outcome == RuntimePublicationOutcome.Published);
        if (stage == "preparation")
        {
            Assert.False(await read.ConfigurationDocuments.AnyAsync(x => x.Generation == scenario.Commit.Generation.Value));
            Assert.False(await read.WorkloadConfigurationRecipients.AnyAsync(x => x.Generation == scenario.Commit.Generation.Value));
        }
        Assert.Equal(accepted.Generation.Value, (await read.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
    }

    [Fact]
    public async Task Competing_corrective_commits_from_independent_instances_have_one_durable_winner()
    {
        JsonNode? envelope = null;
        var scenario = await InternalScenarioAsync(combinedInitial: true, configureManifest: e => envelope = e.DeepClone());
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        Assert.NotNull((await services.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default)).Progress!.ResourcesReady);
        var deployment = new ConfiguredSubmissionFixture();
        var actions = await PrepareRollout(services, scenario, deployment, new IdentityFixture(), configured: false);
        var pending = await actions.AcceptAsync(scenario.Commit, RuntimeSubject, deployment.Contract.Action, "competing-pending", default);
        envelope!["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:competing-correction";
        var revision = await ImportUpdate(services, envelope, "competing-correction");
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        async Task<(DeploymentChangeSet Change, DurablePlan Plan)> CaptureAsync()
        {
            var change = (await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, revision.ApplicationRecordId, revision,
                new(scenario.Commit.DeploymentId, scenario.Commit.Generation), Guid.CreateVersion7(), "operator", default)).ChangeSet!;
            var plan = await planner.CreateOperatorPlanAsync(change.Id, "operator", default, handlers);
            Assert.Equal(DurablePlanStatus.Planned, plan.Status); return (change, plan.Plan!);
        }
        var first = await CaptureAsync(); var second = await CaptureAsync();
        await using var a = InternalServices(scenario.Connection, scenario.Time);
        await using var b = InternalServices(scenario.Connection, scenario.Time);
        var outcomes = await Task.WhenAll(
            ((PostgreSqlDurableDesiredStateCommitter)a.GetRequiredService<IDurableDesiredStateCommitter>()).CommitOperatorAsync(first.Change.Id, first.Plan.Id, "operator", default, handlers).AsTask(),
            ((PostgreSqlDurableDesiredStateCommitter)b.GetRequiredService<IDurableDesiredStateCommitter>()).CommitOperatorAsync(second.Change.Id, second.Plan.Id, "operator", default, handlers).AsTask());
        var winner = Assert.Single(outcomes.OfType<DurableCommitResult.Committed>()).Commit;
        Assert.IsType<DurableCommitResult.ChangeSetStale>(Assert.Single(outcomes, x => x is not DurableCommitResult.Committed));
        Assert.Equal(scenario.Commit.Generation.Value + 1, winner.Generation.Value); Assert.Equal(0, deployment.Writes);
        await using var read = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(2, await read.DeploymentGenerations.CountAsync());
        Assert.Equal(1, await read.ChangeSetCommits.CountAsync(x => x.ChangeSetId == Guid.Parse(first.Change.Id.ToString()) || x.ChangeSetId == Guid.Parse(second.Change.Id.ToString())));
        var owner = await read.ApplicationDeployments.AsNoTracking().SingleAsync(); Assert.Equal(winner.Generation.Value, owner.DesiredGeneration); Assert.Null(owner.AppliedGeneration);
        var original = await read.DurableOperations.AsNoTracking().SingleAsync(x => x.OperationId == pending.OperationId.Value && x.Generation == scenario.Commit.Generation.Value);
        Assert.Equal("Pending", original.State); Assert.Equal(0, original.AttemptNumber); Assert.Null(original.DispatchedAt);
    }

    private static async Task<DurableChangeSetCommit> AcceptFenceCorrectionAsync(ServiceProvider services, InternalScenario scenario, JsonNode envelope)
    {
        envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:fenced-correction";
        var revision = await ImportUpdate(services, envelope, "fenced-correction");
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var change = (await planner.CreateCorrectiveChangeSetAsync(scenario.Environment, revision.ApplicationRecordId, revision,
            new(scenario.Commit.DeploymentId, scenario.Commit.Generation), Guid.CreateVersion7(), "operator", default)).ChangeSet!;
        var plan = await planner.CreateOperatorPlanAsync(change.Id, "operator", default, handlers);
        Assert.Equal(DurablePlanStatus.Planned, plan.Status);
        return Assert.IsType<DurableCommitResult.Committed>(await ((PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>())
            .CommitOperatorAsync(change.Id, plan.Plan!.Id, "operator", default, handlers)).Commit;
    }

    private sealed class PauseCorrectiveOwnerLock : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int paused;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken token = default)
        {
            if (command.CommandText.StartsWith("SELECT id FROM application_deployments", StringComparison.Ordinal) && Interlocked.Exchange(ref paused, 1) == 0)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return result;
        }
    }
}
