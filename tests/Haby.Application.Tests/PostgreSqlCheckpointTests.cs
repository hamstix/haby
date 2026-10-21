using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Application.Tests;

public class PostgreSqlCheckpointTests
{
    private static readonly ManifestContractCatalog Catalog = new([PostgreSqlDatabaseContract.Definition], [], [], []);

    [Theory]
    [InlineData("\"bad-name\"")]
    [InlineData("\"Upper\"")]
    [InlineData("\"\"")]
    [InlineData("123")]
    public async Task Import_rejects_invalid_name_before_planning(string name)
    {
        var store = new InMemoryManifestImportStore();
        var import = new ImportApplicationManifest(store, Catalog, TimeProvider.System);
        Assert.IsType<ImportApplicationManifestResult.InvalidManifest>(await import.ExecuteAsync(Payload(name), "actor"));
        Assert.Empty(store.Revisions);
    }

    [Fact]
    public async Task Import_rejects_long_name_and_unknown_property()
    {
        var store = new InMemoryManifestImportStore();
        var import = new ImportApplicationManifest(store, Catalog, TimeProvider.System);
        Assert.IsType<ImportApplicationManifestResult.InvalidManifest>(await import.ExecuteAsync(
            Payload("\"" + new string('a', 64) + "\""), "actor"));
        var unknown = Encoding.UTF8.GetString(Payload("\"valid_name\""))
            .Replace("\"name\":\"valid_name\"", "\"name\":\"valid_name\",\"extra\":true", StringComparison.Ordinal);
        Assert.IsType<ImportApplicationManifestResult.InvalidManifest>(await import.ExecuteAsync(
            Encoding.UTF8.GetBytes(unknown), "actor"));
        Assert.Empty(store.Revisions);
    }

    [Fact]
    public async Task Plan_rejects_incompatible_exact_implementation()
    {
        var fixture = await Fixture.Create(new RecordingProvisioner());
        fixture.Desired.SetProviderAlias(fixture.Environment.RecordId, ProviderAlias.Create("main"),
            new ProviderResolution("provider", 1, "other"));
        var change = fixture.ChangeSet();
        Assert.IsType<PlanChangeSetResult.ProviderCapabilityMismatch>(fixture.Desired.Plan(change.Id, "actor"));
        Assert.Empty(fixture.Desired.Generations);
    }

    [Fact]
    public async Task Commit_is_side_effect_free_and_successful_retry_does_not_dispatch_again()
    {
        var fake = new RecordingProvisioner();
        var fixture = await Fixture.Create(fake);
        var generation = fixture.Commit();
        Assert.Equal(0, fake.Calls);
        Assert.Null(Assert.Single(fixture.Desired.Deployments).AppliedGeneration);
        Assert.Equal(CheckpointExecutionState.Pending, fixture.Executor.GetStatus(generation).State);
        Assert.Equal(CheckpointExecutionState.Succeeded, (await fixture.Executor.ExecuteAsync(generation)).State);
        Assert.Equal(CheckpointExecutionState.Succeeded, (await fixture.Executor.ExecuteAsync(generation)).State);
        Assert.Equal(1, fake.Calls);
        Assert.Equal(1, Assert.Single(fixture.Desired.Deployments).AppliedGeneration?.Value);
        Assert.Single(fixture.Desired.Generations);
    }

    [Fact]
    public async Task Definitive_failure_can_retry_but_uncertain_outcome_cannot()
    {
        var fake = new RecordingProvisioner(CheckpointProvisionOutcome.PreDispatchFailure,
            CheckpointProvisionOutcome.UncertainOutcome);
        var fixture = await Fixture.Create(fake);
        var generation = fixture.Commit();
        Assert.Equal(CheckpointExecutionFailure.PreDispatchFailure,
            (await fixture.Executor.ExecuteAsync(generation)).Failure);
        Assert.Equal(CheckpointExecutionState.NeedsAttention,
            (await fixture.Executor.ExecuteAsync(generation)).State);
        Assert.Equal(CheckpointExecutionState.NeedsAttention,
            (await fixture.Executor.ExecuteAsync(generation)).State);
        Assert.Equal(2, fake.Calls);
        Assert.Null(Assert.Single(fixture.Desired.Deployments).AppliedGeneration);
    }

