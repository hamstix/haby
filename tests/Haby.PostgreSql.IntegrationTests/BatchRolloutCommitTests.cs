using System.Data.Common;
using System.Text.Json;
using Haby.Application;
using Haby.Operator.Contracts;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Theory]
    [InlineData(0)] // Semantic finalization boundary, independent of save/commit ordinals.
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(-1)] // Failure while saving the new completion checkpoint.
    public async Task Batch_commit_expiry_after_save_prevents_new_commit_admission(int commit)
    {
        // Positive values select save ordinals here; they are not transaction commit ordinals.
        var scenario = await RolloutScenario(configured: false); var deployment = new ConfiguredSubmissionFixture { Pending = commit == 4 }; var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var arranged = await PrepareRollout(services, scenario, deployment, identity, configured: false); await SubmitRollout(arranged, scenario, deployment);
        var clock = new CommitClock(scenario.Time); var control = new CommitControl(clock, commit, before: true);
        var observer = CommitFacade(services, scenario, deployment, identity, clock, control);
        var result = await observer.WaitBatchAsync("before-commit", "operator", [BatchMember(scenario.Commit, "before-commit")], null,
            new(MaximumRounds: 1), null, default).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(control.TargetReached); Assert.Equal(0, control.CommitsAfterExpiry);
        Assert.False(result.CurrentReady); Assert.Equal(commit == -1 ? GenerationReadinessOutcome.Unknown : GenerationReadinessOutcome.Interrupted, result.Members[0].Outcome);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.False(await verify.GenerationStageCheckpoints.AsNoTracking().AnyAsync(x => x.Stage == "RolloutReady"));
        await AssertAtomicCompletionAsync(verify, "before-commit", present: false);
        Assert.Equal(1, deployment.Writes); Assert.Equal(0, identity.Writes);
    }

    [Theory]
    [InlineData(0)] // Semantic finalization boundary, independent of save/commit ordinals.
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
        await using (var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.True(await verify.ObservationRecords.AsNoTracking().AnyAsync(x => x.Request == BatchMember(scenario.Commit, "during-commit").RequestId));
            await AssertAtomicCompletionAsync(verify, "during-commit", present: control.HeldCompletion);
        }
        if (commit == 0) Assert.True(control.HeldCompletion);
        // Confirmed persisted state can exist despite an interrupted response; cancellation never implies rollback.
        if (control.HeldCompletion)
        {
            await using var independent = InternalServices(scenario.Connection, scenario.Time);
            var replay = await BatchFacade(independent, scenario, ConfiguredActions(independent, deployment, identity), deployment, identity)
                .WaitBatchAsync("during-commit", "operator", [BatchMember(scenario.Commit, "during-commit")], null, new(MaximumRounds: 1), null, default);
            var member = Assert.Single(replay.Members); Assert.True(member.Replayed); Assert.True(member.OriginalRequestApplied); Assert.False(member.CurrentApplied);
            Assert.Equal(1, deployment.ReadinessReads);
        }
        Assert.Equal(1, deployment.Writes); Assert.Equal(0, identity.Writes);
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

    private static async Task AssertAtomicCompletionAsync(ManifestCatalogDbContext verify, string batch, bool present)
    {
        var source = await verify.DeploymentGenerations.AsNoTracking().SingleAsync();
        var owner = await verify.ApplicationDeployments.AsNoTracking().SingleAsync();
        var checkpoints = await verify.GenerationStageCheckpoints.AsNoTracking().Where(x => x.Stage == "RetirementCompleted").ToArrayAsync();
        var request = await verify.ObservationRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Kind == "Request");
        var result = request?.Payload is { } payload ? RolloutJson.Decode(payload) : null;
        if (!present)
        {
            Assert.Null(owner.AppliedGeneration); Assert.Empty(checkpoints); Assert.False(result?.Applied ?? false);
            Assert.Null(result?.Retirement);
            return;
        }
        var checkpoint = Assert.Single(checkpoints); Assert.Equal(source.Generation, owner.AppliedGeneration);
        Assert.NotNull(request); Assert.NotNull(request.FinishedAt); Assert.NotNull(result);
        Assert.True(result.Applied); Assert.True(result.Ready); Assert.Equal(RolloutStop.Complete, result.Stop);
        Assert.Equal(checkpoint.Id, result.Retirement); Assert.Equal(source.Generation, result.AppliedGeneration);
        Assert.Equal(BatchObservationKeys.Member(batch, source.DeploymentId.ToString(), source.Generation), request.Request);
        var proof = await InitialRetirementCompletion.RequireProofAsync(verify, source, default);
        Assert.Equal(checkpoint.Id, proof.Id); Assert.Equal(result.Submitted, proof.Submitted); Assert.Equal(result.SavedReady, proof.SavedReady);
        Assert.Equal(request.Request, proof.Request);
        Assert.Equal(JsonSerializer.Deserialize(request.InputJson, RolloutMetadata.Default.RolloutRequest)!.Actor, proof.Actor);
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
        internal bool TargetReached, CompletionSave, CompletionTransaction, HeldCompletion;
        internal CancellationToken ObservedToken;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class CommitSaveProbe(CommitControl control) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            control.CompletionSave = eventData.Context!.ChangeTracker.Entries<GenerationStageCheckpointRow>()
                .Any(x => x.State == EntityState.Added && x.Entity.Stage == "RetirementCompleted");
            if (control.CompletionSave && control.Target == -1)
            { control.TargetReached = true; throw new InvalidOperationException("injected finalization save failure"); }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref control.Saves);
            control.CompletionTransaction |= control.CompletionSave;
            if ((control.Target == 0 ? control.CompletionSave : ordinal == control.Target) && control.Before)
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
            var ordinal = Interlocked.Increment(ref control.Commits);
            var completion = control.CompletionTransaction; control.CompletionTransaction = false;
            if ((control.Target == 0 ? completion : ordinal == control.Target) && !control.Before)
            {
                control.HeldCompletion = completion;
                control.TargetReached = true; control.ObservedToken = cancellationToken; control.Entered.TrySetResult();
                await control.Release.Task.WaitAsync(cancellationToken);
            }
        }
    }
}
