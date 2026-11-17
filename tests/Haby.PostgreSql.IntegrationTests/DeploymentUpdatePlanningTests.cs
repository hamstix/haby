using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Fact]
    public async Task Update_plan_roundtrips_an_accepted_deep_Profile_from_a_fully_Applied_baseline()
    {
        JsonNode? envelope = null;
        var payload = Encoding.UTF8.GetBytes("""{"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"deep","revision":1},"spec":{"documents":{"deep-settings":{"fileName":"deep.json","format":"json","template":""" + new string('[', 60) + "0" + new string(']', 60) + "}}}}");
        Assert.True(ProfilePayloadParser.ParseProfile(payload).IsSuccess);
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e =>
        {
            e["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["configuration"] =
                JsonNode.Parse("""{"documents":["deep-settings"]}"""u8);
            envelope = e.DeepClone();
        }, acceptGeneration: async (services, environment, revision) =>
        {
            var imported = Assert.IsType<ImportConfigurationProfileResult.Succeeded>(await new ImportConfigurationProfile(
                services.GetRequiredService<IConfigurationProfileImportStore>(), services.GetRequiredService<TimeProvider>()).ExecuteAsync(payload, "tester"));
            var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            Assert.Equal(AssignmentOutcome.Created, (await new PostgreSqlProfileAssignmentStore(contexts).ApplyAsync(
                new(environment, EnvironmentId.Create("initial")), new(new ProfileAssignmentTarget.Environment(), [new ProfileDecision.Use(imported.Revision.Reference)]),
                new AssignmentPrecondition.ExpectedAbsence(), new("tester", services.GetRequiredService<TimeProvider>().GetUtcNow(), null), default)).Outcome);
            return await new InternalAcceptedGenerationBuilder(services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>(),
                (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>(), services.GetServices<IInternalActionHandler>().ToArray())
                .AcceptForEffectAsync(environment, revision.ApplicationRecordId, revision, new DeploymentPrecondition.ExpectedAbsence(), "tester", default);
        });
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        var actions = await PrepareRollout(services, scenario, deployment, identity);
        await SubmitRollout(actions, scenario, deployment);
        var ready = await BatchFacade(services, scenario, actions, deployment, identity).WaitAsync(scenario.Commit.DeploymentId,
            scenario.Commit.Generation, "deep-applied-baseline", "operator", new(MaximumRounds: 1), null, default);
        Assert.True(ready.CurrentApplied, ready.CompletionReason);
        envelope!["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:v2";
        var reference = await ImportUpdate(services, envelope);
        var change = await UpdateChange(services, scenario, reference);
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var result = await planner.CreateInternalPlanAsync(change.Id, "tester", default, handlers);
        Assert.Equal(DurablePlanStatus.Planned, result.Status);
        var plan = result.Plan!;
        Assert.Single(plan.Inputs.Configuration!.Profiles);
        Assert.Equal("deep", plan.Inputs.Configuration.Profiles[0].Reference.ProfileId.Value);
        Assert.Contains("registry.example/app:v2", Assert.Single(plan.Graph.WorkloadUpdate!.Document.Members).Delta.Json);
        Assert.Equal(new[] { "deep-settings" }, Assert.Single(plan.Graph.WorkloadUpdate.Document.Members).PrepareDocuments);
        await using var restarted = InternalServices(scenario.Connection, scenario.Time);
        var loaded = (await restarted.GetRequiredService<PostgreSqlDurableDeploymentPlanner>().FindPlanAsync(plan.Id, default))!;
        Assert.Equal(ConfigurationPlanningSnapshotCodec.Encode(plan.Inputs.Configuration), ConfigurationPlanningSnapshotCodec.Encode(loaded.Inputs.Configuration!));
        Assert.Equal(plan.Graph.WorkloadUpdate.Fingerprint, loaded.Graph.WorkloadUpdate!.Fingerprint);
        Assert.Equal(DurablePlanCurrentStatus.Current, await restarted.GetRequiredService<PostgreSqlDurableDeploymentPlanner>().CheckInternalPlanAsync(plan.Id, handlers, default));
        Assert.Equal(DurablePlanStatus.UnsupportedShape, (await planner.CreateOperatorPlanAsync(change.Id, "operator", default, handlers)).Status);
    }
    private async Task<(InternalScenario Scenario, JsonNode Envelope)> AppliedUpdateBaseline(bool initial, bool configured, bool multiple = false)
    {
        JsonNode? envelope = null;
        var scenario = await InternalScenarioAsync(initialOnly: initial, twoComponents: multiple, configureManifest: e =>
        {
            if (configured) ConfiguredDocuments(e, secondDocument: true);
            if (configured && multiple)
            {
                var components = e["manifest"]!["spec"]!["components"]!;
                components["worker"]!["documents"] = components["api"]!["documents"]!.DeepClone();
                components["worker"]!["workloads"]!["main"]!["properties"]!["configuration"] =
                    components["api"]!["workloads"]!["main"]!["properties"]!["configuration"]!.DeepClone();
            }
            envelope = e.DeepClone();
        });
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured);
        await SubmitRollout(actions, scenario, deployment);
        if (multiple)
        {
            var key = await actions.AcceptAsync(scenario.Commit, new ProviderInput.Workload(ManifestLocalId.Create("worker"), ManifestLocalId.Create("main")),
                deployment.Contract.Action, "submit-worker", default);
            Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(key, default)).Outcome);
        }
        var ready = await BatchFacade(services, scenario, actions, deployment, identity).WaitAsync(scenario.Commit.DeploymentId,
            scenario.Commit.Generation, "applied-baseline", "operator", new(MaximumRounds: 1), null, default);
        Assert.True(ready.CurrentApplied, ready.Outcome + "/" + ready.CompletionReason + "/" + ready.Stop);
        return (scenario, envelope!);
    }

    private static async Task<ManifestRevisionRef> ImportUpdate(ServiceProvider services, JsonNode envelope, string version = "update-v2")
    {
        envelope["applicationVersion"] = version;
        var importer = new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(),
            new([PostgreSqlDatabaseContract.Definition], [KubernetesContract.Definition], [], []), services.GetRequiredService<TimeProvider>());
        return Assert.IsType<ImportApplicationManifestResult.Succeeded>(await importer.ExecuteAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), "tester")).ManifestRevisionRef;
    }

    private static async Task<DeploymentChangeSet> UpdateChange(ServiceProvider services, InternalScenario scenario, ManifestRevisionRef revision) =>
        (await services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>().CreateChangeSetAsync(scenario.Environment,
            revision.ApplicationRecordId, revision, new DeploymentPrecondition.ExpectedGeneration(scenario.Commit.Generation), "tester", default)).ChangeSet!;

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Update_foundation_roundtrips_original_inventory_and_cannot_execute_or_complete(bool initial, bool configured)
    {
        var (scenario, envelope) = await AppliedUpdateBaseline(initial, configured);
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:v2";
        var reference = await ImportUpdate(services, envelope);
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var committer = (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>();
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var change = await UpdateChange(services, scenario, reference);
        Assert.Equal(DurablePlanStatus.UnsupportedShape, (await planner.CreateOperatorPlanAsync(change.Id, "operator", default, handlers)).Status);
        var result = await planner.CreateInternalPlanAsync(change.Id, "tester", default, handlers);
        Assert.Equal(DurablePlanStatus.Planned, result.Status);
        var plan = result.Plan!;
        var update = plan.Graph.WorkloadUpdate!.Document;
        Assert.Null(plan.Graph.Retirement);
        Assert.Equal(scenario.Commit.Generation.Value, update.BaselineGeneration);
        Assert.Equal(scenario.Commit.Generation.Value + 1, update.TargetGeneration);
        Assert.Equal(scenario.Commit.PlanId.ToString(), update.Plan.ToString());
        Assert.Equal(initial ? 0 : 1, plan.Graph.ReusedResources.Count);
        Assert.Equal(configured ? 2 : 0, Assert.Single(update.Members).PrepareDocuments.Length);
        Assert.Equal(configured ? 2 : 0, update.ObsoletePublications.Length);
        Assert.Equal(configured ? 2 : 0, update.RetainedObjects.Length);
        Assert.Equal(configured ? 1 : 0, update.RetainedBindings.Length);
        Assert.All(update.RetainedObjects, x => Assert.Equal(scenario.Commit.Generation.Value, x.OriginGeneration));
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckInternalPlanAsync(plan.Id, handlers, default));
        Assert.NotEqual(DurablePlanCurrentStatus.Current, await planner.CheckOperatorPlanAsync(plan.Id, handlers, default));
        Assert.IsType<DurableCommitResult.UnsupportedShape>(await committer.CommitOperatorAsync(change.Id, plan.Id, "operator", default, handlers));
        var accepted = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitInternalAsync(change.Id, plan.Id, "tester", default, handlers)).Commit;
        Assert.Equal(scenario.Commit.Generation.Value + 1, accepted.Generation.Value);
        Assert.Equal(accepted, Assert.IsType<DurableCommitResult.Committed>(await committer.CommitInternalAsync(change.Id, plan.Id, "retry", default, handlers)).Commit);
        Assert.IsType<DurableCommitResult.UnsupportedShape>(await committer.CommitOperatorAsync(change.Id, plan.Id, "operator", default, handlers));
        await using var restarted = InternalServices(scenario.Connection, scenario.Time);
        var loaded = (await restarted.GetRequiredService<IDurableDeploymentPlanner>().FindPlanAsync(plan.Id, default))!;
        Assert.Equal(DurableOperationGraphCodec.Encode(plan.Graph), DurableOperationGraphCodec.Encode(loaded.Graph));
        var saved = await restarted.GetRequiredService<IDurableDesiredStateCommitter>().FindGenerationAsync(accepted.DeploymentId, accepted.Generation, default);
        Assert.Equal(plan.Graph.WorkloadUpdate.Fingerprint, saved!.Graph.WorkloadUpdate!.Fingerprint);
        var contexts = restarted.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        await using (var act = await contexts.CreateDbContextAsync())
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => InternalGenerationAuthority.RequireAsync(act, accepted, default));
            var source = await act.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == accepted.Generation.Value);
            await Assert.ThrowsAsync<InvalidDataException>(() => InitialRetirementAuthority.ValidateAsync(act, source, default));
        }
        var actions = Actions(restarted, handlers.Single());
        await Assert.ThrowsAsync<InvalidDataException>(() => actions.AcceptAsync(accepted, new ProviderInput.Workload(ManifestLocalId.Create("api"), ManifestLocalId.Create("main")),
            InternalDeploymentAction.Create, "forbidden-update", default).AsTask());
        await using var verify = await contexts.CreateDbContextAsync();
        var owner = await verify.ApplicationDeployments.AsNoTracking().SingleAsync();
        Assert.Equal(scenario.Commit.Generation.Value, owner.AppliedGeneration);
        Assert.Equal(accepted.Generation.Value, owner.DesiredGeneration);
        Assert.False(await verify.ActionIntents.AnyAsync(x => x.Generation == accepted.Generation.Value));
        Assert.False(await verify.GenerationStageCheckpoints.AnyAsync(x => x.Generation == accepted.Generation.Value));
        Assert.All(await verify.OwnedObjects.AsNoTracking().ToArrayAsync(), x => Assert.Equal(scenario.Commit.Generation.Value, x.Generation));
    }

    [Theory]
    [InlineData("version-only")]
    [InlineData("equivalent")]
    public async Task Update_request_semantics_preserve_new_revision_and_exact_existing_noop(string shape)
    {
        var (scenario, envelope) = await AppliedUpdateBaseline(true, false);
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var reference = shape == "equivalent" ? scenario.Revision : await ImportUpdate(services, envelope);
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var change = await UpdateChange(services, scenario, reference);
        var plan = (await planner.CreateInternalPlanAsync(change.Id, "tester", default, handlers)).Plan!;
        var member = Assert.Single(plan.Graph.WorkloadUpdate!.Document.Members);
        Assert.Contains("UnchangedImage", member.Delta.Json);
        Assert.Equal(scenario.Commit.Generation.Value + (shape == "equivalent" ? 0 : 1), plan.Graph.WorkloadUpdate.Document.TargetGeneration);
        var accepted = Assert.IsType<DurableCommitResult.Committed>(await ((PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>())
            .CommitInternalAsync(change.Id, plan.Id, "tester", default, handlers)).Commit;
        Assert.Equal(scenario.Commit.Generation.Value + (shape == "equivalent" ? 0 : 1), accepted.Generation.Value);
        if (shape == "equivalent")
        {
            var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
            var inspected = await WorkloadGenerationAuthority.InspectAsync(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
                ConfigurationPreparation(services).Preparation, ConfiguredActions(services, deployment, identity), deployment.Contract.Action,
                identity.Contract.Action, scenario.Commit.DeploymentId, scenario.Commit.Generation, default, completedRead: true);
            Assert.Null(inspected.Reason);
            Assert.Equal(scenario.Commit, inspected.Accepted);
        }
    }

    [Theory]
    [InlineData("container")]
    [InlineData("document")]
    [InlineData("component")]
    [InlineData("component-removed")]
    [InlineData("workload")]
    [InlineData("workload-removed")]
    [InlineData("resource-added")]
    [InlineData("resource-removed")]
    [InlineData("unapplied")]
    public async Task Update_refuses_unbounded_delta_or_incomplete_baseline_without_partial_acceptance(string fault)
    {
        var (scenario, envelope) = await AppliedUpdateBaseline(!fault.StartsWith("resource-", StringComparison.Ordinal), false,
            multiple: fault is "component-removed" or "workload-removed");
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var components = envelope["manifest"]!["spec"]!["components"]!;
        var properties = components["api"]!["workloads"]!["main"]!["properties"]!;
        switch (fault)
        {
            case "container": properties["containerName"] = "other"; break;
            case "document": components["api"]!["documents"] = JsonNode.Parse("""{"extra":{"fileName":"extra.json","format":"json","template":{}}}"""u8); break;
            case "component": components["extra"] = components["api"]!.DeepClone(); break;
            case "component-removed": components.AsObject().Remove("worker"); break;
            case "workload": components["api"]!["workloads"]!["extra"] = components["api"]!["workloads"]!["main"]!.DeepClone(); break;
            case "workload-removed": components["worker"]!["workloads"]!.AsObject().Remove("main"); break;
            case "resource-added":
                var resources = envelope["manifest"]!["spec"]!["resources"]!;
                resources["extra"] = resources["db"]!.DeepClone(); resources["extra"]!["properties"]!["name"] = "extra_db";
                break;
            case "resource-removed": envelope["manifest"]!["spec"]!.AsObject().Remove("resources"); break;
        }
        var reference = await ImportUpdate(services, envelope);
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        if (fault == "unapplied")
            await using (var arrange = await contexts.CreateDbContextAsync()) await arrange.ApplicationDeployments.ExecuteUpdateAsync(s => s.SetProperty(x => x.AppliedGeneration, (long?)null));
        var change = await UpdateChange(services, scenario, reference);
        var result = await services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>().CreateInternalPlanAsync(change.Id, "tester", default, services.GetServices<IInternalActionHandler>().ToArray());
        Assert.NotEqual(DurablePlanStatus.Planned, result.Status);
        Assert.Null(result.Plan);
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Equal(scenario.Commit.Generation.Value, await verify.DeploymentGenerations.CountAsync());
        Assert.False(await verify.ChangeSetCommits.AnyAsync(x => x.ChangeSetId.ToString() == change.Id.ToString()));
    }

    [Fact]
    public async Task Update_commit_CAS_accepts_only_one_successor_from_the_frozen_Applied_baseline()
    {
        var (scenario, envelope) = await AppliedUpdateBaseline(true, false);
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var reference = await ImportUpdate(services, envelope);
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var committer = (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>();
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var first = await UpdateChange(services, scenario, reference); var second = await UpdateChange(services, scenario, reference);
        var p1 = (await planner.CreateInternalPlanAsync(first.Id, "tester", default, handlers)).Plan!;
        var p2 = (await planner.CreateInternalPlanAsync(second.Id, "tester", default, handlers)).Plan!;
        var results = await Task.WhenAll(committer.CommitInternalAsync(first.Id, p1.Id, "tester", default, handlers).AsTask(),
            committer.CommitInternalAsync(second.Id, p2.Id, "tester", default, handlers).AsTask());
        Assert.Single(results.OfType<DurableCommitResult.Committed>());
        Assert.Single(results.OfType<DurableCommitResult.ChangeSetStale>());
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(2, await verify.DeploymentGenerations.CountAsync());
        Assert.Equal(2, await verify.ChangeSetCommits.CountAsync());
        Assert.Equal(scenario.Commit.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("uid")]
    [InlineData("owner")]
    [InlineData("managed")]
    [InlineData("completion")]
    [InlineData("binding-revoked")]
    public async Task Update_planning_rejects_missing_corrupt_foreign_or_revoked_original_proofs(string fault)
    {
        var (scenario, envelope) = await AppliedUpdateBaseline(true, true);
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        // Privileged catalog edits exercise refusal of corrupt persisted evidence. Normal
        // writers cannot mutate these immutable rows; arrange/act/assert use new contexts.
        await using (var arrange = await contexts.CreateDbContextAsync())
        {
            var outputs = arrange.OperationOutputs.Where(x => x.Contract == "haby.kubernetes.deployment-receipt/v4");
            if (fault == "receipt") await outputs.ExecuteDeleteAsync();
            else if (fault is "uid" or "owner" or "managed")
            {
                var output = await outputs.AsNoTracking().SingleAsync();
                var receipt = DeploymentIntents.DecodeReceipt(output.Payload);
                var changed = fault switch
                {
                    "uid" => receipt with { Uid = "replaced-uid" },
                    "owner" => receipt with { Owner = Guid.NewGuid() },
                    _ => receipt with { ManagedWorkload = Guid.NewGuid() }
                };
                var payload = DeploymentIntents.Encode(changed);
                await outputs.ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, payload));
            }
            else if (fault == "completion")
                await arrange.GenerationStageCheckpoints.Where(x => x.Stage == "RetirementCompleted").ExecuteUpdateAsync(s => s.SetProperty(x => x.EvidenceFingerprint, new string('0', 64)));
            else
                await arrange.RuntimeIdentityBindings.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Revoked")
                    .SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow).SetProperty(x => x.RevokedBy, "operator").SetProperty(x => x.ConcurrencyToken, Guid.NewGuid()));
        }
        var reference = await ImportUpdate(services, envelope);
        var change = await UpdateChange(services, scenario, reference);
        var result = await services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>().CreateInternalPlanAsync(change.Id,
            "tester", default, services.GetServices<IInternalActionHandler>().ToArray());
        Assert.Null(result.Plan); Assert.NotEqual(DurablePlanStatus.Planned, result.Status);
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Single(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync());
        Assert.Equal(scenario.Commit.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
    }

    [Theory]
    [InlineData("applied")]
    [InlineData("profile")]
    [InlineData("placement")]
    [InlineData("provider")]
    public async Task Update_check_and_commit_refuse_changed_baseline_or_selection_after_restart(string fault)
    {
        var (scenario, envelope) = await AppliedUpdateBaseline(true, false);
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var reference = await ImportUpdate(services, envelope);
        var change = await UpdateChange(services, scenario, reference);
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var plan = (await planner.CreateInternalPlanAsync(change.Id, "tester", default, handlers)).Plan!;
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        if (fault == "applied")
            await using (var arrange = await contexts.CreateDbContextAsync()) await arrange.ApplicationDeployments.ExecuteUpdateAsync(s => s.SetProperty(x => x.AppliedGeneration, (long?)null));
        else if (fault == "profile")
        {
            var environment = new EnvironmentMetadata(scenario.Environment, plan.Inputs.EnvironmentKey);
            var target = new ProfileAssignmentTarget.Application(plan.Inputs.Providers.Declaration.ApplicationId);
            Assert.Equal(AssignmentOutcome.Created, (await new PostgreSqlProfileAssignmentStore(contexts).ApplyAsync(environment,
                new(target, []), new AssignmentPrecondition.ExpectedAbsence(), new("writer", scenario.Time.GetUtcNow(), null), default)).Outcome);
        }
        else if (fault == "provider")
        {
            var exact = plan.Inputs.Providers.Entries.Values.Single();
            var previousBinding = new ProviderAliasBinding(exact.Instance, exact.Configuration);
            var nextRevision = ProviderConfigurationRevisionNumber.Create(2);
            Haby.Kubernetes.KubernetesProviderConfiguration configuration;
            await using (var arrange = await contexts.CreateDbContextAsync())
            {
                var row = await arrange.ProviderConfigurationRevisions.AsNoTracking().SingleAsync(x => x.ProviderInstanceId.ToString() == exact.Instance.ToString());
                configuration = new Haby.Kubernetes.KubernetesProviderConfigurationCodec().DecodeTyped(row.PayloadJson);
            }
            await services.GetRequiredService<IProviderConfigurationStore>().AddRevisionAsync(scenario.Environment, exact.Instance, nextRevision, configuration, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(scenario.Environment,
                exact.Alias, previousBinding, new(exact.Instance, nextRevision), "writer", default)).Outcome);
        }
        else
        {
            var codec = new KubernetesPlacementCodec();
            var portable = JsonNode.Parse(InternalPlacementInput.Decode(plan.Inputs.Providers.Entries.Values.Single().Placement!).Declaration.Payload)!;
            portable["metadata"]!["name"] = "later";
            var declaration = codec.Decode(Encoding.UTF8.GetBytes(portable.ToJsonString()));
            var alias = ProviderAlias.Create("kubernetes.primary");
            var key = new PlacementRevisionKey(scenario.Environment, alias, declaration.Name, declaration.Revision);
            await new PostgreSqlPlacementRevisionStore(contexts, codec).CompareAndCreateAsync(key, declaration, new("writer", scenario.Time.GetUtcNow()), default);
            var selections = new PostgreSqlPlacementSelectionStore(contexts, codec);
            var target = PlacementSelectionTarget.Create("environment", "", "", "");
            var current = (await selections.FindAsync(scenario.Environment, alias, target, default))!;
            await selections.ApplyAsync(target, key, new PlacementSelectionPrecondition.ExpectedToken(current.Token), new("writer", scenario.Time.GetUtcNow(), null), default);
        }
        await using var restarted = InternalServices(scenario.Connection, scenario.Time);
        var fresh = restarted.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        Assert.NotEqual(DurablePlanCurrentStatus.Current, await fresh.CheckInternalPlanAsync(plan.Id, handlers, default));
        Assert.Equal(plan.Fingerprint, (await fresh.FindPlanAsync(plan.Id, default))!.Fingerprint);
        var refused = await ((PostgreSqlDurableDesiredStateCommitter)restarted.GetRequiredService<IDurableDesiredStateCommitter>()).CommitInternalAsync(change.Id, plan.Id, "tester", default, handlers);
        Assert.IsNotType<DurableCommitResult.Committed>(refused);
        Assert.Null((await fresh.CreateInternalPlanAsync(change.Id, "tester", default, handlers)).Plan);
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Single(await verify.DeploymentGenerations.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.ChangeSetCommits.AsNoTracking().ToArrayAsync());
        Assert.Equal(scenario.Commit.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
    }
}
