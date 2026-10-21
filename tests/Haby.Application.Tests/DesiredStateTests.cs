using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Application.Tests;

public class DesiredStateTests
{
    private static readonly ManifestContractCatalog Catalog = new(
        [new ResourceTypeDefinition(ResourceTypeId.Create("example.resource/v1"), """{"type":"object"}""")],
        [], [new OutputContractDefinition(OutputContractId.Create("example.output/v1"))], []);

    [Fact]
    public async Task Import_plan_and_commit_are_separate_and_execute_no_provider()
    {
        var fixture = await Fixture.Create();
        Assert.Empty(fixture.Desired.Deployments);
        var changeSet = fixture.ChangeSet(fixture.First, new DeploymentPrecondition.ExpectedAbsence());
        Assert.Empty(fixture.Desired.Deployments);
        var plan = fixture.Plan(changeSet);
        Assert.Empty(fixture.Desired.Deployments);
        Assert.Equal([fixture.First.ApplicationRecordId], plan.DependencyOrder);
        Assert.Equal("provider-1", Assert.Single(plan.Inputs.Providers).Resolution.ProviderInstanceId);
        var commit = fixture.Commit(changeSet, plan);
        Assert.Equal(1, commit.Generation.Value);
        Assert.False(commit.NoOp);
        var deployment = Assert.Single(fixture.Desired.Deployments);
        Assert.Null(deployment.AppliedGeneration);
        var generation = Assert.Single(fixture.Desired.Generations);
        Assert.Equal(fixture.First, generation.ManifestRevisionRef);
        Assert.Equal(plan.Inputs, generation.Inputs);
        Assert.Equal(DesiredPresence.Present, generation.Presence);
    }

    [Fact]
    public async Task Stale_intent_wins_over_stale_plan_and_changes_nothing()
    {
        var fixture = await Fixture.Create();
        var one = fixture.ChangeSet(fixture.First, new DeploymentPrecondition.ExpectedAbsence());
        var two = fixture.ChangeSet(fixture.First, new DeploymentPrecondition.ExpectedAbsence());
        var plan = fixture.Plan(two);
        fixture.Commit(one, fixture.Plan(one));
        fixture.Desired.SetProviderAlias(fixture.Environment.RecordId, ProviderAlias.Create("main"),
            new ProviderResolution("provider-2", 2, "fake-v2"));
        var stale = Assert.IsType<CommitDesiredStateResult.ChangeSetStale>(fixture.Desired.Commit(two.Id, plan.Id, "actor"));
        Assert.Equal(1, stale.ActualGeneration?.Value);
        Assert.Single(fixture.Desired.Generations);
        Assert.IsType<PlanChangeSetResult.ChangeSetStale>(fixture.Desired.Plan(two.Id, "actor"));
    }

    [Fact]
    public async Task Changed_alias_stales_plan_and_replan_pins_new_resolution()
    {
        var fixture = await Fixture.Create();
        var changeSet = fixture.ChangeSet(fixture.First, new DeploymentPrecondition.ExpectedAbsence());
        var original = fixture.Plan(changeSet);
        fixture.Desired.SetProviderAlias(fixture.Environment.RecordId, ProviderAlias.Create("main"),
            new ProviderResolution("provider-2", 2, "fake-v2"));
        Assert.IsType<CommitDesiredStateResult.PlanStale>(fixture.Desired.Commit(changeSet.Id, original.Id, "actor"));
        Assert.Empty(fixture.Desired.Deployments);
        var replacement = fixture.Plan(changeSet);
        Assert.Equal("provider-1", Assert.Single(original.Inputs.Providers).Resolution.ProviderInstanceId);
        Assert.Equal("provider-2", Assert.Single(replacement.Inputs.Providers).Resolution.ProviderInstanceId);
        fixture.Commit(changeSet, replacement);
        Assert.Equal("provider-2", Assert.Single(Assert.Single(fixture.Desired.Generations).Inputs.Providers)
            .Resolution.ProviderInstanceId);
    }

