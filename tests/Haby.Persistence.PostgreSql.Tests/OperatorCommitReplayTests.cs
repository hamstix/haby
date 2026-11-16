using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class OperatorCommitReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generation_creation_receipt_still_requires_its_exact_original_audit_and_Plan(bool workload)
    {
        var (change, receipt, plan, generation) = Noop();
        plan.InternalEffects = generation.InternalEffects = workload;
        generation.ChangeSetId = change.Id;
        generation.PlanId = receipt.PlanId;
        generation.ActorId = receipt.ActorId;
        generation.AcceptedAt = receipt.CommittedAt;
        generation.GraphJson = plan.GraphJson;
        generation.Fingerprint = plan.Fingerprint;
        Assert.True(PostgreSqlDurableDesiredStateCommitter.OperatorReplayMatches(change, receipt, plan, generation));
        receipt.ActorId = "another-operator";
        Assert.False(PostgreSqlDurableDesiredStateCommitter.OperatorReplayMatches(change, receipt, plan, generation));
        receipt.ActorId = generation.ActorId;
        receipt.CommittedAt = receipt.CommittedAt.AddTicks(10);
        Assert.False(PostgreSqlDurableDesiredStateCommitter.OperatorReplayMatches(change, receipt, plan, generation));
    }

    [Fact]
    public void Resource_only_noop_receipt_keeps_its_own_original_Plan_actor_and_time_after_generation_creation()
    {
        var (change, receipt, plan, generation) = Noop();
        Assert.NotEqual(generation.ChangeSetId, receipt.ChangeSetId);
        Assert.NotEqual(generation.PlanId, receipt.PlanId);
        Assert.NotEqual(generation.ActorId, receipt.ActorId);
        Assert.NotEqual(generation.AcceptedAt, receipt.CommittedAt);
        Assert.True(PostgreSqlDurableDesiredStateCommitter.OperatorReplayMatches(change, receipt, plan, generation));
        Assert.Equal("noop-operator", receipt.ActorId);
        Assert.Equal(change.Id, receipt.ChangeSetId);
    }

    [Theory]
    [InlineData("plan-member")]
    [InlineData("generation-scope")]
    [InlineData("input")]
    [InlineData("baseline")]
    [InlineData("workload-noop")]
    public void Noop_replay_cannot_borrow_unrelated_Plan_generation_inputs_or_Workload_authority(string fault)
    {
        var (change, receipt, plan, generation) = Noop();
        if (fault == "plan-member") plan.ChangeSetId = Guid.CreateVersion7();
        if (fault == "generation-scope") generation.ApplicationRecordId = Guid.CreateVersion7();
        if (fault == "input") plan.ProviderInputsJson = "changed";
        if (fault == "baseline") plan.GraphJson = DurableOperationGraphCodec.Encode(new([]));
        if (fault == "workload-noop") plan.InternalEffects = generation.InternalEffects = true;
        Assert.False(PostgreSqlDurableDesiredStateCommitter.OperatorReplayMatches(change, receipt, plan, generation));
    }

    private static (DurableChangeSetRow Change, ChangeSetCommitRow Receipt, DurablePlanRow Plan, DeploymentGenerationRow Generation) Noop()
    {
        var change = new DurableChangeSetRow { Id = Guid.CreateVersion7(), EnvironmentRecordId = Guid.CreateVersion7(), ApplicationRecordId = Guid.CreateVersion7(), RevisionNumber = 2, ExpectedGeneration = 3, ActorId = "change-operator" };
        var receipt = new ChangeSetCommitRow { ChangeSetId = change.Id, PlanId = Guid.CreateVersion7(), DeploymentId = Guid.CreateVersion7(), Generation = 3, ActorId = "noop-operator", CommittedAt = DateTimeOffset.Parse("2026-11-15T12:00:00Z") };
        var generation = new DeploymentGenerationRow
        {
            ChangeSetId = Guid.CreateVersion7(),
            PlanId = Guid.CreateVersion7(),
            DeploymentId = receipt.DeploymentId,
            Generation = 3,
            EnvironmentRecordId = change.EnvironmentRecordId,
            ApplicationRecordId = change.ApplicationRecordId,
            RevisionNumber = 2,
            EnvironmentKey = "pilot",
            SnapshotFormatVersion = 4,
            ProviderInputsJson = "same-canonical-provider-inputs",
            ConfigurationJson = "same-canonical-configuration",
            Fingerprint = "v4:" + new string('a', 64),
            GraphJson = DurableOperationGraphCodec.Encode(new([])),
            Presence = nameof(DesiredPresence.Present),
            ActorId = "creation-operator",
            AcceptedAt = receipt.CommittedAt.AddHours(-1)
        };
        var graph = new DurableOperationGraph([], [new(ManifestLocalId.Create("db"), Guid.CreateVersion7(), 1, new string('b', 64))],
            new(ApplicationDeploymentId.Parse(receipt.DeploymentId.ToString()), receipt.Generation, generation.Fingerprint));
        var plan = new DurablePlanRow
        {
            Id = receipt.PlanId,
            ChangeSetId = change.Id,
            EnvironmentRecordId = change.EnvironmentRecordId,
            ApplicationRecordId = change.ApplicationRecordId,
            RevisionNumber = 2,
            EnvironmentKey = "pilot",
            SnapshotFormatVersion = 4,
            ProviderInputsJson = generation.ProviderInputsJson,
            ConfigurationJson = generation.ConfigurationJson,
            GraphJson = DurableOperationGraphCodec.Encode(graph),
            Fingerprint = "v4:" + new string('c', 64),
            ActorId = "plan-operator"
        };
        return (change, receipt, plan, generation);
    }
}
