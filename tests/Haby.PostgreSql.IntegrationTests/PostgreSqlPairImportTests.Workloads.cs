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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlPairImportTests
{
    [Fact]
    public async Task Imported_pair_supports_standard_internal_workload_deployment_and_image_update_without_new_resource_effects()
    {
        var pair = await ExistingPair();
        var setup = await AcceptedPair(pair);
        await using var services = WorkloadServices(setup.Connection);
        await ArrangeWorkloadProvider(services, setup.Owner.EnvironmentRecordId);
        var envelope = Declaration(setup.Plan.Inputs.Providers.Declaration.ApplicationId.Value);
        envelope["manifest"]!["spec"]!["components"]!["worker"]!["workloads"] = JsonNode.Parse("""
            {"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary",
              "properties":{"image":{"reference":"registry.example/app:v1"},"configuration":{"documents":["settings"]}}}}
            """u8);
        var accepted = await AcceptWorkload(services, setup, envelope, 1);
        var fixture = DurableDeploymentPlanningTests.ImportedWorkloadHandlers();
        var configuration = DurableDeploymentPlanningTests.ConfigurationPreparation(services, rendering: PostgreSqlConnectionRendering.CreateCatalog(),
            formatters: PostgreSqlConnectionRendering.FormatterCatalog()).Preparation;
        var actions = WorkloadActions(services, fixture, configuration);
        await using (var missing = WorkloadServices(setup.Connection, includeVerifiers: false))
            await Assert.ThrowsAsync<InvalidDataException>(() => WorkloadConfiguration(missing).PrepareAsync(accepted, "tester", default).AsTask());
        await PrepareWorkload(services, configuration, actions, fixture, accepted);
        var subject = new ProviderInput.Workload(ManifestLocalId.Create("worker"), ManifestLocalId.Create("main"));
        var create = await actions.AcceptAsync(accepted, subject, fixture.Create.Contract.Action, "create-worker", default);
        Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(create, default)).Outcome);
        var clock = services.GetRequiredService<TimeProvider>();
        var options = new RolloutOptions(MaximumRounds: 1);
        var ready = await actions.ObserveOperatorRolloutAsync(accepted, "initial-ready", "tester", options,
            clock.GetTimestamp(), TimeSpan.FromSeconds(10), default, default);
        Assert.True(ready.Applied);
        Assert.Equal(1, fixture.CreateWrites());
        envelope["manifest"]!["spec"]!["components"]!["worker"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:v2";
        var update = await AcceptWorkload(services, setup, envelope, 2);
        await PrepareWorkload(services, configuration, actions, fixture, update);
        var key = await actions.AcceptAsync(update, subject, fixture.Update.Contract.Action, "update-worker", default);
        Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(key, default)).Outcome);
        var completed = await actions.WaitUpdateAsync(update, "update-ready", "tester", options, new(), default);
        Assert.True(completed.Applied);
        Assert.Equal(3, completed.AppliedGeneration);
        Assert.Equal(1, fixture.UpdateWrites());
        await OwnedPassword(pair, "out-of-band-after-completion");
        var databaseProbe = new UnexpectedHistoricalReuse(PostgreSqlImportContracts.Database.Admission);
        var loginProbe = new UnexpectedHistoricalReuse(PostgreSqlImportContracts.Login.Admission);
        await using var historical = WorkloadServices(setup.Connection, configure: s =>
        {
            s.RemoveAll<IDurableResourceReuseVerifier>();
            s.AddSingleton<IDurableResourceReuseVerifier>(databaseProbe);
            s.AddSingleton<IDurableResourceReuseVerifier>(loginProbe);
        });
        var history = WorkloadActions(historical, fixture, WorkloadConfiguration(historical));
        Assert.True((await history.WaitUpdateAsync(update, "historical-new-observation", "tester", options, new(), default)).Applied);
        Assert.Equal(0, databaseProbe.Calls + loginProbe.Calls);
        await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Single(await db.ResourceImportReceipts.AsNoTracking().ToArrayAsync());
        Assert.Equal(2, await db.DurableOperationAttempts.CountAsync(x => x.Generation == 1));
        Assert.Empty(await db.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Imported_pair_drift_blocks_workload_dispatch_and_initial_applied_after_persisted_resource_readiness()
    {
        var pair = await ExistingPair();
        var setup = await AcceptedPair(pair);
        await using var services = WorkloadServices(setup.Connection);
        await ArrangeWorkloadProvider(services, setup.Owner.EnvironmentRecordId);
        var envelope = WorkloadDeclaration(setup);
        var accepted = await AcceptWorkload(services, setup, envelope, 1);
        var fixture = DurableDeploymentPlanningTests.ImportedWorkloadHandlers();
        var configuration = WorkloadConfiguration(services);
        var actions = WorkloadActions(services, fixture, configuration);
        await PrepareWorkload(services, configuration, actions, fixture, accepted);
        var subject = new ProviderInput.Workload(ManifestLocalId.Create("worker"), ManifestLocalId.Create("main"));
        var key = await actions.AcceptAsync(accepted, subject, fixture.Create.Contract.Action, "create-worker", default);
        using (var cancellation = new CancellationTokenSource())
        {
            await using var canceled = WorkloadServices(setup.Connection, new CancelReuse(cancellation));
            var canceledActions = WorkloadActions(canceled, fixture, WorkloadConfiguration(canceled));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledActions.ExecuteAsync(key, cancellation.Token).AsTask());
        }
        await OwnedPassword(pair, "changed-owned-password");
        var refused = await actions.ExecuteAsync(key, default);
        Assert.Equal(InternalActionOutcome.Refused, refused.Outcome);
        Assert.Equal("resource-reuse-conflict", refused.Code);
        Assert.Equal(0, fixture.CreateWrites());
        await using (var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(1, (await db.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Empty(await db.DurableOperationAttempts.AsNoTracking().Where(x => x.OperationId == key.OperationId.Value).ToArrayAsync());
            Assert.True(await db.GenerationStageCheckpoints.AnyAsync(x => x.Generation == 2 && x.Stage == "ResourcesReady"));
        }
        await OwnedPassword(pair, pair.Password);
        Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(key, default)).Outcome);
        await OwnedPassword(pair, "changed-before-applied");
        var options = new RolloutOptions(MaximumRounds: 1);
        var clock = services.GetRequiredService<TimeProvider>();
        await Assert.ThrowsAsync<InvalidDataException>(() => actions.ObserveOperatorRolloutAsync(accepted, "refuse-applied", "tester", options,
            clock.GetTimestamp(), TimeSpan.FromSeconds(30), default, default).AsTask());
        await using (var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            Assert.Equal(1, (await db.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        await OwnedPassword(pair, pair.Password);
        var ready = await actions.ObserveOperatorRolloutAsync(accepted, "fresh-applied", "tester", options,
            clock.GetTimestamp(), TimeSpan.FromSeconds(30), default, default);
        Assert.True(ready.Applied);
        Assert.Equal(1, fixture.CreateWrites());
    }

    [Fact]
    public async Task Imported_pair_drift_before_update_applied_rechecks_zero_resource_effect_completion_after_restart()
    {
        var pair = await ExistingPair();
        var setup = await AcceptedPair(pair);
        var fixture = DurableDeploymentPlanningTests.ImportedWorkloadHandlers();
        DurableChangeSetCommit update;
        await using (var services = WorkloadServices(setup.Connection))
        {
            await ArrangeWorkloadProvider(services, setup.Owner.EnvironmentRecordId);
            var envelope = WorkloadDeclaration(setup);
            var accepted = await AcceptWorkload(services, setup, envelope, 1);
            var configuration = WorkloadConfiguration(services);
            var actions = WorkloadActions(services, fixture, configuration);
            await PrepareWorkload(services, configuration, actions, fixture, accepted);
            var subject = new ProviderInput.Workload(ManifestLocalId.Create("worker"), ManifestLocalId.Create("main"));
            var key = await actions.AcceptAsync(accepted, subject, fixture.Create.Contract.Action, "create-worker", default);
            Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(key, default)).Outcome);
            var clock = services.GetRequiredService<TimeProvider>();
            Assert.True((await actions.ObserveOperatorRolloutAsync(accepted, "initial-ready", "tester", new(MaximumRounds: 1),
                clock.GetTimestamp(), TimeSpan.FromSeconds(30), default, default)).Applied);
            envelope["manifest"]!["spec"]!["components"]!["worker"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:v2";
            update = await AcceptWorkload(services, setup, envelope, 2);
            await PrepareWorkload(services, configuration, actions, fixture, update);
            var updateKey = await actions.AcceptAsync(update, subject, fixture.Update.Contract.Action, "update-worker", default);
            Assert.Equal(InternalActionOutcome.Confirmed, (await actions.ExecuteAsync(updateKey, default)).Outcome);
            var injected = false;
            var completion = WorkloadActions(services, fixture, configuration, point =>
            {
                if (point != InternalActionFault.BeforeUpdateApplied || injected) return;
                injected = true;
                OwnedPassword(pair, "changed-at-update-cas").GetAwaiter().GetResult();
            });
            Assert.False((await completion.WaitUpdateAsync(update, "refuse-update-applied", "tester", new(MaximumRounds: 1), new(), default)).Applied);
            Assert.True(injected);
            await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.Equal(2, (await db.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Equal(2, await db.DurableOperationAttempts.CountAsync(x => x.Generation == 1));
        }
        await OwnedPassword(pair, pair.Password);
        var observed = new CountReuse();
        await using var restart = WorkloadServices(setup.Connection, observed);
        var retry = WorkloadActions(restart, fixture, WorkloadConfiguration(restart));
        var result = await retry.WaitUpdateAsync(update, "retry-update-after-restart", "tester", new(MaximumRounds: 1), new(), default);
        Assert.True(result.Applied);
        Assert.Equal(3, result.AppliedGeneration);
        Assert.True(observed.Count > 0);
        Assert.Equal(1, fixture.UpdateWrites());
    }

    private sealed class UnexpectedHistoricalReuse(DurableActionAdmissionKey key) : IDurableResourceReuseVerifier
    {
        public DurableActionAdmissionKey Key => key;
        public int Calls;
        public ValueTask<ResourceReuseValidationStatus> VerifyAsync(AcceptedOperation original, ResourceImportReceipt receipt, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("Historical observation must not invoke import verification.");
        }
    }

    private Task OwnedPassword(Pair pair, string value) => Sql(Control(),
        $"ALTER ROLE \"{pair.Login}\" PASSWORD '{value.Replace("'", "''", StringComparison.Ordinal)}'");

    private static JsonObject WorkloadDeclaration(Setup setup)
    {
        var envelope = Declaration(setup.Plan.Inputs.Providers.Declaration.ApplicationId.Value);
        envelope["manifest"]!["spec"]!["components"]!["worker"]!["workloads"] = JsonNode.Parse("""
            {"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary",
              "properties":{"image":{"reference":"registry.example/app:v1"},"configuration":{"documents":["settings"]}}}}
            """u8);
        return envelope;
    }
    private static InternalWorkloadConfiguration WorkloadConfiguration(ServiceProvider services) =>
        DurableDeploymentPlanningTests.ConfigurationPreparation(services, rendering: PostgreSqlConnectionRendering.CreateCatalog(),
            formatters: PostgreSqlConnectionRendering.FormatterCatalog()).Preparation;

    private static ServiceProvider WorkloadServices(string connection, IPostgreSqlImportFaultHooks? hooks = null, bool includeVerifiers = true, Action<IServiceCollection>? configure = null) => Services(connection, hooks, configure: s =>
    {
        s.AddSingleton<KubernetesProviderConfigurationCodec>();
        s.AddSingleton<IProviderConfigurationCodec>(p => p.GetRequiredService<KubernetesProviderConfigurationCodec>());
        s.AddSingleton<IInternalActionHandler>(p => new InternalDeploymentAction(p.GetRequiredService<ISecretStore>()));
        s.AddSingleton<IInternalActionHandler>(p => new InternalDeploymentAction(p.GetRequiredService<ISecretStore>(), update: true));
        s.AddSingleton<IProviderPreflightHandler, PostgreSqlPreflightHandler>();
        if (!includeVerifiers) s.RemoveAll<IDurableResourceReuseVerifier>();
        s.AddInternalKubernetesFixturePreflight(p => new InternalKubernetesFixturePreflight(
            p.GetRequiredService<KubernetesProviderConfigurationCodec>(), p.GetRequiredService<ISecretStore>(), InternalKubernetesFixturePreflight.DeterministicProbe));
        configure?.Invoke(s);
    });

    private static async Task ArrangeWorkloadProvider(ServiceProvider services, EnvironmentRecordId environment)
    {
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        await services.GetRequiredService<ISecretStore>().CreateAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator), secret, "fixture-token"u8.ToArray(), default);
        var configuration = new KubernetesProviderConfiguration(new Uri("https://127.0.0.1:6443"), null, secret, null, null);
        var providers = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await providers.CreateInstanceAsync(environment, configuration, default);
        await providers.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
        var alias = ProviderAlias.Create("kubernetes.primary");
        Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment, alias, null,
            new(instance, ProviderConfigurationRevisionNumber.Create(1)), "tester", default)).Outcome);
        var codec = new KubernetesPlacementCodec();
        var placement = codec.Decode("""
            {"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"standard","revision":1},
             "spec":{"namespace":"test","runtimeConfiguration":{"endpoint":"https://configuration.example/","tls":{"mode":"system"}}}}
            """u8.ToArray());
        var key = new PlacementRevisionKey(environment, alias, placement.Name, placement.Revision);
        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        await new PostgreSqlPlacementRevisionStore(factory, codec).CompareAndCreateAsync(key, placement, new("tester", TimeProvider.System.GetUtcNow()), default);
        await new PostgreSqlPlacementSelectionStore(factory, codec).ApplyAsync(PlacementSelectionTarget.Create("environment", "", "", ""), key,
            new PlacementSelectionPrecondition.ExpectedAbsence(), new("tester", TimeProvider.System.GetUtcNow(), null), default);
    }

    private static async Task<DurableChangeSetCommit> AcceptWorkload(ServiceProvider services, Setup setup, JsonObject envelope, long previous)
    {
        envelope["applicationVersion"] = "workload-" + (previous + 1);
        var catalog = new ManifestContractCatalog([PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition],
            [KubernetesContract.Definition], [], [new(PostgreSqlConnectionRendering.Renderer)]);
        var revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(), catalog, TimeProvider.System)
            .ExecuteAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), "tester")).ManifestRevisionRef;
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(setup.Owner.EnvironmentRecordId, revision.ApplicationRecordId, revision,
            new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(previous)), "tester", default)).ChangeSet!;
        var handlers = services.GetServices<IInternalActionHandler>().ToArray();
        var plan = await planner.CreateInternalPlanAsync(change.Id, "tester", default, handlers);
        Assert.Equal(DurablePlanStatus.Planned, plan.Status);
        Assert.Empty(plan.Plan!.Graph.Nodes);
        Assert.Equal(2, plan.Plan.Graph.ReusedResources.Count);
        return Assert.IsType<DurableCommitResult.Committed>(await ((PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>())
            .CommitInternalAsync(change.Id, plan.Plan.Id, "tester", default, handlers)).Commit;
    }

    private static InternalDurableActions WorkloadActions(ServiceProvider services, DurableDeploymentPlanningTests.ImportedWorkloadFixture fixture,
        InternalWorkloadConfiguration configuration, Action<InternalActionFault>? fault = null) => new(
            services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), [fixture.Create, fixture.Identity, fixture.Update],
            services.GetServices<IProviderConfigurationCodec>(), services.GetServices<IExternalOperationOutputContract>(), services.GetRequiredService<DurableActionAdmission>(),
            services.GetRequiredService<DurableOperationSerialization>(), services.GetRequiredService<TimeProvider>(), fault, configuration, reuseVerifiers: services.GetServices<IDurableResourceReuseVerifier>());

    private static async Task PrepareWorkload(ServiceProvider services, InternalWorkloadConfiguration configuration, InternalDurableActions actions,
        DurableDeploymentPlanningTests.ImportedWorkloadFixture fixture, DurableChangeSetCommit accepted)
    {
        Assert.Null((await configuration.PrepareAsync(accepted, "tester", default)).Failure);
        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var identity = await new InternalRuntimeIdentityPreparation(factory, configuration, actions, fixture.Identity.Contract.Action).PrepareAsync(accepted, default);
        Assert.All(identity, x => Assert.Equal(InternalActionOutcome.Confirmed, x.Result.Outcome));
        Assert.Null((await new InternalRuntimePublicationPreparation(new(factory, configuration, actions, services.GetRequiredService<TimeProvider>()))
            .PrepareAsync(accepted, "tester", default)).Reason);
    }
}