    [Fact]
    public async Task Equivalent_state_is_no_op_but_revision_or_resolution_change_advances_generation()
    {
        var fixture = await Fixture.Create();
        var first = fixture.ChangeSet(fixture.First, new DeploymentPrecondition.ExpectedAbsence());
        fixture.Commit(first, fixture.Plan(first));
        var repeat = fixture.ChangeSet(fixture.First, new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(1)));
        Assert.True(fixture.Commit(repeat, fixture.Plan(repeat)).NoOp);
        Assert.Single(fixture.Desired.Generations);
        fixture.Desired.SetProviderAlias(fixture.Environment.RecordId, ProviderAlias.Create("main"),
            new ProviderResolution("provider-1", 2, "fake-v1"));
        var changedInput = fixture.ChangeSet(fixture.First, new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(1)));
        Assert.Equal(2, fixture.Commit(changedInput, fixture.Plan(changedInput)).Generation.Value);
        var changedRevision = fixture.ChangeSet(fixture.Second, new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(2)));
        Assert.Equal(3, fixture.Commit(changedRevision, fixture.Plan(changedRevision)).Generation.Value);
        Assert.Equal(3, fixture.Desired.Generations.Count);
        Assert.All(fixture.Desired.Deployments, deployment => Assert.Null(deployment.AppliedGeneration));
    }

    [Fact]
    public async Task Concurrent_apply_and_retry_return_one_commit_even_with_another_plan()
    {
        var fixture = await Fixture.Create();
        var changeSet = fixture.ChangeSet(fixture.First, new DeploymentPrecondition.ExpectedAbsence());
        var first = fixture.Plan(changeSet);
        var alternate = fixture.Plan(changeSet);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            Assert.IsType<CommitDesiredStateResult.Committed>(fixture.Desired.Commit(changeSet.Id, first.Id, "actor")).Commit)));
        Assert.All(results, result => Assert.Equal(results[0], result));
        var retry = Assert.IsType<CommitDesiredStateResult.Committed>(fixture.Desired.Commit(changeSet.Id, alternate.Id, "actor"));
        Assert.Equal(first.Id, retry.Commit.PlanId);
        Assert.Single(fixture.Desired.Generations);
        Assert.IsType<PlanChangeSetResult.AlreadyCommitted>(fixture.Desired.Plan(changeSet.Id, "actor"));
    }

    [Fact]
    public async Task Invalid_ownership_and_unresolved_dependencies_fail_closed()
    {
        var fixture = await Fixture.Create();
        var other = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await fixture.Import.ExecuteAsync(
            Payload("other", "1", ""), "actor"));
        Assert.IsType<CreateChangeSetResult.RevisionNotOwnedByApplication>(fixture.Desired.CreateChangeSet(
            fixture.Environment.RecordId, fixture.First.ApplicationRecordId, other.ManifestRevisionRef,
            new DeploymentPrecondition.ExpectedAbsence(), "actor"));
        var missing = await Fixture.Create(addAlias: false);
        var missingChangeSet = missing.ChangeSet(missing.First, new DeploymentPrecondition.ExpectedAbsence());
        Assert.IsType<PlanChangeSetResult.ProviderAliasNotResolved>(missing.Desired.Plan(missingChangeSet.Id, "actor"));
        Assert.Empty(missing.Desired.Deployments);
        var external = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await fixture.Import.ExecuteAsync(
            Payload("example", "3", "\"components\":{\"consumer\":{\"bindings\":{\"remote\":{\"target\":{\"kind\":\"application-export\",\"applicationId\":\"other\",\"exportRef\":\"data\",\"contract\":\"example.output/v1\"}}}}}"), "actor"));
        var unsupported = fixture.ChangeSet(external.ManifestRevisionRef, new DeploymentPrecondition.ExpectedAbsence());
        Assert.IsType<PlanChangeSetResult.UnsupportedDependency>(fixture.Desired.Plan(unsupported.Id, "actor"));
        Assert.Empty(fixture.Desired.Deployments);
    }

    [Fact]
    public async Task Cancellation_before_commit_and_returned_snapshot_mutation_change_nothing()
    {
        var fixture = await Fixture.Create();
        var changeSet = fixture.ChangeSet(fixture.First, new DeploymentPrecondition.ExpectedAbsence());
        var plan = fixture.Plan(changeSet);
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Desired.Commit(changeSet.Id, plan.Id, "actor", source.Token));
        Assert.Empty(fixture.Desired.Deployments);
        Assert.Throws<NotSupportedException>(() => ((IList<ResolvedProviderAlias>)plan.Inputs.Providers)[0] =
            new ResolvedProviderAlias(ProviderAlias.Create("main"), new ProviderResolution("changed", 1, "fake")));
        var committed = fixture.Commit(changeSet, plan);
        Assert.Equal("provider-1", Assert.Single(Assert.Single(fixture.Desired.Generations).Inputs.Providers)
            .Resolution.ProviderInstanceId);
        Assert.Equal(committed, Assert.IsType<CommitDesiredStateResult.Committed>(
            fixture.Desired.Commit(changeSet.Id, plan.Id, "actor", source.Token)).Commit);
    }

    private static byte[] Payload(string application, string version, string additionalSpec) => Encoding.UTF8.GetBytes(
        "{\"applicationVersion\":\"" + version + "\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"" + application + "\"},\"spec\":{" +
        "\"resources\":{\"database\":{\"type\":\"example.resource/v1\",\"providerRef\":\"main\",\"properties\":{}}}" +
        (additionalSpec.Length == 0 ? "" : "," + additionalSpec) + "}}}");

    private sealed class Fixture
    {
        private Fixture(InMemoryManifestImportStore revisions, ImportApplicationManifest import,
            InMemoryDesiredStateStore desired, EnvironmentRecord environment, ManifestRevisionRef first,
            ManifestRevisionRef second)
        {
            Revisions = revisions; Import = import; Desired = desired; Environment = environment; First = first; Second = second;
        }
        public InMemoryManifestImportStore Revisions { get; }
        public ImportApplicationManifest Import { get; }
        public InMemoryDesiredStateStore Desired { get; }
        public EnvironmentRecord Environment { get; }
        public ManifestRevisionRef First { get; }
        public ManifestRevisionRef Second { get; }
        public static async Task<Fixture> Create(bool addAlias = true)
        {
            var revisions = new InMemoryManifestImportStore();
            var import = new ImportApplicationManifest(revisions, Catalog, TimeProvider.System);
            var first = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await import.ExecuteAsync(
                Payload("example", "1", ""), "actor")).ManifestRevisionRef;
            var second = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await import.ExecuteAsync(
                Payload("example", "2", ""), "actor")).ManifestRevisionRef;
            var desired = new InMemoryDesiredStateStore(revisions, TimeProvider.System);
            var environment = desired.AddEnvironment(EnvironmentId.Create("test-env"), "Test");
            if (addAlias) desired.SetProviderAlias(environment.RecordId, ProviderAlias.Create("main"),
                new ProviderResolution("provider-1", 1, "fake-v1"));
            return new Fixture(revisions, import, desired, environment, first, second);
        }
        public DeploymentChangeSet ChangeSet(ManifestRevisionRef revision, DeploymentPrecondition precondition) =>
            Assert.IsType<CreateChangeSetResult.Created>(Desired.CreateChangeSet(Environment.RecordId,
                revision.ApplicationRecordId, revision, precondition, "actor")).ChangeSet;
        public DeploymentPlan Plan(DeploymentChangeSet changeSet) =>
            Assert.IsType<PlanChangeSetResult.Planned>(Desired.Plan(changeSet.Id, "actor")).Plan;
        public ChangeSetCommit Commit(DeploymentChangeSet changeSet, DeploymentPlan plan) =>
            Assert.IsType<CommitDesiredStateResult.Committed>(Desired.Commit(changeSet.Id, plan.Id, "actor")).Commit;
    }
}
