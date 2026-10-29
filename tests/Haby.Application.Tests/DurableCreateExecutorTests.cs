using System.Text;
using Haby.Domain;
using Haby.Manifest;
using Haby.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.Application.Tests;

/// <summary>
/// Deterministic contract tests for the durable Create executor against the dispatch barrier. The
/// fakes mirror the real journal's compare-and-transition semantics, so the SQL-free windows
/// (restart, cancellation, uncertain marker and success persistence) are decided without a server.
/// </summary>
public sealed class DurableCreateExecutorTests
{
    private const string DatabaseSchema =
        """{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}""";

    private static readonly ManifestContractCatalog Catalog = new(
        [new ResourceTypeDefinition(PostgreSqlDatabaseContract.Id, DatabaseSchema)],
        [], [], []);

    [Fact]
    public async Task Success_commits_the_marker_before_SQL_and_advances_the_applied_generation_once()
    {
        var (executor, journal, target, _) = Harness();

        var result = await executor.ExecuteAsync(HarnessKey(), default);

        Assert.Equal(DurableCreateExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(DurableCreateOperationState.Succeeded, result.Operation!.State);
        Assert.Equal(["check", "marker", "create", "confirm", "success-recorded"], journal.Log);
        Assert.True(journal.AppliedGenerationAdvanced);
        var attempt = Assert.Single(journal.Attempts);
        Assert.Equal(DurableCreateAttemptOutcome.Succeeded, attempt.Outcome);
        Assert.NotNull(attempt.DispatchedAt);
    }

    [Fact]
    public async Task Retry_after_success_never_sends_a_second_create()
    {
        var (executor, journal, target, _) = Harness();
        await executor.ExecuteAsync(HarnessKey(), default);

        var second = await executor.ExecuteAsync(HarnessKey(), default);

        Assert.Equal(DurableCreateExecutionOutcome.Succeeded, second.Outcome);
        Assert.Equal(["check", "marker", "create", "confirm", "success-recorded"], journal.Log);
        Assert.Single(journal.Attempts);
    }

    [Fact]
    public async Task Restart_from_dispatching_moves_to_needs_attention_without_SQL()
    {
        var attemptId = Guid.CreateVersion7();
        var (executor, journal, target, _) = Harness(new Fixture
        {
            State = DurableCreateOperationState.Dispatching,
            AttemptId = attemptId,
            AttemptNumber = 1,
            OpenAttempt = true
        });

        var result = await executor.ExecuteAsync(HarnessKey(), default);

        Assert.Equal(DurableCreateExecutionOutcome.NeedsAttention, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.UncertainAfterDispatch, result.Operation!.Reason);
        Assert.Equal(["recover"], journal.Log);
        Assert.Empty(target.Inputs);
        var attempt = Assert.Single(journal.Attempts);
        Assert.Equal(DurableCreateAttemptOutcome.Uncertain, attempt.Outcome);
    }

    [Fact]
    public async Task An_uncertain_marker_commit_never_sends_SQL()
    {
        var (executor, journal, target, _) = Harness(new Fixture { ThrowOnMarkerCommit = true });

        var result = await executor.ExecuteAsync(HarnessKey(), default);

        Assert.Equal(DurableCreateExecutionOutcome.DispatchUncertain, result.Outcome);
        Assert.Equal(DurableCreateOperationState.Preparing, result.Operation!.State);
        Assert.DoesNotContain("marker", journal.Log);
        Assert.DoesNotContain("create", journal.Log);
    }

    [Fact]
    public async Task A_preexisting_database_conflicts_before_dispatch_and_is_never_adopted()
    {
        var (executor, journal, target, _) = Harness(new Fixture { Absence = PostgreSqlDatabaseAbsenceOutcome.Present });

        var result = await executor.ExecuteAsync(HarnessKey(), default);

        Assert.Equal(DurableCreateExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.Conflict, result.Operation!.Reason);
        Assert.False(journal.AppliedGenerationAdvanced);
        Assert.DoesNotContain("create", journal.Log);
        Assert.Equal(DurableCreateAttemptOutcome.Conflict, Assert.Single(journal.Attempts).Outcome);
    }

    [Fact]
    public async Task A_second_resource_is_rejected_before_reaching_the_target()
    {
        var (executor, journal, target, _) = Harness(new Fixture
        {
            Manifest = Prepare("""
                "resources":{"database":{"type":"postgresql.database/v1alpha1","providerRef":"primary","properties":{"name":"appdb"}},
                "other":{"type":"postgresql.database/v1alpha1","providerRef":"primary","properties":{"name":"otherdb"}}}
                """)
        });

        var result = await executor.ExecuteAsync(HarnessKey(), default);

        Assert.Equal(DurableCreateExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.UnsupportedShape, result.Operation!.Reason);
        Assert.Empty(target.Inputs);
        Assert.DoesNotContain("create", journal.Log);
    }

    [Fact]
    public async Task An_invalid_database_name_is_rejected_before_reaching_the_target()
    {
        var (executor, journal, target, _) = Harness(new Fixture
        {
            Manifest = Prepare("""
                "resources":{"database":{"type":"postgresql.database/v1alpha1","providerRef":"primary","properties":{"name":"Bad-Db"}}}
                """)
        });

        var result = await executor.ExecuteAsync(HarnessKey(), default);

        Assert.Equal(DurableCreateExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.DatabaseNameInvalid, result.Operation!.Reason);
        Assert.Empty(target.Inputs);
    }

    [Fact]
    public async Task Adapter_local_name_validation_maps_to_the_legacy_reason_without_dispatch()
    {
        var (executor, journal, _, _) = Harness(new Fixture
        { Absence = PostgreSqlDatabaseAbsenceOutcome.DatabaseNameInvalid });

        var result = await executor.ExecuteAsync(HarnessKey(), default);

        Assert.Equal(DurableCreateExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.DatabaseNameInvalid, result.Operation!.Reason);
        Assert.Equal(DurableCreateAttemptOutcome.FailedBeforeDispatch, Assert.Single(journal.Attempts).Outcome);
        Assert.False(journal.AppliedGenerationAdvanced);
        Assert.DoesNotContain("marker", journal.Log);
        Assert.DoesNotContain("create", journal.Log);
    }

    [Fact]
    public void Database_target_contracts_belong_only_to_the_PostgreSql_adapter()
    {
        var adapter = typeof(PostgreSqlDatabaseCreateTarget).Assembly;
        Assert.Equal(adapter, typeof(PostgreSqlDatabaseCreateInput).Assembly);
        Assert.Equal(adapter, typeof(IPostgreSqlDatabaseCreateTarget).Assembly);
        Assert.Equal(adapter, typeof(PostgreSqlDatabaseAbsenceOutcome).Assembly);
        Assert.Equal(adapter, typeof(PostgreSqlDatabaseCreateDispatchOutcome).Assembly);
        var application = typeof(IDurableCreateOperationExecutor).Assembly;
        Assert.DoesNotContain(application.GetReferencedAssemblies(), reference => reference.Name == "Haby.PostgreSql");
        Assert.DoesNotContain(application.GetExportedTypes(), type => type.Name is
            "DurableCreateTargetInput" or "IDurableCreateTarget" or "DurableCreateAbsenceOutcome" or
            "DurableCreateDispatchOutcome");
    }

    [Fact]
    public async Task A_corrupt_pinned_manifest_fails_with_a_bounded_reason_before_dispatch()
    {
        var (executor, journal, target, _) = Harness(new Fixture { CorruptManifest = true });

        var result = await executor.ExecuteAsync(HarnessKey(), default);

        Assert.Equal(DurableCreateExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.ProviderInputUnavailable, result.Operation!.Reason);
        Assert.Equal(DurableCreateAttemptOutcome.FailedBeforeDispatch, Assert.Single(journal.Attempts).Outcome);
        Assert.Empty(target.Inputs);
        Assert.DoesNotContain("marker", journal.Log);
    }

    [Fact]
    public async Task Cancellation_during_the_absence_read_closes_the_attempt_and_stays_resumable()
    {
        var (executor, journal, target, hooks) = Harness(new Fixture { CancelDuringCheck = true });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync(HarnessKey(), hooks.Token).AsTask());

        Assert.Equal(DurableCreateOperationState.Preparing, journal.Operation.State);
        Assert.DoesNotContain("create", journal.Log);
        Assert.Equal(DurableCreateAttemptOutcome.CanceledBeforeDispatch, Assert.Single(journal.Attempts).Outcome);
    }