    [Fact]
    public async Task Server_rejection_is_visible_and_retryable()
    {
        var fake = new RecordingProvisioner(CheckpointProvisionOutcome.ServerRejected,
            CheckpointProvisionOutcome.Created);
        var fixture = await Fixture.Create(fake);
        var generation = fixture.Commit();
        var rejected = await fixture.Executor.ExecuteAsync(generation);
        Assert.Equal(CheckpointExecutionState.Failed, rejected.State);
        Assert.Equal(CheckpointExecutionFailure.ServerRejected, rejected.Failure);
        Assert.Null(Assert.Single(fixture.Desired.Deployments).AppliedGeneration);
        Assert.Equal(CheckpointExecutionState.Succeeded, (await fixture.Executor.ExecuteAsync(generation)).State);
        Assert.Equal(2, fake.Calls);
        Assert.Equal(generation.Generation, Assert.Single(fixture.Desired.Deployments).AppliedGeneration);
    }

    [Fact]
    public async Task Concurrent_attempts_dispatch_only_once()
    {
        var fake = new RecordingProvisioner();
        fake.Pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = await Fixture.Create(fake);
        var generation = fixture.Commit();
        var first = fixture.Executor.ExecuteAsync(generation).AsTask();
        Assert.Equal(CheckpointExecutionState.Running, (await fixture.Executor.ExecuteAsync(generation)).State);
        Assert.Equal(1, fake.Calls);
        fake.Pause.SetResult();
        Assert.Equal(CheckpointExecutionState.Succeeded, (await first).State);
    }

    [Fact]
    public async Task Cancellation_before_dispatch_is_retryable()
    {
        var fake = new RecordingProvisioner();
        var fixture = await Fixture.Create(fake);
        var generation = fixture.Commit();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Executor.ExecuteAsync(generation,
            cancelled.Token).AsTask());
        Assert.Equal(CheckpointExecutionState.Failed, fixture.Executor.GetStatus(generation).State);
        Assert.Equal(CheckpointExecutionState.Succeeded, (await fixture.Executor.ExecuteAsync(generation)).State);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Unsupported_committed_shape_never_dispatches_or_applies()
    {
        var fake = new RecordingProvisioner();
        var fixture = await Fixture.Create(fake, twoResources: true);
        var generation = fixture.Commit();
        Assert.Equal(CheckpointExecutionFailure.UnsupportedShape,
            (await fixture.Executor.ExecuteAsync(generation)).Failure);
        Assert.Equal(0, fake.Calls);
        Assert.Null(Assert.Single(fixture.Desired.Deployments).AppliedGeneration);
    }

    [Fact]
    public async Task Conflict_does_not_advance_applied_generation()
    {
        var fake = new RecordingProvisioner(CheckpointProvisionOutcome.Conflict);
        var fixture = await Fixture.Create(fake);
        var generation = fixture.Commit();
        Assert.Equal(CheckpointExecutionFailure.Conflict, (await fixture.Executor.ExecuteAsync(generation)).Failure);
        Assert.Null(Assert.Single(fixture.Desired.Deployments).AppliedGeneration);
    }

