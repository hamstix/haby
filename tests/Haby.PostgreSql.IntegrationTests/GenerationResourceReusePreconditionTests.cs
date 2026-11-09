using System.Data.Common;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    [Theory]
    [InlineData("incomplete", false)]
    [InlineData("incomplete", true)]
    [InlineData("uncertain", false)]
    [InlineData("uncertain", true)]
    [InlineData("wrong-generation", false)]
    [InlineData("wrong-generation", true)]
    [InlineData("alias-stale", false)]
    [InlineData("alias-stale", true)]
    [InlineData("profile-stale", false)]
    [InlineData("profile-stale", true)]
    [InlineData("provider-revision", false)]
    [InlineData("provider-revision", true)]
    public async Task Resource_reuse_requires_complete_baseline_exact_generation_provider_and_fresh_inputs(string mode, bool mixed)
    {
        var scenario = await Prepare(full: true, render: true);
        if (mode != "incomplete") { await AccessReady(scenario); await AccessExecute(scenario); }
        if (mode == "uncertain") await Sql(scenario.Connection, "SET session_replication_role = replica; UPDATE application_deployments SET applied_generation = NULL; UPDATE durable_create_operations SET state = 'NeedsAttention' WHERE operation_id = 'app-login-access'");
        var history = await ReuseHistory(scenario.Connection);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var revision = mixed ? await ImportUpdate(client, MixedEnvelope(scenario, 2, "mixed_pins_" + Guid.NewGuid().ToString("N"))) : SameRevision(scenario);
        DurablePlanSummary? plan = mode.EndsWith("stale", StringComparison.Ordinal) ? await UpdatePlan(client, revision, 1, created: mixed ? 1 : 0) : null;
        if (mode == "profile-stale")
        {
            var environment = (await host.Services.GetRequiredService<IEnvironmentCatalog>().GetAsync(EnvironmentId.Create("pilot"), default))!;
            var assignments = host.Services.GetRequiredService<IProfileAssignmentStore>(); var target = new Haby.Domain.ProfileAssignmentTarget.Environment();
            var old = (await assignments.FindAsync(environment, target, default))!;
            // Explicitly changing even an otherwise unused scope decision is an exact planning input change.
            var changed = new ConfigurationProfileAssignment(target, old.Declaration.Decisions.Concat([new ProfileDecision.Disable(ProfileId.Create("unused"))]));
            Assert.Equal(AssignmentOutcome.Updated, (await assignments.ApplyAsync(environment, changed,
                new AssignmentPrecondition.ExpectedConcurrencyToken(old.Token), new("tester", DateTimeOffset.UtcNow, null), default)).Outcome);
        }
        if (mode is "alias-stale" or "provider-revision")
        {
            var providers = host.Services.GetRequiredService<IProviderConfigurationStore>();
            var inputs = scenario.Plan.Inputs;
            var original = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(inputs.Environment, inputs.ProviderInstance, inputs.ConfigurationRevision, default))!;
            await providers.AddRevisionAsync(inputs.Environment, inputs.ProviderInstance, ProviderConfigurationRevisionNumber.Create(2), original.Configuration, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await host.Services.GetRequiredService<IProviderAliasStore>().ChangeAsync(inputs.Environment, inputs.Alias,
                new(inputs.ProviderInstance, inputs.ConfigurationRevision), new(inputs.ProviderInstance, ProviderConfigurationRevisionNumber.Create(2)), "tester", default)).Outcome);
        }
        if (plan is not null)
        {
            Assert.Equal(PlanningStatus.PlanStale, (await client.CheckPlanAsync(new() { PlanId = plan.PlanId })).Status);
            Assert.Equal(CommitStatus.PlanStale, (await client.CommitDesiredStateAsync(new() { ChangeSetId = plan.ChangeSetId, PlanId = plan.PlanId })).Status);
        }
        else
        {
            var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = revision, Precondition = PlanningPrecondition.ExpectedGeneration, ExpectedGeneration = mode == "wrong-generation" ? 2 : 1 });
            var result = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
            Assert.Equal(mode switch { "wrong-generation" => PlanningStatus.ChangeSetStale, "provider-revision" => PlanningStatus.UnsupportedShape, _ => PlanningStatus.PlanInputUnavailable }, result.Status);
        }
        Assert.Equal(history, await ReuseHistory(scenario.Connection));
        await using var verify = Context(scenario.Connection); Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
        await host.StopAsync();
    }

    private sealed class ReuseCancelBeforeCommit(CancellationTokenSource source) : SaveChangesInterceptor
    {
        public bool Enabled;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries().Any(x => x.Metadata.GetTableName() == "change_set_commits")) source.Cancel();
            return new(result);
        }
    }
    private sealed class ReuseCancelAfterCommit(CancellationTokenSource source) : DbTransactionInterceptor
    {
        public bool Enabled;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
        { if (Enabled) source.Cancel(); return Task.CompletedTask; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Resource_reuse_cancellation_before_acceptance_rolls_back_and_after_confirmed_commit_keeps_receipt(bool mixed, bool configurationOnly)
    {
        var scenario = await Prepare(full: true, render: true); await AccessReady(scenario); await AccessExecute(scenario);
        using var before = new CancellationTokenSource(); using var after = new CancellationTokenSource();
        var cancelBefore = new ReuseCancelBeforeCommit(before); var cancelAfter = new ReuseCancelAfterCommit(after);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"],
            s => s.AddDbContextFactory<ManifestCatalogDbContext>(o => o.AddInterceptors(cancelBefore, cancelAfter)));
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var revision = configurationOnly ? await SelectConfigurationRevision(client, scenario, 2) : await ImportUpdate(client, mixed ? MixedEnvelope(scenario, 2, "mixed_cancel_" + Guid.NewGuid().ToString("N")) : UpdateEnvelope(scenario, 2));
        var plan = await UpdatePlan(client, revision, 1, created: mixed ? 1 : 0);
        var committer = host.Services.GetRequiredService<IDurableDesiredStateCommitter>();
        cancelBefore.Enabled = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await committer.CommitAsync(DeploymentChangeSetId.Parse(plan.ChangeSetId), DeploymentPlanId.Parse(plan.PlanId), "tester", before.Token));
        await using (var verify = Context(scenario.Connection))
        { Assert.Equal(1, await verify.DeploymentGenerations.CountAsync()); Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration); }
        cancelBefore.Enabled = false; cancelAfter.Enabled = true;
        var result = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(DeploymentChangeSetId.Parse(plan.ChangeSetId), DeploymentPlanId.Parse(plan.PlanId), "tester", after.Token));
        Assert.True(after.IsCancellationRequested); Assert.Equal(2, result.Commit.Generation.Value);
        var saved = (await committer.FindCommitAsync(result.Commit.ChangeSetId, default))!;
        Assert.Equal(saved, Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(saved.ChangeSetId, saved.PlanId, "retry", default)).Commit);
        await using (var verify = Context(scenario.Connection))
        { Assert.Equal(2, await verify.DeploymentGenerations.CountAsync()); Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration); }
        await host.StopAsync();
    }
}
