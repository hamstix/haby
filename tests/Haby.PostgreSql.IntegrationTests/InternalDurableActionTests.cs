using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Haby.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    internal sealed record InternalScenario(string Connection, TimeProvider Time, EnvironmentRecordId Environment,
        DurableChangeSetCommit Commit, ManifestRevisionRef Revision);
    internal async Task<InternalScenario> InternalScenarioAsync(Func<ISecretStore, EnvironmentRecordId, Task<KubernetesProviderConfiguration>>? suppliedConfiguration = null, string namespaceName = "test", bool subMicrosecondCommit = false,
        Func<ISecretStore, EnvironmentRecordId, ProviderAliasBinding, KubernetesProviderConfiguration, CancellationToken, ValueTask<ProviderPreflightResult>>? actualKubernetesProbe = null, bool twoComponents = false, bool distinctPlacements = false, bool initialOnly = false, bool invalidSecondWorkload = false,
        Func<ServiceProvider, EnvironmentRecordId, ManifestRevisionRef, Task<DurableChangeSetCommit>>? acceptGeneration = null)
    {
        var prepared = initialOnly ? ((string Connection, AdjustableTimeProvider Time, EnvironmentRecordId Environment, ManifestRevisionRef Revision, ProviderAliasBinding First, ProviderAliasBinding Second, string Password)?)null : await PrepareCommitAsync();
        var connection = prepared?.Connection ?? await NewDatabaseAsync();
        var time = prepared?.Time ?? new AdjustableTimeProvider();
        ProviderAliasBinding? fixtureBinding = null;
        InternalKubernetesFixtureProbe? probe = actualKubernetesProbe is null ? null : (secrets, environment, configuration, token) =>
            actualKubernetesProbe(secrets, environment, fixtureBinding ?? throw new InvalidDataException("Fixture binding unavailable."), configuration, token);
        await using var services = Services(connection, time, internalActions: true, kubernetesProbe: probe);
        if (initialOnly) await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var environmentId = prepared?.Environment ?? await services.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("initial"), default);
        if (prepared is { } setup) Assert.Equal(ProviderPreflightOutcome.Ready, (await services.GetRequiredService<IProviderRevisionPreflight>().CheckAsync(environmentId, setup.First, default)).Outcome);
        var catalog = new ManifestContractCatalog([PostgreSqlDatabaseContract.Definition], [KubernetesContract.Definition], [], []);
        var envelope = JsonNode.Parse(Envelope)!;
        envelope["applicationVersion"] = "internal-base";
        envelope["manifest"]!["metadata"]!["id"] = "effect-" + Guid.NewGuid().ToString("N")[..12];
        envelope["manifest"]!["spec"]!["resources"]!["db"]!["properties"]!["name"] = "haby202_" + Guid.NewGuid().ToString("N");
        var importer = new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(), catalog, time);
        DurableChangeSetCommit? original = null;
        if (!initialOnly)
        {
            var baseline = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await importer.ExecuteAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), "tester"));
            var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
            var change = (await planner.CreateChangeSetAsync(environmentId, baseline.ManifestRevisionRef.ApplicationRecordId, baseline.ManifestRevisionRef,
                new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
            var plan = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
            var committer = services.GetRequiredService<IDurableDesiredStateCommitter>();
            original = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(change.Id, plan.Id, "tester", default)).Commit;
            Assert.Null((await services.GetRequiredService<DurableGenerationRunner>().RunAsync(original.DeploymentId, original.Generation, default)).StopOutcome);
        }
        if (initialOnly) envelope["manifest"]!["spec"]!.AsObject().Remove("resources");
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        await services.GetRequiredService<ISecretStore>().CreateAsync(new(new SecretOwner.Environment(environmentId), SecretPurpose.ProviderAdministrator), secret, "fixture-token"u8.ToArray(), default);
        var configuration = suppliedConfiguration is not null ? await suppliedConfiguration(services.GetRequiredService<ISecretStore>(), environmentId) : new KubernetesProviderConfiguration(new Uri("https://127.0.0.1:6443"), null, secret, null, null);
        var providerStore = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await providerStore.CreateInstanceAsync(environmentId, configuration, default);
        await providerStore.AddRevisionAsync(environmentId, instance, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
        fixtureBinding = new(instance, ProviderConfigurationRevisionNumber.Create(1));
        var alias = ProviderAlias.Create("kubernetes.primary");
        Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environmentId, alias, null,
            fixtureBinding, "tester", default)).Outcome);
        var placementCodec = new KubernetesPlacementCodec();
        var placement = placementCodec.Decode("""{"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"standard","revision":1},"spec":{"namespace":"test"}}"""u8.ToArray());
        placement = placementCodec.Decode(Encoding.UTF8.GetBytes(placement.Payload.Replace("\"test\"", "\"" + namespaceName + "\"", StringComparison.Ordinal)));
        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var reference = new PlacementRevisionKey(environmentId, alias, placement.Name, placement.Revision);
        await new PostgreSqlPlacementRevisionStore(factory, placementCodec).CompareAndCreateAsync(reference, placement, new("tester", time.GetUtcNow()), default);
        await new PostgreSqlPlacementSelectionStore(factory, placementCodec).ApplyAsync(PlacementSelectionTarget.Create("environment", "", "", ""), reference,
            new PlacementSelectionPrecondition.ExpectedAbsence(), new("tester", time.GetUtcNow(), null), default);
        if (distinctPlacements)
        {
            var workerPlacement = placementCodec.Decode(Encoding.UTF8.GetBytes(placement.Payload.Replace("standard", "worker-placement", StringComparison.Ordinal)
                .Replace("\"" + namespaceName + "\"", "\"" + namespaceName + "-worker\"", StringComparison.Ordinal)));
            var workerReference = new PlacementRevisionKey(environmentId, alias, workerPlacement.Name, workerPlacement.Revision);
            await new PostgreSqlPlacementRevisionStore(factory, placementCodec).CompareAndCreateAsync(workerReference, workerPlacement, new("tester", time.GetUtcNow()), default);
            await new PostgreSqlPlacementSelectionStore(factory, placementCodec).ApplyAsync(PlacementSelectionTarget.Create("named", "worker-selected", "", ""), workerReference,
                new PlacementSelectionPrecondition.ExpectedAbsence(), new("tester", time.GetUtcNow(), null), default);
        }
        envelope["applicationVersion"] = "internal-target";
        envelope["manifest"]!["spec"]!["components"] = JsonNode.Parse("""{"api":{"workloads":{"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary","properties":{"image":{"reference":"registry.example/app:v1"}}}}}}"""u8);
        if (twoComponents)
        {
            var worker = envelope["manifest"]!["spec"]!["components"]!["api"]!.DeepClone();
            if (distinctPlacements) worker["workloads"]!["main"]!["placementRef"] = "worker-selected";
            envelope["manifest"]!["spec"]!["components"]!["worker"] = worker;
        }
        if (invalidSecondWorkload)
        {
            var invalid = envelope["manifest"]!["spec"]!["components"]!["api"]!.DeepClone();
            invalid["workloads"]!["main"]!["properties"]!["env"] = JsonNode.Parse("{\"FEATURE\":{\"value\":\"on\"}}"u8);
            envelope["manifest"]!["spec"]!["components"]!["invalid"] = invalid;
        }
        var target = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await importer.ExecuteAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), "tester"));
        if (subMicrosecondCommit) time.Advance(TimeSpan.FromTicks(1));
        var accepted = acceptGeneration is not null ? await acceptGeneration(services, environmentId, target.ManifestRevisionRef) : await new InternalAcceptedGenerationBuilder(services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>(),
            (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>(), services.GetServices<IInternalActionHandler>().ToArray()).AcceptForEffectAsync(environmentId, target.ManifestRevisionRef.ApplicationRecordId,
                target.ManifestRevisionRef, original is null ? new DeploymentPrecondition.ExpectedAbsence() : new DeploymentPrecondition.ExpectedGeneration(original.Generation), "tester", default);
        return new(connection, time, environmentId, accepted, target.ManifestRevisionRef);
    }
    internal static InternalDurableActions Actions(ServiceProvider services, IInternalActionHandler handler, Action<InternalActionFault>? fault = null,
        DurableOperationSerialization? serialization = null) => new(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), [handler],
            services.GetServices<IProviderConfigurationCodec>(), services.GetServices<IExternalOperationOutputContract>(), services.GetRequiredService<DurableActionAdmission>(),
            serialization ?? services.GetRequiredService<DurableOperationSerialization>(), services.GetRequiredService<TimeProvider>(), fault);
    internal static ServiceProvider InternalServices(string connection, TimeProvider time) => Services(connection, time, internalActions: true);
    private static readonly ProviderInput SyntheticSubject = new ProviderInput.Resource(ManifestLocalId.Create("db"));
    private sealed class SyntheticAction : IInternalActionHandler
    {
        public InternalActionContract Contract { get; } = new("postgresql.database/v1alpha1", PostgreSqlProviderConfigurationCodec.Kind,
            PostgreSqlProviderConfigurationCodec.Shape, Implementation, "sample.confirmation/v1", "sample.intent/v1", "sample.receipt/v1");
        public bool ObservationRecovery => true;
        internal int Writes; internal int Reads; internal bool Exists; internal bool Available = true; internal bool LoseResponse;
        public string Prepare(InternalActionContext context) => "{\"marker\":\"" + context.Marker.ToString("N") + "\"}";
        public bool Validate(InternalActionContext context, string payload) => Prepare(context) == payload;
        public bool ValidateReceipt(InternalActionContext context, string payload, string receipt, bool observation) => Validate(context, payload) && receipt == payload;
        public ValueTask<InternalActionResult> ExecuteAsync(InternalActionContext context, string payload, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Writes); Exists = true; return ValueTask.FromResult(new InternalActionResult(LoseResponse ? InternalActionOutcome.Uncertain : InternalActionOutcome.Confirmed, "synthetic-response", LoseResponse ? null : payload)); }
        public ValueTask<InternalActionResult> ObserveAsync(InternalActionContext context, string payload, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Reads); return ValueTask.FromResult(new InternalActionResult(!Available ? InternalActionOutcome.Unavailable : Exists ? InternalActionOutcome.Confirmed : InternalActionOutcome.NotConfirmed, "synthetic-observation", Available && Exists ? payload : null)); }
    }

    [Fact]
    public async Task Internal_action_acceptance_is_immutable_and_public_paths_cannot_dispatch_or_apply_it()
    {
        var scenario = await InternalScenarioAsync(); var handler = new SyntheticAction();
        await using var services = Services(scenario.Connection, scenario.Time, internalActions: true);
        var actions = Actions(services, handler);
        var key = await actions.AcceptAsync(scenario.Commit, SyntheticSubject, handler.Contract.Action, "request", default);
        Assert.Equal(key, await actions.AcceptAsync(scenario.Commit, SyntheticSubject, handler.Contract.Action, "request", default));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await actions.AcceptAsync(scenario.Commit,
            new ProviderInput.Workload(ManifestLocalId.Create("api"), ManifestLocalId.Create("main")), handler.Contract.Action, "request", default));
        Assert.Equal(DurableExecutionOutcome.OperationNotFound, (await services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(key, default)).Outcome);
        Assert.Null(await services.GetRequiredService<IDurableOperationJournal>().ProgressAsync(key.DeploymentId, key.Generation, default));
        Assert.IsType<DurableCommitResult.UnsupportedShape>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(scenario.Commit.ChangeSetId, scenario.Commit.PlanId, "tester", default));
        Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(key, default)).Outcome);
        var before = await actions.HistoryAsync(key, default);
        Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(key, default)).Outcome);
        Assert.Equal(1, handler.Writes); Assert.Equal(0, handler.Reads);
        Assert.Single(before!.Attempts); Assert.Empty(before.Observations);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Equal(2, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).DesiredGeneration);
        Assert.Equal(2, await verify.GenerationStageCheckpoints.CountAsync());
    }
    [Theory]
    [InlineData((int)InternalActionFault.BeforeMarkerCommit, 1, false)]
    [InlineData((int)InternalActionFault.AfterMarkerCommit, 0, true)]
    [InlineData((int)InternalActionFault.AfterExternalResult, 1, true)]
    [InlineData((int)InternalActionFault.BeforeCompletionCommit, 1, true)]
    [InlineData((int)InternalActionFault.AfterCompletionCommit, 1, false)]
    public async Task Restart_retains_barrier_and_receipt_atomicity_without_write_replay(int boundary, int writes, bool uncertain)
    {
        var point = (InternalActionFault)boundary;
        var scenario = await InternalScenarioAsync(); var handler = new SyntheticAction(); DurableOperationKey key;
        await using (var act = Services(scenario.Connection, scenario.Time, internalActions: true))
        {
            var actions = Actions(act, handler, reached => { if (reached == point) throw new IOException("Injected boundary interruption."); });
            key = await actions.AcceptAsync(scenario.Commit, SyntheticSubject, handler.Contract.Action, "interrupted", default);
            await Assert.ThrowsAsync<IOException>(async () => await actions.ExecuteAsync(key, default));
        }
        await using var restarted = Services(scenario.Connection, scenario.Time, internalActions: true);
        var resumed = Actions(restarted, handler);
        var history = (await resumed.HistoryAsync(key, default))!;
        if (point == InternalActionFault.BeforeCompletionCommit) { Assert.Null(history.Output); Assert.Equal("Dispatching", history.Operation.State); }
        var result = await resumed.ExecuteAsync(key, default);
        Assert.Equal(writes, handler.Writes);
        Assert.Equal(writes == 0 ? InternalActionOutcome.NotConfirmed : InternalActionOutcome.Confirmed, result.Outcome);
        history = (await resumed.HistoryAsync(key, default))!;
        if (uncertain)
        {
            Assert.Equal(nameof(DurableAttemptOutcome.Uncertain), Assert.Single(history.Attempts).Outcome);
            Assert.Equal(writes == 1, Assert.Single(history.Observations).Confirmed);
        }
        else Assert.Equal(nameof(DurableAttemptOutcome.Succeeded), history.Attempts.Last().Outcome);
        if (point == InternalActionFault.BeforeMarkerCommit)
            Assert.Equal(["AbandonedBeforeDispatch", "Succeeded"], history.Attempts.Select(x => x.Outcome!).ToArray());
    }

    [Fact]
    public async Task Lost_response_unavailable_observation_then_confirmation_preserves_one_uncertain_attempt_and_original_output()
    {
        var scenario = await InternalScenarioAsync(); var handler = new SyntheticAction { LoseResponse = true };
        DurableOperationKey key;
        await using (var act = Services(scenario.Connection, scenario.Time, internalActions: true))
        {
            var actions = Actions(act, handler);
            key = await actions.AcceptAsync(scenario.Commit, SyntheticSubject, handler.Contract.Action, "lost", default);
            Assert.Equal(InternalActionOutcome.Uncertain, (await actions.ExecuteAsync(key, default)).Outcome);
        }
        await using var restarted = Services(scenario.Connection, scenario.Time, internalActions: true);
        var recovery = Actions(restarted, handler);
        handler.Available = false;
        Assert.Equal(InternalActionOutcome.Unavailable, (await recovery.RecoverByObservationAsync(key, default)).Outcome);
        handler.Available = true;
        var result = await recovery.RecoverByObservationAsync(key, default);
        Assert.Equal(InternalActionOutcome.Confirmed, result.Outcome);
        Assert.Equal(result, await recovery.ExecuteAsync(key, default));
        var history = (await recovery.HistoryAsync(key, default))!;
        Assert.Equal(1, handler.Writes); Assert.Equal(2, handler.Reads);
        Assert.Equal("Uncertain", Assert.Single(history.Attempts).Outcome);
        Assert.Equal([false, true], history.Observations.Select(x => x.Confirmed).ToArray());
        Assert.Equal(result.Receipt, history.Output!.Payload);
    }

    [Fact]
    public async Task Concurrent_internal_executors_share_one_dispatch_and_terminal_result()
    {
        var scenario = await InternalScenarioAsync(); var handler = new SyntheticAction();
        await using var services = Services(scenario.Connection, scenario.Time, internalActions: true);
        var actions = Actions(services, handler);
        var keys = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => actions.AcceptAsync(scenario.Commit, SyntheticSubject, handler.Contract.Action, "concurrent", default).AsTask()));
        Assert.Single(keys.Distinct());
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => actions.ExecuteAsync(keys[0], default).AsTask()));
        Assert.All(results, x => Assert.Equal(InternalActionOutcome.Confirmed, x.Outcome)); Assert.Equal(1, handler.Writes);
        Assert.Single((await actions.HistoryAsync(keys[0], default))!.Attempts);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("checkpoint")]
    [InlineData("provider")]
    [InlineData("handler")]
    public async Task Corrupt_or_missing_exact_authority_refuses_without_attempt_or_external_call(string corruption)
    {
        var scenario = await InternalScenarioAsync(); var handler = new SyntheticAction();
        await using var services = Services(scenario.Connection, scenario.Time, internalActions: true);
        var actions = Actions(services, handler);
        var key = await actions.AcceptAsync(scenario.Commit, SyntheticSubject, handler.Contract.Action, "corrupt", default);
        await using (var arrange = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            if (corruption == "payload") await arrange.ActionIntents.ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{}"));
            if (corruption == "checkpoint")
            {
                var foreign = (await arrange.GenerationStageCheckpoints.AsNoTracking().SingleAsync(x => x.Generation == 1)).Id;
                await arrange.ActionIntents.ExecuteUpdateAsync(s => s.SetProperty(x => x.CheckpointId, foreign));
            }
            if (corruption == "provider") await arrange.ActionIntents.ExecuteUpdateAsync(s => s.SetProperty(x => x.ConfigurationRevision, 2));
        }
        if (corruption == "handler") actions = new(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), [],
            services.GetServices<IProviderConfigurationCodec>(), services.GetServices<IExternalOperationOutputContract>(), services.GetRequiredService<DurableActionAdmission>(),
            services.GetRequiredService<DurableOperationSerialization>(), scenario.Time);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await actions.ExecuteAsync(key, default));
        var retained = (await actions.HistoryAsync(key, default))!;
        Assert.Equal("Pending", retained.Operation.State); Assert.Empty(retained.Attempts); Assert.Empty(retained.Observations);
        Assert.Equal(0, handler.Writes); Assert.Equal(0, handler.Reads);
    }

    [Fact]
    public async Task Durable_update_has_no_admission_without_genuine_prior_workload_authority()
    {
        var scenario = await InternalScenarioAsync();
        await using var services = Services(scenario.Connection, scenario.Time, internalActions: true);
        var handler = new InternalDeploymentAction(services.GetRequiredService<ISecretStore>());
        var actions = Actions(services, handler);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await actions.AcceptAsync(scenario.Commit,
            new ProviderInput.Workload(ManifestLocalId.Create("api"), ManifestLocalId.Create("main")), "haby.kubernetes.deployment-update/v1", "update", default));
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verify.ActionIntents.ToArrayAsync()); Assert.False(await verify.GenerationStageCheckpoints.AnyAsync(x => x.Generation == 2));
    }

    [Fact]
    public async Task Current_build_restore_retains_uncertainty_marker_inputs_and_observation_history_without_replay()
    {
        var scenario = await InternalScenarioAsync(); var handler = new SyntheticAction { LoseResponse = true, Available = false };
        DurableOperationKey key; Guid marker;
        await using (var act = Services(scenario.Connection, scenario.Time, internalActions: true))
        {
            var actions = Actions(act, handler);
            key = await actions.AcceptAsync(scenario.Commit, SyntheticSubject, handler.Contract.Action, "restore", default);
            Assert.Equal(InternalActionOutcome.Uncertain, (await actions.ExecuteAsync(key, default)).Outcome);
            Assert.Equal(InternalActionOutcome.Unavailable, (await actions.RecoverByObservationAsync(key, default)).Outcome);
            marker = (await actions.HistoryAsync(key, default))!.Intent.Marker;
        }
        var restored = await NewDatabaseAsync();
        var directory = "/tmp/haby202-restore-" + Guid.NewGuid().ToString("N"); var dump = directory + "/catalog.dump";
        try
        {
            await fixture.RunClientUtilityAsync("mkdir", "-m", "700", directory);
            await fixture.RunClientUtilityAsync("pg_dump", "-U", "postgres", "--format=custom", "--file=" + dump, "--dbname=" + new Npgsql.NpgsqlConnectionStringBuilder(scenario.Connection).Database);
            await fixture.RunClientUtilityAsync("pg_restore", "-U", "postgres", "--exit-on-error", "--no-owner", "--no-privileges", "--dbname=" + new Npgsql.NpgsqlConnectionStringBuilder(restored).Database, dump);
            await using var restarted = Services(restored, scenario.Time, internalActions: true);
            await restarted.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            var actions = Actions(restarted, handler); handler.Available = true;
            Assert.Equal(InternalActionOutcome.Confirmed, (await actions.RecoverByObservationAsync(key, default)).Outcome);
            var history = (await actions.HistoryAsync(key, default))!;
            Assert.Equal(marker, history.Intent.Marker); Assert.Equal("Uncertain", Assert.Single(history.Attempts).Outcome);
            Assert.Equal([false, true], history.Observations.Select(x => x.Confirmed).ToArray()); Assert.Equal(1, handler.Writes);
        }
        finally
        {
            await fixture.RunClientUtilityAsync("rm", "-f", dump);
            await fixture.RunClientUtilityAsync("rmdir", directory);
        }
    }

    [Fact]
    public async Task First_internal_submicrosecond_commit_receipt_accepts_action_and_matches_persisted_replay()
    {
        var scenario = await InternalScenarioAsync(subMicrosecondCommit: true);
        await using var services = Services(scenario.Connection, scenario.Time, internalActions: true);
        var handler = new SyntheticAction();
        var key = await Actions(services, handler).AcceptAsync(scenario.Commit, SyntheticSubject, handler.Contract.Action, "precision", default);
        var committer = (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>();
        var replay = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitInternalAsync(scenario.Commit.ChangeSetId, scenario.Commit.PlanId, "replay", default)).Commit;
        Assert.Equal(scenario.Commit, replay); Assert.Equal(0, scenario.Commit.CommittedAt.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.NotNull(await Actions(services, handler).HistoryAsync(key, default));
    }

    [Fact]
    public async Task First_public_submicrosecond_commit_receipt_matches_exact_persisted_replay()
    {
        var setup = await PrepareCommitAsync(); setup.Time.Advance(TimeSpan.FromTicks(1));
        await using var services = Services(setup.Connection, setup.Time);
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(setup.Environment, setup.Revision.ApplicationRecordId, setup.Revision,
            new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
        var plan = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
        var committer = services.GetRequiredService<IDurableDesiredStateCommitter>();
        var first = Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(change.Id, plan.Id, "tester", default)).Commit;
        Assert.Equal(first, await committer.FindCommitAsync(change.Id, default));
        Assert.Equal(first, Assert.IsType<DurableCommitResult.Committed>(await committer.CommitAsync(change.Id, plan.Id, "replay", default)).Commit);
        Assert.Equal(0, first.CommittedAt.Ticks % TimeSpan.TicksPerMicrosecond);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_components_accept_their_frozen_placements_after_current_selections_change(bool distinct)
    {
        var scenario = await InternalScenarioAsync(twoComponents: true, distinctPlacements: distinct);
        await using var services = Services(scenario.Connection, scenario.Time, internalActions: true);
        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(); var codec = new KubernetesPlacementCodec();
        var alias = ProviderAlias.Create("kubernetes.primary");
        var later = codec.Decode("""{"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"later","revision":1},"spec":{"namespace":"later-target"}}"""u8.ToArray());
        var reference = new PlacementRevisionKey(scenario.Environment, alias, later.Name, later.Revision);
        await new PostgreSqlPlacementRevisionStore(factory, codec).CompareAndCreateAsync(reference, later, new("later", scenario.Time.GetUtcNow()), default);
        var selections = new PostgreSqlPlacementSelectionStore(factory, codec);
        foreach (var target in distinct ? new[] { PlacementSelectionTarget.Create("environment", "", "", ""), PlacementSelectionTarget.Create("named", "worker-selected", "", "") } :
            new[] { PlacementSelectionTarget.Create("environment", "", "", "") })
        {
            var previous = (await selections.FindAsync(scenario.Environment, alias, target, default))!;
            Assert.Equal(PlacementSelectionOutcome.Updated, (await selections.ApplyAsync(target, reference,
                new PlacementSelectionPrecondition.ExpectedToken(previous.Token), new("later", scenario.Time.GetUtcNow(), null), default)).Outcome);
        }
        var handler = new InternalDeploymentAction(services.GetRequiredService<ISecretStore>()); var actions = Actions(services, handler);
        foreach (var component in new[] { "api", "worker" })
        {
            var key = await actions.AcceptAsync(scenario.Commit, new ProviderInput.Workload(ManifestLocalId.Create(component), ManifestLocalId.Create("main")),
                InternalDeploymentAction.Create, component + "-create", default);
            var history = (await actions.HistoryAsync(key, default))!; var intent = DeploymentIntents.Decode(history.Intent.Payload);
            Assert.Equal(component == "worker" && distinct ? "test-worker" : "test", intent.Namespace);
            Assert.Equal(component == "worker" && distinct ? "worker-placement" : "standard", intent.Placement);
            Assert.Equal(component, intent.Component); Assert.Empty(history.Attempts);
        }
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.ActionIntents.CountAsync()); Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Fact]
    public async Task Typed_internal_fixture_preflight_keeps_real_PostgreSql_checks_and_refuses_bad_Kubernetes_credentials_before_alias_write()
    {
        var setup = await PrepareCommitAsync(); await using var services = Services(setup.Connection, setup.Time, internalActions: true);
        var preflight = services.GetRequiredService<IProviderRevisionPreflight>();
        Assert.Equal(ProviderPreflightOutcome.Ready, (await preflight.CheckAsync(setup.Environment, setup.First, default)).Outcome);
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        await services.GetRequiredService<ISecretStore>().CreateAsync(new(new SecretOwner.Environment(setup.Environment), SecretPurpose.ProviderAdministrator), secret, "wrong-fixture-token"u8.ToArray(), default);
        var configuration = new KubernetesProviderConfiguration(new Uri("https://127.0.0.1:6443"), null, secret, null, null);
        var providers = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await providers.CreateInstanceAsync(setup.Environment, configuration, default);
        await providers.AddRevisionAsync(setup.Environment, instance, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
        var binding = new ProviderAliasBinding(instance, ProviderConfigurationRevisionNumber.Create(1));
        Assert.Equal(ProviderPreflightOutcome.AuthenticationFailed, (await preflight.CheckAsync(setup.Environment, binding, default)).Outcome);
        var alias = ProviderAlias.Create("kubernetes.primary"); var store = services.GetRequiredService<IProviderAliasStore>();
        var refused = await store.ChangeAsync(setup.Environment, alias, null, binding, "tester", default);
        Assert.Equal(ProviderAliasChangeOutcome.PreflightFailed, refused.Outcome);
        Assert.Equal(ProviderPreflightOutcome.AuthenticationFailed, refused.Preflight!.Value.Outcome);
        Assert.Null(await store.ResolveAsync(setup.Environment, alias, default));
    }
}
