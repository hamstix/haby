using System.Data.Common;
using Haby.Application;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Batch_commit_expiry_after_save_prevents_new_commit_admission(int commit)
    {
        var scenario = await RolloutScenario(configured: false); var deployment = new ConfiguredSubmissionFixture { Pending = commit == 4 }; var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var arranged = await PrepareRollout(services, scenario, deployment, identity, configured: false); await SubmitRollout(arranged, scenario, deployment);
        var clock = new CommitClock(scenario.Time); var control = new CommitControl(clock, commit, before: true);
        var observer = CommitFacade(services, scenario, deployment, identity, clock, control);
        var result = await observer.WaitBatchAsync("before-commit", "operator", [BatchMember(scenario.Commit, "before-commit")], null,
            new(MaximumRounds: 1), null, default).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(control.TargetReached); Assert.Equal(0, control.CommitsAfterExpiry);
        Assert.False(result.CurrentReady); Assert.Equal(GenerationReadinessOutcome.Interrupted, result.Members[0].Outcome);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.False(await verify.GenerationStageCheckpoints.AsNoTracking().AnyAsync(x => x.Stage == "RolloutReady"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Batch_commit_inflight_cancellation_is_bounded_and_does_not_claim_rollback(int commit)
    {
        var scenario = await RolloutScenario(configured: false); var deployment = new ConfiguredSubmissionFixture { Pending = commit == 4 }; var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var arranged = await PrepareRollout(services, scenario, deployment, identity, configured: false); await SubmitRollout(arranged, scenario, deployment);
        var clock = new CommitClock(scenario.Time); var control = new CommitControl(clock, commit, before: false);
        var observer = CommitFacade(services, scenario, deployment, identity, clock, control);
        using var caller = new CancellationTokenSource();
        var operation = observer.WaitBatchAsync("during-commit", "operator", [BatchMember(scenario.Commit, "during-commit")], null,
            new(MaximumRounds: 1), null, caller.Token).AsTask();
        BatchObservationResult? canceledResult = null;
        try
        {
            await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The test driver already committed, but CommitAsync has not returned to the observer.
            clock.Expire(); caller.Cancel();
            try { canceledResult = await operation.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { } // Preserve a bounded red result against the old uncancelable commit.
        }
        finally
        {
            caller.Cancel(); control.Release.TrySetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(10)); // Never strand a test-owned commit/worker.
        }
        Assert.NotNull(canceledResult); Assert.True(control.ObservedToken.CanBeCanceled); Assert.True(control.ObservedToken.IsCancellationRequested);
        Assert.False(canceledResult.CurrentReady); Assert.Equal(GenerationReadinessOutcome.Interrupted, canceledResult.Members[0].Outcome);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.True(await verify.ObservationRecords.AsNoTracking().AnyAsync(x => x.Request == BatchMember(scenario.Commit, "during-commit").RequestId));
        if (commit == 3) Assert.True(await verify.GenerationStageCheckpoints.AsNoTracking().AnyAsync(x => x.Stage == "RolloutReady"));
        // Confirmed persisted state can exist despite an interrupted response; cancellation never implies rollback.
    }

    [Fact]
    public async Task Batch_commit_policy_preserves_single_generation_commit_and_closure_behavior()
    {
        var scenario = await RolloutScenario(configured: false); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var arranged = await PrepareRollout(services, scenario, deployment, identity, configured: false); await SubmitRollout(arranged, scenario, deployment);
        var clock = new CommitClock(scenario.Time); var control = new CommitControl(clock, 1, before: false);
        var observer = CommitFacade(services, scenario, deployment, identity, clock, control);
        using var caller = new CancellationTokenSource();
        var operation = observer.WaitAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, "single-commit", "operator",
            new(MaximumRounds: 1), null, caller.Token).AsTask();
        try
        {
            await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel();
            Assert.False(control.ObservedToken.CanBeCanceled);
        }
        finally { control.Release.TrySetResult(); await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.False((await operation).CurrentReady);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.NotNull((await verify.ObservationRecords.AsNoTracking().SingleAsync(x => x.Kind == "Request")).Payload);
    }

    private static PostgreSqlOperatorRolloutObservation CommitFacade(ServiceProvider services, InternalScenario scenario,
        ConfiguredSubmissionFixture deployment, IdentityFixture identity, CommitClock clock, CommitControl control)
    {
        var contexts = new CommitFactory(scenario.Connection, new CommitSaveProbe(control), new CommitTransactionProbe(control));
        var configuration = ConfigurationPreparation(services).Preparation;
        var actions = new InternalDurableActions(contexts, [deployment, identity], services.GetServices<IProviderConfigurationCodec>(),
            services.GetServices<IExternalOperationOutputContract>(), services.GetRequiredService<DurableActionAdmission>(),
            services.GetRequiredService<DurableOperationSerialization>(), clock, configurationPreparation: configuration);
        return new(contexts, configuration, actions, new(PollMilliseconds: 100), clock, deployment.Contract.Action, identity.Contract.Action);
    }
    private sealed class CommitFactory(string connection, params IInterceptor[] interceptors) : IDbContextFactory<ManifestCatalogDbContext>
    {
        public ManifestCatalogDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>()
            .UseNpgsql(connection).AddInterceptors(interceptors).Options);
    }
    private sealed class CommitClock(TimeProvider source) : TimeProvider
    {
        private long offset;
        public override DateTimeOffset GetUtcNow() => source.GetUtcNow();
        public override long GetTimestamp() => base.GetTimestamp() + Interlocked.Read(ref offset);
        internal void Expire() => Interlocked.Add(ref offset, 31 * TimestampFrequency);
    }
    private sealed class CommitControl(CommitClock clock, int target, bool before)
    {
        internal readonly CommitClock Clock = clock;
        internal readonly int Target = target;
        internal readonly bool Before = before;
        internal int Saves, Commits, CommitsAfterExpiry;
        internal bool TargetReached;
        internal CancellationToken ObservedToken;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class CommitSaveProbe(CommitControl control) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref control.Saves) == control.Target && control.Before)
            { control.TargetReached = true; control.Clock.Expire(); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CommitTransactionProbe(CommitControl control) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (control.Before && control.TargetReached) Interlocked.Increment(ref control.CommitsAfterExpiry);
            return ValueTask.FromResult(result);
        }
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref control.Commits) == control.Target && !control.Before)
            {
                control.TargetReached = true; control.ObservedToken = cancellationToken; control.Entered.TrySetResult();
                await control.Release.Task.WaitAsync(cancellationToken);
            }
        }
    }
}