    [Fact]
    public async Task Cancellation_after_entering_provider_needs_attention()
    {
        var fake = new RecordingProvisioner { Pause = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var fixture = await Fixture.Create(fake);
        var generation = fixture.Commit();
        using var cancelled = new CancellationTokenSource();
        var pending = fixture.Executor.ExecuteAsync(generation, cancelled.Token).AsTask();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(CheckpointExecutionState.NeedsAttention, fixture.Executor.GetStatus(generation).State);
        Assert.Equal(CheckpointExecutionState.NeedsAttention,
            (await fixture.Executor.ExecuteAsync(generation)).State);
        Assert.Equal(1, fake.Calls);
        Assert.Null(Assert.Single(fixture.Desired.Deployments).AppliedGeneration);
    }

    private static byte[] Payload(string name, bool twoResources = false) => Encoding.UTF8.GetBytes(
        "{\"applicationVersion\":\"1\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"checkpoint\"},\"spec\":{\"resources\":{\"database\":{\"type\":\"postgresql.database/v1alpha1\",\"providerRef\":\"main\",\"properties\":{\"name\":" + name + "}}" +
        (twoResources ? ",\"other\":{\"type\":\"postgresql.database/v1alpha1\",\"providerRef\":\"main\",\"properties\":{\"name\":\"another_db\"}}" : "") + "}}}}");

    private sealed class Fixture
    {
        private readonly ManifestRevisionRef _revision;
        private Fixture(InMemoryDesiredStateStore desired, EnvironmentRecord environment,
            PostgreSqlCheckpointExecutor executor, ManifestRevisionRef revision)
        { Desired = desired; Environment = environment; Executor = executor; _revision = revision; }
        public InMemoryDesiredStateStore Desired { get; }
        public EnvironmentRecord Environment { get; }
        public PostgreSqlCheckpointExecutor Executor { get; }
        public static async Task<Fixture> Create(RecordingProvisioner fake, bool twoResources = false)
        {
            var revisions = new InMemoryManifestImportStore();
            var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await
                new ImportApplicationManifest(revisions, Catalog, TimeProvider.System)
                    .ExecuteAsync(Payload("\"checkpoint_db\"", twoResources), "actor"));
            var desired = new InMemoryDesiredStateStore(revisions, TimeProvider.System, fake);
            var environment = desired.AddEnvironment(EnvironmentId.Create("test-env"), "Test");
            desired.SetProviderAlias(environment.RecordId, ProviderAlias.Create("main"),
                new ProviderResolution("provider", 1, "checkpoint"));
            return new Fixture(desired, environment, new PostgreSqlCheckpointExecutor(desired, revisions, fake),
                imported.ManifestRevisionRef);
        }
        public DeploymentChangeSet ChangeSet() => Assert.IsType<CreateChangeSetResult.Created>(
            Desired.CreateChangeSet(Environment.RecordId, _revision.ApplicationRecordId, _revision,
                new DeploymentPrecondition.ExpectedAbsence(), "actor")).ChangeSet;
        public ApplicationDeploymentGeneration Commit()
        {
            var change = ChangeSet();
            var plan = Assert.IsType<PlanChangeSetResult.Planned>(Desired.Plan(change.Id, "actor")).Plan;
            Assert.IsType<CommitDesiredStateResult.Committed>(Desired.Commit(change.Id, plan.Id, "actor"));
            return Assert.Single(Desired.Generations);
        }
    }

    private sealed class RecordingProvisioner(params CheckpointProvisionOutcome[] outcomes) :
        ICheckpointProviderCapability, ICheckpointDatabaseProvisioner
    {
        public int Calls { get; private set; }
        public TaskCompletionSource? Pause { get; set; }
        public bool Supports(ProviderResolution selection, ResourceTypeId contract) =>
            selection.ImplementationId == "checkpoint" && contract == PostgreSqlDatabaseContract.Id;
        public async ValueTask<CheckpointProvisionOutcome> CreateAsync(ProviderResolution exactProvider,
            string databaseName, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("checkpoint_db", databaseName);
            if (Pause is not null) await Pause.Task.WaitAsync(cancellationToken);
            return Calls <= outcomes.Length ? outcomes[Calls - 1] : CheckpointProvisionOutcome.Created;
        }
    }
}
