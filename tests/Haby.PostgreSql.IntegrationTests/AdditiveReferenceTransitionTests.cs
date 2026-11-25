using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Haby.Persistence.PostgreSql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ResourceReferenceKernelTests
{
    [Fact]
    public async Task Applied_import_retains_server_acquisition_when_new_Reference_is_added()
    {
        var (connection, boundary, baseline, revision, secondProvider) = await AdditiveTransitionAsync("two-added");
        await using var services = Services(connection, boundary);
        var result = await services.GetRequiredService<IDurableDeploymentPlanner>().CreateAcquisitionChangeSetAsync(baseline.Environment,
            revision.ApplicationRecordId, revision, new DeploymentPrecondition.ExpectedGeneration(baseline.Commit.Generation),
            new([new(ManifestLocalId.Create("added"), ResourceAcquisitionMode.Reference, Payload("added-service")),
                new(ManifestLocalId.Create("peer"), ResourceAcquisitionMode.Reference, Payload("peer-service"))]), "tester", default);
        Assert.Equal(DurableChangeSetCreateStatus.Created, result.Status);
        var change = Assert.IsType<DeploymentChangeSet>(result.ChangeSet);
        Assert.Equal(baseline.Plan.Inputs.Acquisitions.Resource(Referenced), change.Acquisitions!.Resource(Referenced));
        Assert.Equal(3, change.Acquisitions.Resources.Count);
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var planned = await planner.CreatePlanAsync(change.Id, "tester", default);
        Assert.Equal(DurablePlanStatus.Planned, planned.Status);
        var plan = planned.Plan!;
        Assert.Equal(baseline.Plan.Inputs.Providers.Resource(Referenced), plan.Inputs.Providers.Resource(Referenced));
        Assert.Equal(secondProvider, plan.Inputs.Providers.Resource(ManifestLocalId.Create("added")).Instance);
        Assert.Equal(Referenced, Assert.Single(plan.Graph.ReusedResources).Resource);
        Assert.All(plan.Graph.Nodes, x => Assert.Contains(x.ResourceId.Value, new[] { "added", "peer" }));
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(plan.Id, default));
        var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>()
            .CommitAsync(change.Id, plan.Id, "tester", default)).Commit;
        await using (var before = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            Assert.Equal(1, (await before.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        await using var restart = Services(connection, boundary);
        var completed = await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(commit.DeploymentId, commit.Generation, default);
        Assert.Null(completed.StopOutcome);
        Assert.Equal(2, completed.Progress!.AppliedGeneration);
        Assert.Null((await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(commit.DeploymentId, commit.Generation, default)).StopOutcome);
        Assert.Equal(3, boundary.Observations);
        var followupEnvelope = JsonNode.Parse("""
            {"applicationVersion":"v3","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"reference-app"},"spec":{"resources":{"referenced":{"type":"test.capability/v1","providerRef":"sample.primary","properties":{}},"added":{"type":"test.capability/v1","providerRef":"sample.secondary","properties":{}},"peer":{"type":"test.capability/v1","providerRef":"sample.secondary","properties":{}}}}}}
            """u8)!;
        var followup = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            restart.GetRequiredService<IManifestImportStore>(), new([new(Type, "{\"type\":\"object\",\"additionalProperties\":false}")], [], [], []), TimeProvider.System)
            .ExecuteAsync(Encoding.UTF8.GetBytes(followupEnvelope.ToJsonString()), "tester")).ManifestRevisionRef;
        var nextPlanner = restart.GetRequiredService<IDurableDeploymentPlanner>();
        var nextChange = (await nextPlanner.CreateChangeSetAsync(baseline.Environment, revision.ApplicationRecordId, followup,
            new DeploymentPrecondition.ExpectedGeneration(commit.Generation), "tester", default)).ChangeSet!;
        var nextPlan = (await nextPlanner.CreatePlanAsync(nextChange.Id, "tester", default)).Plan!;
        Assert.Empty(nextPlan.Graph.Nodes);
        Assert.Equal(3, nextPlan.Graph.ReusedResources.Count);
        var nextCommit = Assert.IsType<DurableCommitResult.Committed>(await restart.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(nextChange.Id, nextPlan.Id, "tester", default)).Commit;
        var nextRun = await restart.GetRequiredService<DurableGenerationRunner>().RunAsync(nextCommit.DeploymentId, nextCommit.Generation, default);
        Assert.Null(nextRun.StopOutcome);
        Assert.Equal(3, nextRun.Progress!.AppliedGeneration);
        Assert.Equal(3, boundary.Observations);
        await using var verify = await restart.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        var retained = await verify.ResourceManagement.AsNoTracking().SingleAsync(x => x.Resource == Referenced.Value);
        Assert.Equal(ResourceOrigin.Imported, retained.Origin);
        Assert.Equal(ResourceLifecycleAuthority.UseAndRetain, retained.Authority);
        Assert.Equal(1, retained.SourceGeneration);
    }
    private async Task<(string Connection, Boundary Boundary, Prepared Baseline, ManifestRevisionRef Revision, ProviderInstanceId AddedProvider)> AdditiveTransitionAsync(string? fault = null)
    {
        var connection = await CreateDatabaseAsync("additive_reference_");
        var boundary = new Boundary();
        var baseline = await PrepareAsync(connection, boundary, mixed: false, singleMode: ResourceAcquisitionMode.ImportUseAndRetain);
        await using (var apply = Services(connection, boundary))
        {
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await apply.GetRequiredService<IDurableOperationExecutor>()
                .ExecuteAsync(Operation(baseline, "referenced-verify"), default)).Outcome);
            var baselineCompleted = await apply.GetRequiredService<DurableGenerationRunner>().RunAsync(baseline.Commit.DeploymentId, baseline.Commit.Generation, default);
            Assert.Equal(1, baselineCompleted.Progress!.AppliedGeneration);
        }
        await using var services = Services(connection, boundary);
        var configurations = services.GetRequiredService<IProviderConfigurationStore>();
        var configuration = new SampleConfiguration("verified-test-service", "sample.impl/v1");
        var secondProvider = await configurations.CreateInstanceAsync(baseline.Environment, configuration, default);
        await configurations.AddRevisionAsync(baseline.Environment, secondProvider, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
        await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(baseline.Environment, ProviderAlias.Create("sample.secondary"), null,
            new(secondProvider, ProviderConfigurationRevisionNumber.Create(1)), "tester", default);
        var envelope = JsonNode.Parse("""
            {"applicationVersion":"v2","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"reference-app"},"spec":{"resources":{"referenced":{"type":"test.capability/v1","providerRef":"sample.primary","properties":{}},"added":{"type":"test.capability/v1","providerRef":"sample.secondary","properties":{}}}}}}
            """u8)!;
        var resources = envelope["manifest"]!["spec"]!["resources"]!.AsObject();
        if (fault == "two-added") resources["peer"] = JsonNode.Parse("""{"type":"test.capability/v1","providerRef":"sample.secondary","properties":{}}"""u8);
        if (fault == "changed-provider") resources["referenced"]!["providerRef"] = "sample.secondary";
        if (fault == "changed-lifecycle") resources["referenced"]!["lifecycle"] = JsonNode.Parse("""{"deletionPolicy":"orphan"}"""u8);
        if (fault == "removed") resources.Remove("referenced");
        if (fault == "renamed") { var renamed = resources["referenced"]!.DeepClone(); resources.Remove("referenced"); resources["renamed"] = renamed; }
        var revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), new([new(Type, "{\"type\":\"object\",\"additionalProperties\":false}")], [], [], []), TimeProvider.System)
            .ExecuteAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), "tester")).ManifestRevisionRef;
        return (connection, boundary, baseline, revision, secondProvider);
    }

    [Theory]
    [InlineData("existing-input")]
    [InlineData("missing-capability")]
    [InlineData("baseline-race")]
    [InlineData("alias-race")]
    public async Task Additive_Reference_refuses_untrusted_existing_inputs_missing_capability_and_stale_baseline(string fault)
    {
        var (connection, boundary, baseline, revision, secondProvider) = await AdditiveTransitionAsync();
        await using var services = Services(connection, boundary, referenceEvidence: fault != "missing-capability");
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var requests = new List<ResourceAcquisitionRequest> { new(ManifestLocalId.Create("added"), ResourceAcquisitionMode.Reference, Payload("added-service")) };
        if (fault == "existing-input") requests.Add(new(Referenced, ResourceAcquisitionMode.Reference, Payload("replacement-origin")));
        var result = await planner.CreateAcquisitionChangeSetAsync(baseline.Environment, revision.ApplicationRecordId, revision,
            new DeploymentPrecondition.ExpectedGeneration(baseline.Commit.Generation), new(requests), "tester", default);
        if (fault is "existing-input" or "missing-capability")
        {
            Assert.Equal(DurableChangeSetCreateStatus.ReferenceNotSupported, result.Status);
            Assert.Null(result.ChangeSet);
        }
        else
        {
            Assert.Equal(DurableChangeSetCreateStatus.Created, result.Status);
            var plan = (await planner.CreatePlanAsync(result.ChangeSet!.Id, "tester", default)).Plan!;
            if (fault == "alias-race")
            {
                await services.GetRequiredService<IProviderConfigurationStore>().AddRevisionAsync(baseline.Environment, secondProvider,
                    ProviderConfigurationRevisionNumber.Create(2), new SampleConfiguration("verified-test-service", "sample.impl/v1"), default);
                Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(baseline.Environment, ProviderAlias.Create("sample.secondary"),
                    new(secondProvider, ProviderConfigurationRevisionNumber.Create(1)), new(secondProvider, ProviderConfigurationRevisionNumber.Create(2)), "tester", default)).Outcome);
            }
            else
            {
                var competingRevision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                    services.GetRequiredService<IManifestImportStore>(), new([new(Type, "{\"type\":\"object\",\"additionalProperties\":false}")], [], [], []), TimeProvider.System)
                    .ExecuteAsync("""
                    {"applicationVersion":"v3","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"reference-app"},"spec":{"resources":{"referenced":{"type":"test.capability/v1","providerRef":"sample.primary","properties":{}}}}}}
                    """u8.ToArray(), "tester")).ManifestRevisionRef;
                var competitor = (await planner.CreateChangeSetAsync(baseline.Environment, revision.ApplicationRecordId, competingRevision,
                    new DeploymentPrecondition.ExpectedGeneration(baseline.Commit.Generation), "tester", default)).ChangeSet!;
                var other = (await planner.CreatePlanAsync(competitor.Id, "tester", default)).Plan!;
                Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(competitor.Id, other.Id, "tester", default));
            }
            Assert.Equal(fault == "alias-race" ? DurablePlanCurrentStatus.PlanStale : DurablePlanCurrentStatus.ChangeSetStale, await planner.CheckPlanAsync(plan.Id, default));
            var committed = await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(result.ChangeSet.Id, plan.Id, "tester", default);
            Assert.True(committed is DurableCommitResult.PlanStale or DurableCommitResult.ChangeSetStale);
        }
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Single(await verify.ResourceManagement.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.ResourceObservationReceipts.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, boundary.Observations);
    }

    [Theory]
    [InlineData("changed-provider")]
    [InlineData("changed-lifecycle")]
    [InlineData("removed")]
    [InlineData("renamed")]
    public async Task Additive_Reference_intake_refuses_changed_removed_or_renamed_existing_Resources(string fault)
    {
        var (connection, boundary, baseline, revision, _) = await AdditiveTransitionAsync(fault);
        await using var services = Services(connection, boundary);
        var result = await services.GetRequiredService<IDurableDeploymentPlanner>().CreateAcquisitionChangeSetAsync(baseline.Environment,
            revision.ApplicationRecordId, revision, new DeploymentPrecondition.ExpectedGeneration(baseline.Commit.Generation),
            new([new(ManifestLocalId.Create("added"), ResourceAcquisitionMode.Reference, Payload("added-service"))]), "tester", default);
        Assert.Equal(DurableChangeSetCreateStatus.UnsupportedShape, result.Status);
        Assert.Null(result.ChangeSet);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
        Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Single(await verify.ResourceManagement.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.ResourceObservationReceipts.AsNoTracking().ToArrayAsync());
    }

}