    [Fact]
    public async Task Cancellation_at_the_marker_boundary_leaves_preparing_to_resume_with_a_new_attempt()
    {
        var (executor, journal, _, hooks) = Harness();
        hooks.BeforeMarker = cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            hooks.Cancel();
            return ValueTask.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync(HarnessKey(), hooks.Token).AsTask());

        Assert.Equal(DurableCreateOperationState.Preparing, journal.Operation.State);
        Assert.DoesNotContain("marker", journal.Log);
        Assert.Null(Assert.Single(journal.Attempts).Outcome);

        var resumed = await executor.ExecuteAsync(HarnessKey(), default);
        Assert.Equal(DurableCreateExecutionOutcome.Succeeded, resumed.Outcome);
        Assert.Equal(DurableCreateAttemptOutcome.AbandonedBeforeDispatch, journal.Attempts[0].Outcome);
        Assert.Equal(DurableCreateAttemptOutcome.Succeeded, journal.Attempts[1].Outcome);
    }

    [Fact]
    public async Task Cancellation_after_the_committed_marker_records_needs_attention_without_retry()
    {
        var (executor, journal, target, hooks) = Harness();
        hooks.AfterMarker = cancellationToken =>
        {
            hooks.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        };

        var result = await executor.ExecuteAsync(HarnessKey(), hooks.Token);

        Assert.Equal(DurableCreateExecutionOutcome.NeedsAttention, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.UncertainAfterDispatch, result.Operation!.Reason);
        Assert.DoesNotContain("create", journal.Log);
        Assert.DoesNotContain("success-recorded", journal.Log);
    }

    [Fact]
    public async Task An_uncertain_dispatch_requires_attention_and_is_not_retried_in_process()
    {
        var (executor, journal, _, _) = Harness(new Fixture
        { Dispatch = PostgreSqlDatabaseCreateDispatchOutcome.Uncertain });

        var result = await executor.ExecuteAsync(HarnessKey(), default);

        Assert.Equal(DurableCreateExecutionOutcome.NeedsAttention, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.UncertainAfterDispatch, result.Operation!.Reason);
        Assert.Equal(1, journal.Log.Count(entry => entry == "create"));
        Assert.False(journal.AppliedGenerationAdvanced);
    }

    [Fact]
    public async Task A_duplicate_answer_after_dispatch_fails_as_conflict_without_adoption()
    {
        var (executor, journal, _, _) = Harness(new Fixture
        { Dispatch = PostgreSqlDatabaseCreateDispatchOutcome.Conflict });

        var result = await executor.ExecuteAsync(HarnessKey(), default);

        Assert.Equal(DurableCreateExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(DurableCreateOperationReason.Conflict, result.Operation!.Reason);
        Assert.False(journal.AppliedGenerationAdvanced);
    }

    [Fact]
    public async Task A_created_answer_without_confirmation_is_not_a_success()
    {
        var (executor, journal, _, _) = Harness(new Fixture
        { Confirm = PostgreSqlDatabaseAbsenceOutcome.Absent });

        var result = await executor.ExecuteAsync(HarnessKey(), default);

        Assert.Equal(DurableCreateExecutionOutcome.NeedsAttention, result.Outcome);
        Assert.DoesNotContain("success-recorded", journal.Log);
        Assert.False(journal.AppliedGenerationAdvanced);
    }

    [Fact]
    public async Task Failed_success_persistence_leaves_the_dispatching_marker_and_no_applied_generation()
    {
        var (executor, journal, _, _) = Harness(new Fixture { ThrowOnRecordSuccess = true });

        var result = await executor.ExecuteAsync(HarnessKey(), default);

        Assert.Equal(DurableCreateExecutionOutcome.DispatchUncertain, result.Outcome);
        Assert.Equal(DurableCreateOperationState.Dispatching, journal.Operation.State);
        Assert.False(journal.AppliedGenerationAdvanced);
    }

    [Fact]
    public async Task Terminal_operations_are_idempotent_and_send_no_SQL()
    {
        var (failed, failedJournal, failedTarget, _) = Harness(new Fixture
        { State = DurableCreateOperationState.Failed, Reason = DurableCreateOperationReason.Conflict });
        var failedResult = await failed.ExecuteAsync(HarnessKey(), default);
        Assert.Equal(DurableCreateExecutionOutcome.Failed, failedResult.Outcome);
        Assert.Empty(failedTarget.Inputs);
        Assert.Empty(failedJournal.Log);

        var (attention, _, attentionTarget, _) = Harness(new Fixture
        {
            State = DurableCreateOperationState.NeedsAttention,
            Reason = DurableCreateOperationReason.UncertainAfterDispatch
        });
        var attentionResult = await attention.ExecuteAsync(HarnessKey(), default);
        Assert.Equal(DurableCreateExecutionOutcome.NeedsAttention, attentionResult.Outcome);
        Assert.Empty(attentionTarget.Inputs);
    }

    [Fact]
    public async Task Manual_recovery_requires_consent_and_never_dispatches_create()
    {
        var (_, journal, target, _) = Harness(new Fixture { Absence = PostgreSqlDatabaseAbsenceOutcome.Present });
        var attemptId = Guid.CreateVersion7();
        journal.SeedUncertain(attemptId);
        var decisions = new FakeRecoveryDecisions();
        var recovery = new PostgreSqlDatabaseRecovery(journal, decisions,
            new FakeManifests(Prepare(OneResource), false), target,
            new FixedClock(DateTimeOffset.UnixEpoch));
        var preview = await recovery.InspectAsync(journal.Operation.Key, default);
        Assert.Equal("appdb", preview!.DatabaseName);
        Assert.Equal(attemptId, preview.UncertainAttemptId);
        Assert.Empty(target.Inputs);
        var command = new AssignExistingPostgreSqlDatabase(journal.Operation.Key,
            journal.Operation.Environment, attemptId, true,
            PostgreSqlRecoveryReason.OperatorAcceptedExistingDatabase, "verified by operator", "tester");
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await recovery.AssignExistingDatabaseAsync(command with { Acknowledged = false }, default));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await recovery.AssignExistingDatabaseAsync(command with { Note = "token=secret" }, default));
        Assert.Empty(target.Inputs);
        Assert.Equal(0, decisions.AssignCalls);
        var result = await recovery.AssignExistingDatabaseAsync(command, default);
        Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.Assigned, result.Outcome);
        Assert.Single(target.Inputs);
        Assert.DoesNotContain("create", journal.Log);
        Assert.Equal(1, decisions.AssignCalls);
        Assert.DoesNotContain(command.Note, command.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DurableCreateOperationState.Pending)]
    [InlineData(DurableCreateOperationState.Preparing)]
    [InlineData(DurableCreateOperationState.Dispatching)]
    [InlineData(DurableCreateOperationState.Failed)]
    [InlineData(DurableCreateOperationState.Succeeded)]
    public async Task Manual_recovery_rejects_non_uncertain_states_before_provider_access(
        DurableCreateOperationState state)
    {
        var (_, journal, target, _) = Harness(new Fixture { State = state });
        var decisions = new FakeRecoveryDecisions();
        var recovery = new PostgreSqlDatabaseRecovery(journal, decisions,
            new FakeManifests(Prepare(OneResource), false), target,
            new FixedClock(DateTimeOffset.UnixEpoch));
        var command = new AssignExistingPostgreSqlDatabase(journal.Operation.Key,
            journal.Operation.Environment, Guid.CreateVersion7(), true,
            PostgreSqlRecoveryReason.OperatorAcceptedExistingDatabase, "checked", "tester");
        Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.Rejected,
            (await recovery.AssignExistingDatabaseAsync(command, default)).Outcome);
        Assert.Empty(target.Inputs);
        Assert.Equal(0, decisions.AssignCalls);
    }

    [Fact]
    public async Task Corrupt_pinned_manifest_rejects_recovery_without_provider_access()
    {
        var (_, journal, target, _) = Harness();
        var attemptId = Guid.CreateVersion7();
        journal.SeedUncertain(attemptId);
        var decisions = new FakeRecoveryDecisions();
        var recovery = new PostgreSqlDatabaseRecovery(journal, decisions,
            new FakeManifests(Prepare(OneResource), true), target,
            new FixedClock(DateTimeOffset.UnixEpoch));
        var preview = await recovery.InspectAsync(journal.Operation.Key, default);
        Assert.Null(preview!.DatabaseName);
        Assert.Equal(DurableCreateOperationReason.ProviderInputUnavailable, preview.Reason);
        var command = new AssignExistingPostgreSqlDatabase(journal.Operation.Key,
            journal.Operation.Environment, attemptId, true,
            PostgreSqlRecoveryReason.OperatorAcceptedExistingDatabase, "checked", "tester");
        Assert.Equal(PostgreSqlDatabaseAssignmentOutcome.TargetUnavailable,
            (await recovery.AssignExistingDatabaseAsync(command, default)).Outcome);
        Assert.Empty(target.Inputs);
        Assert.Equal(0, decisions.AssignCalls);
    }

    [Fact]
    public async Task Concurrent_execution_on_one_key_dispatches_exactly_once()
    {
        var (executor, journal, _, hooks) = Harness();
        var key = journal.Operation.Key;
        var markerCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        hooks.AfterMarker = async token =>
        {
            markerCommitted.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
        };

        var first = executor.ExecuteAsync(key, timeout.Token).AsTask();
        await markerCommitted.Task.WaitAsync(timeout.Token);
        // The first call holds the exact key's gate after the durable marker and before SQL.
        // Fake dependencies complete synchronously, so an unserialized second call would finish
        // recovery immediately rather than waiting here. No scheduling delay is needed.
        var second = executor.ExecuteAsync(key, timeout.Token).AsTask();
        try
        {
            Assert.False(second.IsCompleted);
            Assert.Equal(DurableCreateOperationState.Dispatching, journal.Operation.State);
            Assert.Single(journal.Attempts);
            Assert.False(journal.AppliedGenerationAdvanced);
            Assert.DoesNotContain("create", journal.Log);
        }
        finally
        {
            releaseFirst.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(timeout.Token);
        }

        Assert.Equal(DurableCreateExecutionOutcome.Succeeded, (await first).Outcome);
        Assert.Equal(DurableCreateExecutionOutcome.Succeeded, (await second).Outcome);
        Assert.Equal(DurableCreateOperationState.Succeeded, journal.Operation.State);
        Assert.True(journal.AppliedGenerationAdvanced);
        Assert.Equal(1, journal.Log.Count(entry => entry == "create"));
        Assert.Equal(DurableCreateAttemptOutcome.Succeeded, Assert.Single(journal.Attempts).Outcome);
    }

    [Fact]
    public async Task The_target_sees_exactly_the_inputs_pinned_by_the_generation()
    {
        var instance = ProviderInstanceId.New();
        var (executor, journal, target, _) = Harness(new Fixture { ProviderInstance = instance });

        await executor.ExecuteAsync(HarnessKey(), default);

        Assert.All(target.Inputs, input =>
        {
            Assert.Equal(journal.Operation.Environment, input.Environment);
            Assert.Equal(instance, input.ProviderInstance);
            Assert.Equal(7L, input.ConfigurationRevision.Value);
            Assert.Equal("haby.postgresql.database/v1alpha1", input.ImplementationId);
            Assert.Equal("appdb", input.DatabaseName);
        });
        // The absence read and the independent confirmation read; the dispatch window itself adds none.
        Assert.Equal(2, target.Inputs.Count);
    }

    [Fact]
    public void Registration_supplies_the_null_hooks_and_the_pilot_execution_chain()
    {
        var services = new ServiceCollection().AddPostgreSqlDurableCreateOperationExecution();
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDurableCreateOperationExecutor) &&
            descriptor.ImplementationType == typeof(PostgreSqlDurableCreateOperationExecutor));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDurableCreateDispatchFaultHooks) &&
            descriptor.ImplementationType == typeof(NullDurableCreateDispatchFaultHooks));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IPostgreSqlDatabaseCreateTarget) &&
            descriptor.ImplementationType == typeof(PostgreSqlDatabaseCreateTarget));
    }

    private const string OneResource =
        """
        "resources":{"database":{"type":"postgresql.database/v1alpha1","providerRef":"primary","properties":{"name":"appdb"}}}
        """;

    private static DurableCreateOperationKey HarnessKey() => new(ApplicationDeploymentId.New(),
        DeploymentGeneration.Create(1), ManifestLocalId.Create("database"), DurableOperationAction.Create);

    private static (PostgreSqlDurableCreateOperationExecutor Executor, FakeJournal Journal, FakeTarget Target,
        TestHooks Hooks) Harness(Fixture? fixture = null)
    {
        fixture ??= new Fixture();
        var attemptId = fixture.AttemptId;
        var journal = new FakeJournal(
            new DurableCreateOperation(HarnessKey(), fixture.State, EnvironmentRecordId.New(),
                ApplicationRecordId.New(),
                ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)),
                fixture.ProviderInstance, ProviderConfigurationRevisionNumber.Create(7),
                "haby.postgresql.database/v1alpha1", DateTimeOffset.UnixEpoch, null, fixture.Reason,
                attemptId, fixture.AttemptNumber,
                fixture.State == DurableCreateOperationState.Dispatching ? DateTimeOffset.UnixEpoch : null, null))
        {
            ThrowOnMarkerCommit = fixture.ThrowOnMarkerCommit,
            ThrowOnRecordSuccess = fixture.ThrowOnRecordSuccess
        };
        if (fixture.OpenAttempt)
            journal.Attempts.Add(new DurableCreateOperationAttempt(attemptId!.Value, 1,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null));
        var hooks = new TestHooks();
        hooks.BeforeMarker = fixture.BeforeMarker;
        hooks.AfterMarker = fixture.AfterMarker;
        var target = new FakeTarget(journal.Log)
        {
            Absence = fixture.Absence,
            Confirm = fixture.Confirm,
            Dispatch = fixture.Dispatch,
            CancelOnCheck = fixture.CancelDuringCheck ? (Action?)hooks.Cancel : null
        };
        var executor = new PostgreSqlDurableCreateOperationExecutor(journal,
            new FakeManifests(fixture.Manifest ?? Prepare(OneResource), fixture.CorruptManifest), target, hooks,
            new FixedClock(DateTimeOffset.UnixEpoch));
        return (executor, journal, target, hooks);
    }

    private static ManifestRevision Prepare(string specMembers)
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"applicationVersion\":\"1.0\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\"," +
            "\"kind\":\"Application\",\"metadata\":{\"id\":\"example\"},\"spec\":{" + specMembers + "}}}");
        var prepared = Assert.IsType<ManifestCandidateResult.Valid>(
            ManifestCandidatePipeline.Prepare(payload, Catalog));
        var candidate = prepared.Candidate;
        return new ManifestRevision(ManifestRevisionId.New(),
            ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)),
            candidate.ApplicationId, candidate.ApplicationVersion, candidate.Declaration.InputApiVersion,
            candidate.Declaration.NormalizedModelVersion, candidate.Declaration, candidate.Warnings,
            new ImportAuditMetadata("actor", DateTimeOffset.UnixEpoch));
    }

    private sealed class Fixture
    {
        public DurableCreateOperationState State { get; init; } = DurableCreateOperationState.Pending;
        public DurableCreateOperationReason? Reason { get; init; }
        public Guid? AttemptId { get; init; }
        public long AttemptNumber { get; init; }
        public bool OpenAttempt { get; init; }
        public ProviderInstanceId ProviderInstance { get; init; } = ProviderInstanceId.New();
        public ManifestRevision? Manifest { get; init; }
        public bool CorruptManifest { get; init; }
        public bool ThrowOnMarkerCommit { get; init; }
        public bool ThrowOnRecordSuccess { get; init; }
        public PostgreSqlDatabaseAbsenceOutcome Absence { get; init; } = PostgreSqlDatabaseAbsenceOutcome.Absent;
        public PostgreSqlDatabaseAbsenceOutcome Confirm { get; init; } = PostgreSqlDatabaseAbsenceOutcome.Present;
        public PostgreSqlDatabaseCreateDispatchOutcome Dispatch { get; init; } = PostgreSqlDatabaseCreateDispatchOutcome.Created;
        public bool CancelDuringCheck { get; init; }
        public Func<CancellationToken, ValueTask>? BeforeMarker { get; init; }
        public Func<CancellationToken, ValueTask>? AfterMarker { get; init; }
    }

    private sealed class FakeManifests(ManifestRevision revision, bool corrupt) : IManifestRevisionQuery
    {
        public ValueTask<ManifestRevision?> FindAsync(ManifestRevisionRef reference,
            CancellationToken cancellationToken = default) => corrupt
                ? throw new InvalidDataException("Stored manifest revision is corrupt.")
                : new(revision);
    }

    private sealed class FakeJournal(DurableCreateOperation operation) : IDurableCreateOperationJournal
    {
        public List<string> Log { get; } = [];
        public List<DurableCreateOperationAttempt> Attempts { get; } = [];
        public bool ThrowOnMarkerCommit { get; init; }
        public bool ThrowOnRecordSuccess { get; init; }
        public bool AppliedGenerationAdvanced { get; private set; }
        public DurableCreateOperation Operation { get; private set; } = operation;

        public void SeedUncertain(Guid attemptId)
        {
            Operation = Operation with
            {
                State = DurableCreateOperationState.NeedsAttention,
                Reason = DurableCreateOperationReason.UncertainAfterDispatch,
                AttemptId = attemptId,
                AttemptNumber = 1,
                DispatchedAt = DateTimeOffset.UnixEpoch,
                FinalizedAt = DateTimeOffset.UnixEpoch
            };
            Attempts.Add(new DurableCreateOperationAttempt(attemptId, 1, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DurableCreateAttemptOutcome.Uncertain));
        }

        public ValueTask<DurableCreateOperation?> FindAsync(DurableCreateOperationKey key,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<DurableCreateOperation?>(Operation);
        }

        public ValueTask<IReadOnlyList<DurableCreateOperationAttempt>> FindAttemptsAsync(
            DurableCreateOperationKey key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<DurableCreateOperationAttempt>>(Attempts);
        }

        public ValueTask<DurableCreateBeginAttemptResult> TryBeginAttemptAsync(DurableCreateOperationKey key,
            DateTimeOffset at, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Operation.State is not (DurableCreateOperationState.Pending or DurableCreateOperationState.Preparing))
                return new ValueTask<DurableCreateBeginAttemptResult>(new DurableCreateBeginAttemptResult.Terminal(Operation));
            var attemptId = Guid.CreateVersion7();
            for (var index = 0; index < Attempts.Count; index++)
                if (Attempts[index].FinishedAt is null)
                    Attempts[index] = Attempts[index] with
                    { FinishedAt = at, Outcome = DurableCreateAttemptOutcome.AbandonedBeforeDispatch };
            var number = Operation.AttemptNumber + 1;
            Operation = Operation with
            {
                State = DurableCreateOperationState.Preparing,
                AttemptId = attemptId,
                AttemptNumber = number,
                Reason = null,
                UpdatedAt = at
            };
            Attempts.Add(new DurableCreateOperationAttempt(attemptId, number, at, null, null, null));
            return new ValueTask<DurableCreateBeginAttemptResult>(
                new DurableCreateBeginAttemptResult.Started(Operation, attemptId, number));
        }

        public ValueTask<DurableCreateMarkerResult> TryCommitDispatchMarkerAsync(DurableCreateOperationKey key,
            Guid attemptId, DateTimeOffset at, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnMarkerCommit) throw new InvalidOperationException("The marker commit result is uncertain.");
            if (Operation.State != DurableCreateOperationState.Preparing || Operation.AttemptId != attemptId)
                return new ValueTask<DurableCreateMarkerResult>(new DurableCreateMarkerResult.Rejected(Operation));
            Operation = Operation with
            {
                State = DurableCreateOperationState.Dispatching,
                DispatchedAt = at,
                UpdatedAt = at
            };
            CloseAttempt(attemptId, at, dispatched: true);
            Log.Add("marker");
            return new ValueTask<DurableCreateMarkerResult>(new DurableCreateMarkerResult.Committed());
        }

        public ValueTask<bool> TryCloseAttemptBeforeDispatchAsync(DurableCreateOperationKey key, Guid attemptId,
            DurableCreateAttemptOutcome outcome, DurableCreateOperationReason? reason, DateTimeOffset at,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return new ValueTask<bool>(false);
            if (Operation.State != DurableCreateOperationState.Preparing || Operation.AttemptId != attemptId)
                return new ValueTask<bool>(false);
            Operation = Operation with { Reason = reason, UpdatedAt = at };
            CloseAttempt(attemptId, at, outcome: outcome);
            return new ValueTask<bool>(true);
        }

        public ValueTask<bool> TryFinalizeFailureAsync(DurableCreateOperationKey key, Guid attemptId,
            DurableCreateOperationState terminal, DurableCreateOperationReason reason, DateTimeOffset at,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Operation.AttemptId != attemptId ||
                Operation.State is not (DurableCreateOperationState.Preparing or
                    DurableCreateOperationState.Dispatching))
                return new ValueTask<bool>(false);
            Operation = Operation with
            {
                State = terminal,
                Reason = reason,
                FinalizedAt = at,
                UpdatedAt = at
            };
            CloseAttempt(attemptId, at, outcome: terminal == DurableCreateOperationState.NeedsAttention
                ? DurableCreateAttemptOutcome.Uncertain
                : reason switch
                {
                    DurableCreateOperationReason.Conflict => DurableCreateAttemptOutcome.Conflict,
                    DurableCreateOperationReason.ServerRejected => DurableCreateAttemptOutcome.ServerRejected,
                    _ => DurableCreateAttemptOutcome.FailedBeforeDispatch
                });
            return new ValueTask<bool>(true);
        }

        public ValueTask<bool> TryRecoverDispatchingAsync(DurableCreateOperationKey key, DateTimeOffset at,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Operation.State != DurableCreateOperationState.Dispatching) return new ValueTask<bool>(false);
            Operation = Operation with
            {
                State = DurableCreateOperationState.NeedsAttention,
                Reason = DurableCreateOperationReason.UncertainAfterDispatch,
                FinalizedAt = at,
                UpdatedAt = at
            };
            for (var index = 0; index < Attempts.Count; index++)
                if (Attempts[index].FinishedAt is null)
                    Attempts[index] = Attempts[index] with
                    { FinishedAt = at, Outcome = DurableCreateAttemptOutcome.Uncertain };
            Log.Add("recover");
            return new ValueTask<bool>(true);
        }

        public ValueTask<bool> TryRecordSuccessAsync(DurableCreateOperationKey key, Guid attemptId, DateTimeOffset at,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (ThrowOnRecordSuccess) throw new InvalidOperationException("Terminal persistence failed.");
            if (Operation.State != DurableCreateOperationState.Dispatching || Operation.AttemptId != attemptId)
                return new ValueTask<bool>(false);
            Operation = Operation with { State = DurableCreateOperationState.Succeeded, FinalizedAt = at, UpdatedAt = at };
            CloseAttempt(attemptId, at, outcome: DurableCreateAttemptOutcome.Succeeded);
            AppliedGenerationAdvanced = true;
            Log.Add("success-recorded");
            return new ValueTask<bool>(true);
        }

        private void CloseAttempt(Guid attemptId, DateTimeOffset at, bool dispatched = false,
            DurableCreateAttemptOutcome? outcome = null)
        {
            for (var index = 0; index < Attempts.Count; index++)
                if (Attempts[index].AttemptId == attemptId)
                    Attempts[index] = Attempts[index] with
                    {
                        DispatchedAt = dispatched ? at : Attempts[index].DispatchedAt,
                        FinishedAt = outcome is null ? Attempts[index].FinishedAt : at,
                        Outcome = outcome ?? Attempts[index].Outcome
                    };
        }
    }

    private sealed class FakeRecoveryDecisions : IPostgreSqlDatabaseRecoveryJournal
    {
        public int AssignCalls { get; private set; }
        public ValueTask<PostgreSqlDatabaseRecoveryAudit?> FindDecisionAsync(DurableCreateOperationKey key,
            Guid attemptId, CancellationToken cancellationToken) => new((PostgreSqlDatabaseRecoveryAudit?)null);
        public ValueTask<PostgreSqlDatabaseAssignmentResult> TryAssignAsync(
            AssignExistingPostgreSqlDatabase command, DateTimeOffset at, CancellationToken cancellationToken)
        {
            AssignCalls++;
            return new(new PostgreSqlDatabaseAssignmentResult(PostgreSqlDatabaseAssignmentOutcome.Assigned,
                new PostgreSqlDatabaseRecoveryReceipt(Guid.CreateVersion7(), command.Key,
                    command.ExpectedAttemptId, command.Reason, command.TechnicalActor, at)));
        }
    }

    private sealed class FakeTarget(List<string> log) : IPostgreSqlDatabaseCreateTarget
    {
        public List<PostgreSqlDatabaseCreateInput> Inputs { get; } = [];
        public PostgreSqlDatabaseAbsenceOutcome Absence { get; init; } = PostgreSqlDatabaseAbsenceOutcome.Absent;
        public PostgreSqlDatabaseAbsenceOutcome Confirm { get; init; } = PostgreSqlDatabaseAbsenceOutcome.Present;
        public PostgreSqlDatabaseCreateDispatchOutcome Dispatch { get; init; } = PostgreSqlDatabaseCreateDispatchOutcome.Created;
        public Action? CancelOnCheck { get; init; }
        private bool Dispatched;

        public ValueTask<PostgreSqlDatabaseAbsenceOutcome> CheckDatabaseAbsentAsync(PostgreSqlDatabaseCreateInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inputs.Add(input);
            if (CancelOnCheck is { } cancel)
            {
                cancel();
                throw new OperationCanceledException(cancellationToken);
            }

            log.Add(Dispatched ? "confirm" : "check");
            return new ValueTask<PostgreSqlDatabaseAbsenceOutcome>(Dispatched ? Confirm : Absence);
        }

        public ValueTask<PostgreSqlDatabaseCreateDispatchOutcome> DispatchCreateAsync(PostgreSqlDatabaseCreateInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log.Add("create");
            Dispatched = true;
            return new ValueTask<PostgreSqlDatabaseCreateDispatchOutcome>(Dispatch);
        }
    }

    private sealed class TestHooks : IDurableCreateDispatchFaultHooks
    {
        private readonly CancellationTokenSource _source = new();
        public Func<CancellationToken, ValueTask>? BeforeMarker { get; set; }
        public Func<CancellationToken, ValueTask>? AfterMarker { get; set; }
        public CancellationToken Token => _source.Token;
        public void Cancel() => _source.Cancel();
        public ValueTask BeforeDispatchMarkerAsync(CancellationToken cancellationToken) =>
            BeforeMarker?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
        public ValueTask AfterDispatchMarkerCommittedAsync(CancellationToken cancellationToken) =>
            AfterMarker?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
        public ValueTask DuringCreateCommandAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask BeforeSuccessPersistedAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
