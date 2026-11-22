using System.Data.Common;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlPairImportTests
{
    [Theory]
    [InlineData("before-transfer")]
    [InlineData("after-transfer")]
    [InlineData("lost-response")]
    public async Task Correction_transaction_faults_preserve_old_authority_or_one_recoverable_commit(string phase)
    {
        var pair = await ExistingPair();
        var setup = await Prepare(pair, PostgreSqlTransportMode.Disable, password: Guid.NewGuid().ToString("N"), operatorRequest: true);
        ImportDraft replacement;
        await using (var arrange = Services(setup.Connection))
        {
            Assert.Equal(DurableExecutionOutcome.Failed, (await arrange.GetRequiredService<DurableGenerationRunner>()
                .RunAsync(setup.Commit.DeploymentId, setup.Commit.Generation, default)).StopOutcome);
            var previous = new OperatorImportRequestBinding(setup.Request, setup.Owner.EnvironmentRecordId, setup.Plan.Inputs.Revision,
                setup.Owner.ResourceLocalId, new(setup.Plan.Inputs.Import!.Resources.Select(x => x with { Credential = null })));
            replacement = await NewCorrection(arrange, pair, previous, setup.Commit);
        }
        var fault = new CorrectionTransferFault(phase);
        var lost = new CorrectionResponseFault(replacement.Change!.Id, phase == "lost-response");
        await using (var writer = Services(setup.Connection, configure: s => s.AddSingleton<IDbContextFactory<ManifestCatalogDbContext>>(
            new CorrectionFactory(setup.Connection, fault, lost))))
            await Assert.ThrowsAsync<IOException>(() => writer.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(replacement.Change.Id, replacement.Plan!.Id, "original-committer", default).AsTask());
        Assert.True(phase == "lost-response" ? lost.Thrown : fault.Thrown);
        await using var restart = Services(setup.Connection);
        var committer = restart.GetRequiredService<IDurableDesiredStateCommitter>();
        var recovered = await committer.FindCommitAsync(replacement.Change.Id, default);
        if (phase == "lost-response") Assert.NotNull(recovered);
        else
        {
            Assert.Null(recovered);
            await using var verify = await restart.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.Single(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.ImportSupersessions.AsNoTracking().ToArrayAsync());
            Assert.Equal(setup.Staged.Id, (await verify.ProtectedCredentialOwners.AsNoTracking().SingleAsync()).Stage);
            Assert.All(await verify.CurrentResourceManagement.AsNoTracking().ToArrayAsync(), x => Assert.Equal(1, x.SourceGeneration));
            Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
        }
        var accepted = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(replacement.Change.Id,
            replacement.Plan!.Id, "retry-committer", default)).Commit;
        if (recovered is not null) Assert.Equal(recovered, accepted);
        Assert.Equal(2, accepted.Generation.Value);
        await AssertCurrentCorrection(restart, new(replacement.Binding with { RequestId = setup.Request, Correction = null }, null, null, setup.Staged), replacement, accepted);
        await using (var verify = await restart.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(2, await verify.DeploymentGenerations.CountAsync());
            Assert.Equal(2, await verify.ChangeSetCommits.CountAsync());
            Assert.Equal(4, await verify.ResourceManagement.CountAsync());
            Assert.Equal(phase == "lost-response" ? "original-committer" : "retry-committer",
                (await verify.ImportSupersessions.AsNoTracking().SingleAsync()).Actor);
        }
        using var different = Material(Guid.NewGuid().ToString("N"));
        Assert.Equal(OperatorImportRequestStatus.Conflict, (await restart.GetRequiredService<OperatorImportRequests>()
            .CreateAsync(replacement.Binding, different, "changed-replay", default)).Status);
        Assert.Null((await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(accepted.DeploymentId, accepted.Generation, default)).StopOutcome);
    }

    private sealed class CorrectionTransferFault(string phase) : DbCommandInterceptor
    {
        public bool Thrown { get; private set; }
        private bool Transfer(DbCommand command) => !Thrown && command.CommandText.StartsWith("UPDATE protected_application_credential_owners", StringComparison.Ordinal);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (phase == "before-transfer" && Transfer(command)) { Thrown = true; throw new IOException("Injected before authority transfer."); }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (phase == "after-transfer" && Transfer(command)) { Thrown = true; throw new IOException("Injected after authority transfer."); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CorrectionResponseFault(DeploymentChangeSetId change, bool enabled) : DbTransactionInterceptor
    {
        public bool Thrown { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (enabled && !Thrown && eventData.Context!.ChangeTracker.Entries<ChangeSetCommitRow>().Any(x => x.Entity.ChangeSetId == Guid.Parse(change.ToString())))
            { Thrown = true; throw new IOException("Injected lost correction commit response."); }
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("precommit")]
    [InlineData("pending-binding")]
    [InlineData("competing-corrections")]
    [InlineData("old-commit-race")]
    [InlineData("other-operation-active")]
    [InlineData("verification-active")]
    [InlineData("late-verification")]
    [InlineData("finished-uncertain")]
    [InlineData("correction-fences-begin")]
    public async Task Explicit_correction_serializes_pending_ownership_execution_and_commit(string scenario)
    {
        var pair = await ExistingPair();
        var setup = await Prepare(pair, PostgreSqlTransportMode.Disable, operatorRequest: true);
        if (scenario is "other-operation-active" or "verification-active" or "late-verification" or "finished-uncertain" or "correction-fences-begin")
        {
            await ActiveCorrectionCase(setup, pair, scenario);
            return;
        }
        await using var services = Services(setup.Connection);
        var original = await NewDraft(services, pair, setup.Owner.EnvironmentRecordId, scenario == "pending-binding");
        var replacement = await NewCorrection(services, pair, original.Binding, null);
        var committer = services.GetRequiredService<IDurableDesiredStateCommitter>();
        if (scenario == "competing-corrections")
        {
            var rival = await NewCorrection(services, pair, original.Binding, null);
            var results = await Task.WhenAll(committer.CommitAsync(replacement.Change!.Id, replacement.Plan!.Id, "first", default).AsTask(),
                committer.CommitAsync(rival.Change!.Id, rival.Plan!.Id, "second", default).AsTask());
            var winner = Assert.Single(results.OfType<DurableCommitResult.Committed>());
            Assert.Single(results.OfType<DurableCommitResult.ChangeSetStale>());
            var chosen = winner.Commit.ChangeSetId == replacement.Change.Id ? replacement : rival;
            var loser = ReferenceEquals(chosen, replacement) ? rival : replacement;
            using var unavailable = await services.GetRequiredService<IProtectedApplicationCredentialIntake>()
                .ResolveStagedAsync(loser.Credential!, loser.Binding.CredentialOwner, default);
            Assert.Null(unavailable);
            await AssertCurrentCorrection(services, original, chosen, winner.Commit);
        }
        else if (scenario == "old-commit-race")
        {
            var results = await Task.WhenAll(committer.CommitAsync(original.Change!.Id, original.Plan!.Id, "original", default).AsTask(),
                committer.CommitAsync(replacement.Change!.Id, replacement.Plan!.Id, "correcting", default).AsTask());
            var winner = Assert.Single(results.OfType<DurableCommitResult.Committed>());
            Assert.Single(results.OfType<DurableCommitResult.ChangeSetStale>());
            if (winner.Commit.ChangeSetId == replacement.Change.Id) await AssertCurrentCorrection(services, original, replacement, winner.Commit);
            else
            {
                Assert.Equal(original.Change.Id, winner.Commit.ChangeSetId);
                Assert.Equal(OperatorImportRequestStatus.Created, (await services.GetRequiredService<OperatorImportRequests>().GetAsync(original.Binding.RequestId, default)).Status);
                await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
                Assert.Empty(await db.ImportSupersessions.AsNoTracking().ToArrayAsync());
                using var unavailable = await services.GetRequiredService<IProtectedApplicationCredentialIntake>()
                    .ResolveStagedAsync(replacement.Credential!, replacement.Binding.CredentialOwner, default);
                Assert.Null(unavailable);
            }
        }
        else
        {
            // Direct legacy creation cannot execute an inactive candidate through a different ChangeSet.
            var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
            var legacy = (await planner.CreateImportChangeSetAsync(replacement.Binding.Environment, replacement.Binding.Revision.ApplicationRecordId,
                replacement.Binding.Revision, replacement.Binding.Attach(replacement.Credential!), "legacy", default)).ChangeSet!;
            Assert.Equal(DurablePlanStatus.ChangeSetStale, (await planner.CreatePlanAsync(legacy.Id, "legacy", default)).Status);
            var accepted = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(replacement.Change!.Id,
                replacement.Plan!.Id, "correcting", default)).Commit;
            await AssertCurrentCorrection(services, original, replacement, accepted);
            if (original.Change is not null)
                Assert.IsType<DurableCommitResult.ChangeSetStale>(await committer.CommitAsync(original.Change.Id, original.Plan!.Id, "late", default));
            else
            {
                using var material = Material(pair.Password);
                Assert.Equal(ProtectedCredentialStageStatus.Conflict, (await services.GetRequiredService<IProtectedApplicationCredentialIntake>()
                    .StageAsync(original.Binding.RequestId, original.Binding.CredentialOwner, material, default)).Status);
            }
            Assert.Null((await services.GetRequiredService<DurableGenerationRunner>().RunAsync(accepted.DeploymentId, accepted.Generation, default)).StopOutcome);
        }
    }

    private sealed record ImportDraft(OperatorImportRequestBinding Binding, DeploymentChangeSet? Change, DurablePlan? Plan,
        ProtectedCredentialReference? Credential);

    private async Task<ImportDraft> NewDraft(ServiceProvider services, Pair pair, EnvironmentRecordId environment, bool bindOnly = false)
    {
        var revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(),
            new([PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition], [], [], [new(PostgreSqlConnectionRendering.Renderer)]), TimeProvider.System)
            .ExecuteAsync(PostgreSqlPairPlanningTests.Bytes(Declaration("draft-" + Guid.NewGuid().ToString("N")[..12])), "tester")).ManifestRevisionRef;
        var prepared = await services.GetRequiredService<PrepareResourceImport>().ExecuteAsync(new(Guid.NewGuid(), environment, revision,
            PostgreSqlImportPreparer.Encode(new(pair.Database, pair.Login, pair.Input.Schema))), default);
        Assert.Equal(ResourceImportPreparationStatus.Prepared, prepared.Status);
        if (bindOnly)
        {
            Assert.Equal(OperatorImportRequestStatus.Pending, (await services.GetRequiredService<IOperatorImportRequestStore>()
                .BindAsync(prepared.Binding!, "original", default)).Status);
            return new(prepared.Binding!, null, null, null);
        }
        return await LinkDraft(services, prepared.Binding!, pair.Password);
    }

    private static async Task<ImportDraft> NewCorrection(ServiceProvider services, Pair pair, OperatorImportRequestBinding previous, DurableChangeSetCommit? expected)
    {
        var prepared = await services.GetRequiredService<PrepareResourceImport>().ExecuteAsync(new(Guid.NewGuid(), previous.Environment, previous.Revision,
            PostgreSqlImportPreparer.Encode(new(pair.Database, pair.Login, pair.Input.Schema))), default);
        Assert.Equal(ResourceImportPreparationStatus.Prepared, prepared.Status);
        return await LinkDraft(services, prepared.Binding! with { Correction = new(previous.RequestId, expected?.DeploymentId, expected?.Generation,
            OperatorImportCorrectionReason.InputCorrection) }, pair.Password);
    }

    private static async Task<ImportDraft> LinkDraft(ServiceProvider services, OperatorImportRequestBinding binding, string password)
    {
        using var material = Material(password);
        var created = await services.GetRequiredService<OperatorImportRequests>().CreateAsync(binding, material, "linking", default);
        Assert.Equal(OperatorImportRequestStatus.Created, created.Status);
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.FindChangeSetAsync(created.Request!.ChangeSetId!, default))!;
        var plan = await planner.CreatePlanAsync(change.Id, "planning", default);
        Assert.Equal(DurablePlanStatus.Planned, plan.Status);
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(plan.Plan!.Id, default));
        return new(binding, change, plan.Plan, change.Import!.Resources.Single(x => x.Resource == binding.CredentialResource).Credential!);
    }

    private static async Task AssertCurrentCorrection(ServiceProvider services, ImportDraft previous, ImportDraft replacement, DurableChangeSetCommit commit)
    {
        var result = await services.GetRequiredService<OperatorImportRequests>().GetAsync(previous.Binding.RequestId, default);
        Assert.Equal(OperatorImportRequestStatus.Superseded, result.Status);
        Assert.Equal(replacement.Binding.RequestId, result.Request!.SupersededByRequestId);
        if (previous.Credential is not null)
        {
            using var old = await services.GetRequiredService<IProtectedApplicationCredentialIntake>()
                .ResolveStagedAsync(previous.Credential, previous.Binding.CredentialOwner, default);
            Assert.Null(old);
        }
        await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        var application = Guid.Parse(replacement.Binding.Revision.ApplicationRecordId.ToString());
        var owner = await db.ProtectedCredentialOwners.AsNoTracking().SingleAsync(x => x.Application == application);
        Assert.Equal(replacement.Credential!.Id, owner.Stage);
        Assert.Equal(replacement.Binding.RequestId, owner.Request);
        Assert.Single(await db.ImportSupersessions.AsNoTracking().Where(x => x.PreviousRequest == previous.Binding.RequestId).ToArrayAsync());
        Assert.Equal(commit.Generation.Value, (await db.ApplicationDeployments.AsNoTracking().SingleAsync(x => x.ApplicationRecordId == application)).DesiredGeneration);
    }

    private async Task ActiveCorrectionCase(Setup setup, Pair pair, string scenario)
    {
        await using var services = Services(setup.Connection);
        var request = await services.GetRequiredService<OperatorImportRequests>().GetAsync(setup.Request, default);
        var previous = new OperatorImportRequestBinding(setup.Request, setup.Owner.EnvironmentRecordId, setup.Plan.Inputs.Revision, setup.Owner.ResourceLocalId,
            new(setup.Plan.Inputs.Import!.Resources.Select(x => x with { Credential = null })));
        Assert.Equal(OperatorImportRequestStatus.Created, request.Status);
        var replacement = await NewCorrection(services, pair, previous, setup.Commit);
        if (scenario == "correction-fences-begin")
        {
            await CorrectionFencesBegin(setup, replacement);
            return;
        }
        if (scenario is "verification-active" or "late-verification")
        {
            var pause = new PauseVerification();
            await using var running = Services(setup.Connection, pause);
            var task = running.GetRequiredService<DurableGenerationRunner>().RunAsync(setup.Commit.DeploymentId, setup.Commit.Generation, default).AsTask();
            try
            {
                await pause.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
                if (scenario == "late-verification")
                {
                    // A second executor can close the exact read-only attempts while the first
                    // verifier is physically paused. Its late observation must not publish.
                    var journal = services.GetRequiredService<IDurableOperationJournal>();
                    foreach (var node in setup.Plan.Graph.Nodes)
                    {
                        var key = setup.Key with { OperationId = node.Id };
                        var operation = (await journal.FindAsync(key, default))!;
                        Assert.True(await journal.TryFinalizeFailureAsync(key, operation.AttemptId!.Value, DurableOperationState.Failed,
                            DurableOperationReason.Conflict, DateTimeOffset.UtcNow, default));
                    }
                    var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>()
                        .CommitAsync(replacement.Change!.Id, replacement.Plan!.Id, "correcting", default)).Commit;
                    pause.Release.TrySetResult();
                    Assert.NotEqual(DurableExecutionOutcome.Succeeded, (await task).StopOutcome);
                    await AssertCurrentCorrection(services, new(previous, null, null, setup.Staged), replacement, commit);
                    await using var check = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
                    Assert.Empty(await check.ResourceImportReceipts.AsNoTracking().ToArrayAsync());
                    Assert.Empty(await check.OperationOutputs.AsNoTracking().ToArrayAsync());
                    var oldVersion = Guid.Parse(setup.Staged.Reference.SecretVersionId.ToString());
                    var newVersion = Guid.Parse(replacement.Credential!.Reference.SecretVersionId.ToString());
                    Assert.False(await check.SecretVersions.AsNoTracking().AnyAsync(x => x.Id == oldVersion || x.Id == newVersion));
                    Assert.Null((await check.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
                    return;
                }
                Assert.IsNotType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>()
                    .CommitAsync(replacement.Change!.Id, replacement.Plan!.Id, "blocked", default));
            }
            finally { pause.Release.TrySetResult(); await task; }
        }
        else
        {
            // Catalog journal control: the non-credential database operation alone must block the coupled pair.
            var node = setup.Plan.Graph.Nodes.Single(x => x.ResourceId.Value == "db");
            var key = new DurableOperationKey(setup.Commit.DeploymentId, setup.Commit.Generation, node.Id);
            var journal = services.GetRequiredService<IDurableOperationJournal>();
            var begin = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(key, DateTimeOffset.UtcNow, default));
            Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(key, begin.AttemptId, DateTimeOffset.UtcNow, default));
            if (scenario == "finished-uncertain")
            {
                Assert.True(await journal.TryRecoverDispatchingAsync(key, DateTimeOffset.UtcNow, default));
                var uncertain = Assert.Single(await journal.FindAttemptsAsync(key, default));
                Assert.NotNull(uncertain.FinishedAt);
                Assert.Equal(DurableAttemptOutcome.Uncertain, uncertain.Outcome);
            }
            Assert.IsNotType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>()
                .CommitAsync(replacement.Change!.Id, replacement.Plan!.Id, "blocked", default));
        }
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verify.ImportSupersessions.AsNoTracking().ToArrayAsync());
        Assert.Equal(setup.Staged.Id, (await verify.ProtectedCredentialOwners.AsNoTracking().SingleAsync()).Stage);
        Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
    }

    private sealed class PauseVerification : IPostgreSqlImportFaultHooks
    {
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask AfterVerificationAsync(CancellationToken token)
        { Ready.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), token); }
    }

    private async Task CorrectionFencesBegin(Setup setup, ImportDraft replacement)
    {
        var pause = new PauseOwnerTransfer();
        var gate = new ObserveImportGate(setup.Owner);
        await using var writer = Services(setup.Connection, configure: s => s.AddSingleton<IDbContextFactory<ManifestCatalogDbContext>>(new CorrectionFactory(setup.Connection, pause)));
        await using var reader = Services(setup.Connection, configure: s => s.AddSingleton<IDbContextFactory<ManifestCatalogDbContext>>(new CorrectionFactory(setup.Connection, gate)));
        var commit = writer.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(replacement.Change!.Id, replacement.Plan!.Id, "winner", default).AsTask();
        Task<DurableBeginAttemptResult>? begin = null;
        try
        {
            await pause.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            begin = reader.GetRequiredService<IDurableOperationJournal>().TryBeginAttemptAsync(setup.Key, DateTimeOffset.UtcNow, default).AsTask();
            await gate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(begin.IsCompleted);
        }
        finally { pause.Release.TrySetResult(); }
        var accepted = Assert.IsType<DurableCommitResult.Committed>(await commit).Commit;
        Assert.NotNull(begin);
        Assert.IsNotType<DurableBeginAttemptResult.Started>(await begin);
        await using var verify = Services(setup.Connection);
        var previous = new ImportDraft(replacement.Binding with { RequestId = setup.Request, Correction = null }, null, null, setup.Staged);
        await AssertCurrentCorrection(verify, previous, replacement, accepted);
        await using var db = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await db.DurableOperationAttempts.AsNoTracking().Where(x => x.Generation == setup.Commit.Generation.Value).ToArrayAsync());
    }

    private sealed class CorrectionFactory(string connection, params IInterceptor[] interceptors) : IDbContextFactory<ManifestCatalogDbContext>
    {
        public ManifestCatalogDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).AddInterceptors(interceptors).Options);
    }
    private sealed class PauseOwnerTransfer : DbCommandInterceptor
    {
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE protected_application_credential_owners", StringComparison.Ordinal))
            { Ready.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); }
            return result;
        }
    }
    private sealed class ObserveImportGate(ManagedResourceKey owner) : DbCommandInterceptor
    {
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var key = $"haby-import:{Guid.Parse(owner.EnvironmentRecordId.ToString()):N}:{Guid.Parse(owner.ApplicationRecordId.ToString()):N}";
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal) && command.Parameters.Cast<DbParameter>().Any(x => x.Value is string value && value == key))
                Ready.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
