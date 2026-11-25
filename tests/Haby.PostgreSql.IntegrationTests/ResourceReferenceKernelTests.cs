using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

/// <summary>Real catalog/dispatch semantics with a closed test-only observational adapter. No production Provider claim.</summary>
[Collection("Catalog database group")]
public sealed partial class ResourceReferenceKernelTests(CatalogServerFixture fixture) : CatalogTestCase(fixture)
{
    private static readonly ProviderAdapterKey Provider = new("sample/v1", "sample.configuration/v1", "sample.impl/v1");
    private static readonly ResourceTypeId Type = ResourceTypeId.Create("test.capability/v1");
    private static readonly ExternalOperationOutputIdentity Output = new("test.capability-output/v1", "access");
    private static readonly ManifestLocalId Imported = ManifestLocalId.Create("imported");
    private static readonly ManifestLocalId Referenced = ManifestLocalId.Create("referenced");
    private static DurableActionExecutionKey Key(ResourceAcquisitionMode mode) => new(new(Type, Provider) { Mode = mode }, OperationContractId.Create("test.verify/v1"));
    public sealed record CapabilityInput(string Identity, string? SharingClass = null);
    private sealed class Boundary
    {
        public int Observations;
        public int HandlerCalls;
        public bool SecondMemberDependsOnImport;
        public CancellationTokenSource? CancelBeforeAcceptance;
    }
    private sealed record Prepared(DurablePlan Plan, DurableChangeSetCommit Commit, EnvironmentRecordId Environment, ProviderInstanceId Provider);
    private static DurableActionPayload Payload(string identity, string? sharingClass = null) => new("test.capability-input/v1", JsonSerializer.Serialize(new CapabilityInput(identity, sharingClass), CapabilityJsonContext.Default.CapabilityInput));
    private static CapabilityInput? Decode(DurableActionPayload input)
    {
        if (input.Format != "test.capability-input/v1") return null;
        try
        {
            var value = JsonSerializer.Deserialize(input.Json, CapabilityJsonContext.Default.CapabilityInput);
            return value?.Identity is { Length: > 0 and <= 128 } ? value : null;
        }
        catch (JsonException) { return null; }
    }
    private static ServiceProvider Services(string connection, Boundary boundary, bool referenceExecutor = true, bool referenceEvidence = true, IInterceptor? interceptor = null, bool observationCapability = true, bool referenceIdentityCapability = true, bool sharingCapability = true, string sharingContract = "test.reference-sharing/v1", bool sharingAvailable = true)
    {
        var services = new ServiceCollection().AddSingleton<TimeProvider>(new Clock()).AddSingleton(ConfigurationRenderingCatalog.Empty)
            .AddPostgreSqlManifestCatalog(connection).AddPostgreSqlPlaintextSecretStore().AddPostgreSqlProviderConfigurationStore()
            .AddSingleton<IProviderConfigurationCodec, SampleConfigurationCodec>().AddPostgreSqlProviderAliasStore()
            .AddSingleton<IProviderRevisionPreflight, Ready>().AddPostgreSqlDurableDesiredStateCommitter().AddPostgreSqlDurableOperationJournal()
            .AddPostgreSqlResourceObservationAcceptance().AddPostgreSqlProviderResourceReservations()
            .AddSingleton<DurableOperationSerialization>().AddSingleton<ExactDurableOperationExecutor>()
            .AddSingleton<IDurableOperationExecutor>(p => p.GetRequiredService<ExactDurableOperationExecutor>()).AddSingleton<DurableGenerationRunner>();
        if (interceptor is not null) services.AddDbContextFactory<ManifestCatalogDbContext>(o => o.AddInterceptors(interceptor));
        foreach (var mode in new[] { ResourceAcquisitionMode.ImportUseAndRetain, ResourceAcquisitionMode.Reference })
        {
            services.AddSingleton<IDurableActionAdmissionHandler>(p => new Handler(mode, p.GetRequiredService<IDurableOperationJournal>(),
                p.GetRequiredService<IExternalOperationOutputs>(), p.GetRequiredService<IResourceObservationAcceptance>(), p.GetRequiredService<IProviderConfigurationStore>(), boundary));
            if (mode != ResourceAcquisitionMode.Reference || referenceExecutor)
                services.AddSingleton<IDurableActionHandler>(p => new Handler(mode, p.GetRequiredService<IDurableOperationJournal>(),
                    p.GetRequiredService<IExternalOperationOutputs>(), p.GetRequiredService<IResourceObservationAcceptance>(), p.GetRequiredService<IProviderConfigurationStore>(), boundary));
            if (mode != ResourceAcquisitionMode.Reference || referenceEvidence)
                services.AddSingleton<IExternalOperationOutputContract>(mode == ResourceAcquisitionMode.Reference && !observationCapability
                    ? new OutputOnlyEvidence(mode) : mode == ResourceAcquisitionMode.Reference && !referenceIdentityCapability
                        ? new ObservationOnlyEvidence(mode) : mode == ResourceAcquisitionMode.Reference && !sharingCapability
                            ? new ReferenceOnlyEvidence(mode) : new Evidence(mode, sharingContract, sharingAvailable));
        }
        services.AddSingleton<IDurableResourceReuseVerifier>(new CapabilityReuseVerifier(ResourceAcquisitionMode.ImportUseAndRetain));
        services.AddSingleton<IDurableResourceReuseVerifier>(new CapabilityReuseVerifier(ResourceAcquisitionMode.Reference));
        return services.BuildServiceProvider();
    }
    private sealed class CapabilityReuseVerifier(ResourceAcquisitionMode mode) : IDurableResourceReuseVerifier
    {
        public DurableActionAdmissionKey Key => ResourceReferenceKernelTests.Key(mode).Admission;
        public ValueTask<ResourceReuseValidationStatus> VerifyAsync(AcceptedOperation original, ResourceObservationReceipt receipt, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new Evidence(mode).ValidateObservationEvidence(original, receipt.Evidence)
                ? ResourceReuseValidationStatus.Confirmed : ResourceReuseValidationStatus.InputUnavailable);
        }
    }
    private static async Task<Prepared> PrepareAsync(string connection, Boundary boundary, string application = "reference-app",
        Prepared? scope = null, bool mixed = true, string identity = "shared-service", ResourceAcquisitionMode singleMode = ResourceAcquisitionMode.Reference, string? sharingClass = null, bool secondReference = false, bool newProvider = false, string? components = null)
    {
        await using var services = Services(connection, boundary);
        if (scope is null) await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var environment = scope?.Environment ?? await services.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var provider = newProvider ? null : scope?.Provider;
        if (provider is null)
        {
            var configurations = services.GetRequiredService<IProviderConfigurationStore>();
            var config = new SampleConfiguration("verified-test-service", "sample.impl/v1");
            provider = await configurations.CreateInstanceAsync(environment, config, default);
            await configurations.AddRevisionAsync(environment, provider, ProviderConfigurationRevisionNumber.Create(1), config, default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment,
                ProviderAlias.Create("sample.primary"), newProvider && scope is not null ? new(scope.Provider, scope.Plan.Inputs.Providers.Resource(Referenced).Configuration) : null,
                new(provider, ProviderConfigurationRevisionNumber.Create(1)), "tester", default)).Outcome);
        }
        var resources = mixed ? "\"imported\":{\"type\":\"test.capability/v1\",\"providerRef\":\"sample.primary\",\"properties\":{}}," : "";
        if (secondReference) resources += "\"shared-peer\":{\"type\":\"test.capability/v1\",\"providerRef\":\"sample.primary\",\"properties\":{}},";
        var bytes = Encoding.UTF8.GetBytes("{\"applicationVersion\":\"v1\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"" + application + "\"},\"spec\":{\"resources\":{" + resources + "\"referenced\":{\"type\":\"test.capability/v1\",\"providerRef\":\"sample.primary\",\"properties\":{}}}" + (components is null ? "" : ",\"components\":" + components) + "}}}");
        var revision = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(),
            new([new(Type, "{\"type\":\"object\",\"additionalProperties\":false}")], [], [], []), TimeProvider.System).ExecuteAsync(bytes, "tester")).ManifestRevisionRef;
        var requests = new List<ResourceAcquisitionRequest> { new(Referenced, singleMode, Payload(identity, sharingClass)) { SharingRequest = sharingClass is null ? null : SharingRequest(sharingClass) } };
        if (secondReference) requests.Add(new(ManifestLocalId.Create("shared-peer"), ResourceAcquisitionMode.Reference, Payload(identity, sharingClass)) { SharingRequest = sharingClass is null ? null : SharingRequest(sharingClass) });
        if (mixed) requests.Add(new(Imported, ResourceAcquisitionMode.ImportUseAndRetain, Payload("imported-service")));
        var planner = Assert.IsType<PostgreSqlDurableDeploymentPlanner>(services.GetRequiredService<IDurableDeploymentPlanner>());
        var ordinary = (await planner.CreateChangeSetAsync(environment, revision.ApplicationRecordId, revision, new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
        Assert.Equal(DurablePlanStatus.ProviderCapabilityMismatch, (await planner.CreatePlanAsync(ordinary.Id, "tester", default)).Status);
        var change = (await planner.CreateAcquisitionChangeSetAsync(environment, revision.ApplicationRecordId, revision, new(requests), "tester", default)).ChangeSet!;
        var result = await planner.CreatePlanAsync(change.Id, "tester", default);
        Assert.Equal(DurablePlanStatus.Planned, result.Status);
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(result.Plan!.Id, default));
        var commit = Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, result.Plan.Id, "tester", default)).Commit;
        return new(result.Plan, commit, environment, provider);
    }
    private static DurableOperationKey Operation(Prepared item, string id) => new(item.Commit.DeploymentId, item.Commit.Generation, OperationId.Create(id));
    private static ProviderResourceReservation Identity(Prepared item, ManifestLocalId resource, string identity) => new(item.Provider, item.Environment,
        item.Plan.Inputs.Revision.ApplicationRecordId, resource, "test.service", identity);
    private static async Task<ResourceObservationAcceptance> StageAsync(ServiceProvider services, Prepared item, ManifestLocalId resource)
    {
        var journal = services.GetRequiredService<IDurableOperationJournal>();
        var members = new List<ObservationOperationConfirmation>();
        foreach (var node in item.Plan.Graph.Nodes.Where(x => x.ResourceId == resource))
        {
            var key = Operation(item, node.Id.Value);
            var current = (await journal.FindAsync(key, default))!;
            var attempt = current.AttemptId;
            if (current.State != DurableOperationState.Dispatching)
            {
                attempt = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(key, DateTimeOffset.UtcNow, default)).AttemptId;
                Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(key, attempt.Value, DateTimeOffset.UtcNow, default));
            }
            members.Add(new(key, attempt!.Value, new(Output.Contract, Output.Key, node.Input.Json)));
        }
        var payload = item.Plan.Inputs.Acquisitions.Resource(resource).Input!;
        return new(members, [], [Identity(item, resource, Decode(payload)!.Identity)], Observation(new(item.Plan.Inputs, item.Plan.Graph, item.Plan.Graph.Nodes.First(x => x.ResourceId == resource), item.Plan.Inputs.Configuration!.Manifest.Spec)));
    }
    private static async Task<ExternalOperationOutput?> ReadAsync(ServiceProvider services, Prepared item, string operation = "referenced-first", ManagedResourceKey? owner = null) =>
        await services.GetRequiredService<IConfigurationOutputEvidence>().ReadAsync(Operation(item, operation), owner ?? new(item.Environment,
            item.Plan.Inputs.Revision.ApplicationRecordId, Referenced), Key(ResourceAcquisitionMode.Reference), Output.Contract, Output.Key, default);

    [Fact]
    public async Task Mixed_modes_confirm_independent_groups_and_recover_accepted_outputs_without_reobservation()
    {
        var connection = await CreateDatabaseAsync("reference_kernel_");
        var boundary = new Boundary();
        var item = await PrepareAsync(connection, boundary);
        Assert.Equal(2, item.Plan.Graph.ObservationGroups.Count);
        Assert.Equal(0, boundary.Observations);
        await using (var unavailable = Services(connection, boundary, referenceExecutor: false))
            Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await unavailable.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(Operation(item, "imported-verify"), default)).Outcome);
        await using (var unavailable = Services(connection, boundary, referenceEvidence: false))
            Assert.Equal(DurableExecutionOutcome.HandlerUnavailable, (await unavailable.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(Operation(item, "imported-verify"), default)).Outcome);
        Assert.Equal(0, boundary.Observations);
        Guid receipt;
        await using (var act = Services(connection, boundary))
        {
            Assert.Null(await ReadAsync(act, item));
            await act.GetRequiredService<IProviderConfigurationStore>().AddRevisionAsync(item.Environment, item.Provider, ProviderConfigurationRevisionNumber.Create(2),
                new SampleConfiguration("verified-test-service", "sample.impl/v2"), default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await act.GetRequiredService<IProviderAliasStore>().ChangeAsync(item.Environment, ProviderAlias.Create("sample.primary"),
                new(item.Provider, ProviderConfigurationRevisionNumber.Create(1)), new(item.Provider, ProviderConfigurationRevisionNumber.Create(2)), "tester", default)).Outcome);
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(Operation(item, "imported-verify"), default)).Outcome);
            Assert.Null(await ReadAsync(act, item));
            var progress = await act.GetRequiredService<IDurableOperationJournal>().ProgressAsync(item.Commit.DeploymentId, item.Commit.Generation, default);
            Assert.Null(progress!.AppliedGeneration);
            Assert.Equal(1, progress.Completed);
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(Operation(item, "referenced-first"), default)).Outcome);
            Assert.NotNull(await ReadAsync(act, item));
            Assert.Null(await ReadAsync(act, item, owner: new(item.Environment, ApplicationRecordId.New(), Referenced)));
            receipt = (await act.GetRequiredService<IResourceObservationAcceptance>().FindAsync(Operation(item, "referenced-second"), default))!.Id;
            Assert.True(await act.GetRequiredService<IDurableOperationJournal>().TryCompleteGenerationAsync(item.Commit.DeploymentId, item.Commit.Generation, act.GetRequiredService<DurableActionAdmission>(), default));
        }
        await using (var restart = Services(connection, boundary))
        {
            var saved = (await restart.GetRequiredService<IDurableDeploymentPlanner>().FindPlanAsync(item.Plan.Id, default))!;
            Assert.Equal(item.Plan, saved);
            Assert.Equal(1, saved.Inputs.Providers.Resource(Referenced).Configuration.Value);
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await restart.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(Operation(item, "referenced-first"), default)).Outcome);
            Assert.Equal(receipt, (await restart.GetRequiredService<IResourceObservationAcceptance>().FindAsync(Operation(item, "referenced-first"), default))!.Id);
            Assert.NotNull(await ReadAsync(restart, item));
            await using var db = await restart.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.Equal(2, await db.ResourceObservationReceipts.CountAsync());
            Assert.Equal(3, await db.OperationOutputs.CountAsync());
            Assert.Equal(3, await db.DurableOperationAttempts.CountAsync());
            Assert.Equal(1, await db.ResourceIdentityClaims.CountAsync());
            Assert.Empty(await db.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
            Assert.Empty(await db.Secrets.AsNoTracking().ToArrayAsync());
            Assert.Equal(ResourceLifecycleAuthority.ConsumptionOnly, (await db.ResourceManagement.AsNoTracking().SingleAsync(x => x.Resource == Referenced.Value)).Authority);
        }
        await using (var missing = Services(connection, boundary, referenceExecutor: false, referenceEvidence: false))
        {
            Assert.Equal(receipt, (await missing.GetRequiredService<IResourceObservationAcceptance>().FindAsync(Operation(item, "referenced-first"), default))!.Id);
            Assert.Null(await ReadAsync(missing, item));
        }
        Assert.Equal(2, boundary.Observations);
    }
    [Fact]
    public Task Missing_observation_capability_refuses_before_any_sibling_handler_or_publication() => MissingCapabilityAsync(true);

    [Fact]
    public Task Missing_reference_identity_capability_refuses_before_any_sibling_handler_or_publication() => MissingCapabilityAsync(false);

    private async Task MissingCapabilityAsync(bool missingObservation)
    {
        var connection = await CreateDatabaseAsync("reference_capability_");
        var boundary = new Boundary();
        var item = await PrepareAsync(connection, boundary);
        await using var services = Services(connection, boundary, observationCapability: !missingObservation,
            referenceIdentityCapability: missingObservation);
        // Retain the exact handler, ordinary output registration and matching output identity.
        var output = Assert.Single(services.GetServices<IExternalOperationOutputContract>(), x => x.Producer == Key(ResourceAcquisitionMode.Reference));
        Assert.Equal(Output, output.Identity);
        var handler = Assert.Single(services.GetServices<IDurableActionHandler>(), x => x.Key == Key(ResourceAcquisitionMode.Reference));
        Assert.All(item.Plan.Graph.Nodes.Where(x => x.ResourceId == Referenced), node => Assert.True(handler.Supports(node)));
        var result = await services.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(Operation(item, "imported-verify"), default);
        await using (var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            Assert.Equal((DurableExecutionOutcome.HandlerUnavailable, 0, 0, 0, 0, 0),
                (result.Outcome, boundary.HandlerCalls, boundary.Observations, await verify.DurableOperationAttempts.CountAsync(),
                    await verify.ResourceObservationReceipts.CountAsync(), await verify.OperationOutputs.CountAsync()));
        ResourceObservationAcceptance request;
        await using (var healthy = Services(connection, boundary)) request = await StageAsync(healthy, item, Imported);
        Assert.Equal(ResourceObservationAcceptanceStatus.InputUnavailable, (await services.GetRequiredService<IResourceObservationAcceptance>()
            .CompleteAsync(request, DateTimeOffset.UtcNow, default)).Status);
        await using var afterDirect = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(0, boundary.HandlerCalls);
        Assert.Equal(0, boundary.Observations);
        Assert.Empty(await afterDirect.ResourceObservationReceipts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await afterDirect.OperationOutputs.AsNoTracking().ToArrayAsync());
        Assert.All(await afterDirect.DurableOperations.AsNoTracking().ToArrayAsync(), x => Assert.NotEqual(nameof(DurableOperationState.Succeeded), x.State));
    }

    [Fact]
    public async Task Direct_acceptance_validates_every_member_prerequisite_and_cannot_publish_partial_group()
    {
        var connection = await CreateDatabaseAsync("reference_prerequisite_");
        var boundary = new Boundary { SecondMemberDependsOnImport = true };
        var item = await PrepareAsync(connection, boundary);
        await using var act = Services(connection, boundary);
        var request = await StageAsync(act, item, Referenced);
        var service = act.GetRequiredService<IResourceObservationAcceptance>();
        Assert.False((await service.CompleteAsync(request with { Operations = [request.Operations[0]] }, DateTimeOffset.UtcNow, default)).IsSuccess);
        Assert.False((await service.CompleteAsync(request, DateTimeOffset.UtcNow, default)).IsSuccess);
        Assert.False((await service.CompleteAsync(request with { Reservations = [Identity(item, Referenced, "invented-service")] }, DateTimeOffset.UtcNow, default)).IsSuccess);
        Assert.False(await act.GetRequiredService<IExternalOperationOutputs>().CompleteAsync(request.Operations[0].Key, request.Operations[0].AttemptId, request.Operations[0].Output, DateTimeOffset.UtcNow, default));
        Assert.False(await act.GetRequiredService<IDurableOperationJournal>().TryRecordSuccessAsync(request.Operations[0].Key, request.Operations[0].AttemptId, DateTimeOffset.UtcNow, default));
        Assert.Null(await ReadAsync(act, item));
        await using (var verify = await act.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.Empty(await verify.ResourceObservationReceipts.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.OperationOutputs.AsNoTracking().ToArrayAsync());
        }
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(Operation(item, "imported-verify"), default)).Outcome);
        var corrupt = request.Operations.Select((x, index) => index == 1 ? x with { Output = x.Output with { Payload = "{}" } } : x).ToArray();
        Assert.False((await service.CompleteAsync(request with { Operations = corrupt }, DateTimeOffset.UtcNow, default)).IsSuccess);
        Assert.Null(await ReadAsync(act, item));
        await using (var verify = await act.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(1, await verify.OperationOutputs.CountAsync());
            Assert.Empty(await verify.ResourceIdentityClaims.AsNoTracking().ToArrayAsync());
            Assert.Equal(1, await verify.ResourceObservationReceipts.CountAsync());
        }
        Assert.Equal(DurableExecutionOutcome.Succeeded, (await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(Operation(item, "referenced-first"), default)).Outcome);
        Assert.NotNull(await ReadAsync(act, item));
    }
    [Fact]
    public async Task Canceled_observation_repeats_only_exact_registered_readonly_attempt_then_history_survives_inactive_authority()
    {
        var connection = await CreateDatabaseAsync("reference_restart_");
        var boundary = new Boundary();
        var item = await PrepareAsync(connection, boundary, mixed: false);
        using (var cancel = new CancellationTokenSource())
        {
            boundary.CancelBeforeAcceptance = cancel;
            await using var act = Services(connection, boundary);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(Operation(item, "referenced-first"), cancel.Token).AsTask());
            Assert.Null(await ReadAsync(act, item));
        }
        boundary.CancelBeforeAcceptance = null;
        Guid receipt;
        await using (var restart = Services(connection, boundary))
        {
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await restart.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(Operation(item, "referenced-first"), default)).Outcome);
            receipt = (await restart.GetRequiredService<IResourceObservationAcceptance>().FindAsync(Operation(item, "referenced-first"), default))!.Id;
            await using var db = await restart.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.Equal(2, await db.DurableOperationAttempts.CountAsync());
            await db.Database.ExecuteSqlRawAsync("DELETE FROM resource_management_heads");
        }
        await using (var historical = Services(connection, boundary))
        {
            Assert.Equal(receipt, (await historical.GetRequiredService<IResourceObservationAcceptance>().FindAsync(Operation(item, "referenced-first"), default))!.Id);
            Assert.Null(await ReadAsync(historical, item));
        }
        Assert.Equal(2, boundary.Observations);
    }
    [Fact]
    public async Task Reference_claim_and_reservation_writers_serialize_both_orders_and_no_row_race()
    {
        foreach (var order in new[] { "reference-first", "reservation-first", "concurrent" })
        {
            var connection = await CreateDatabaseAsync("reference_claim_");
            var boundary = new Boundary();
            var item = await PrepareAsync(connection, boundary, mixed: false);
            var peer = await PrepareAsync(connection, boundary, "peer-app", item, false, "other-service");
            await using var act = Services(connection, boundary);
            var request = await StageAsync(act, item, Referenced);
            var acceptance = act.GetRequiredService<IResourceObservationAcceptance>();
            var reservations = act.GetRequiredService<IProviderResourceReservations>();
            var claim = Identity(peer, Referenced, "shared-service");
            bool referenceWon;
            bool reservationWon;
            if (order == "reference-first")
            {
                referenceWon = (await acceptance.CompleteAsync(request, DateTimeOffset.UtcNow, default)).IsSuccess;
                reservationWon = await reservations.TryReserveAsync(claim, default);
            }
            else if (order == "reservation-first")
            {
                reservationWon = await reservations.TryReserveAsync(claim, default);
                referenceWon = (await acceptance.CompleteAsync(request, DateTimeOffset.UtcNow, default)).IsSuccess;
            }
            else
            {
                var referenceTask = acceptance.CompleteAsync(request, DateTimeOffset.UtcNow, default).AsTask();
                var reservationTask = reservations.TryReserveAsync(claim, default).AsTask();
                await Task.WhenAll(referenceTask, reservationTask);
                referenceWon = (await referenceTask).IsSuccess;
                reservationWon = await reservationTask;
            }
            Assert.NotEqual(referenceWon, reservationWon);
            if (order == "reference-first") Assert.True(referenceWon);
            if (order == "reservation-first") Assert.True(reservationWon);
            if (referenceWon)
            {
                Assert.False(await reservations.TryReserveAsync(Identity(item, Referenced, "different-identity"), default));
                Assert.False(await reservations.TryReserveAsync(Identity(item, Referenced, "shared-service"), default));
            }
            else Assert.Null(await ReadAsync(act, item));
        }
    }
    [Fact]
    public async Task Duplicate_reference_association_refuses_without_publishing_peer_outputs()
    {
        var connection = await CreateDatabaseAsync("reference_duplicate_");
        var boundary = new Boundary();
        var item = await PrepareAsync(connection, boundary, mixed: false);
        await using (var first = Services(connection, boundary))
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await first.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(Operation(item, "referenced-first"), default)).Outcome);
        var peer = await PrepareAsync(connection, boundary, "peer-app", item, false);
        await using var act = Services(connection, boundary);
        var request = await StageAsync(act, peer, Referenced);
        Assert.Equal(ResourceObservationAcceptanceStatus.Conflict, (await act.GetRequiredService<IResourceObservationAcceptance>().CompleteAsync(request, DateTimeOffset.UtcNow, default)).Status);
        Assert.Null(await ReadAsync(act, peer));
        Assert.NotNull(await ReadAsync(act, item));
    }
    [Fact]
    public async Task Reference_and_import_acceptance_share_conflict_authority_in_both_orders_and_concurrently()
    {
        foreach (var order in new[] { "reference-first", "import-first", "concurrent" })
        {
            var connection = await CreateDatabaseAsync("reference_import_race_");
            var boundary = new Boundary();
            var reference = await PrepareAsync(connection, boundary, mixed: false);
            var import = await PrepareAsync(connection, boundary, "import-peer", reference, false, singleMode: ResourceAcquisitionMode.ImportUseAndRetain);
            await using var services = Services(connection, boundary);
            var referenceRequest = await StageAsync(services, reference, Referenced);
            var importRequest = await StageAsync(services, import, Referenced);
            var service = services.GetRequiredService<IResourceObservationAcceptance>();
            ResourceObservationAcceptanceResult referenceResult;
            ResourceObservationAcceptanceResult importResult;
            if (order == "reference-first")
            {
                referenceResult = await service.CompleteAsync(referenceRequest, DateTimeOffset.UtcNow, default);
                importResult = await service.CompleteAsync(importRequest, DateTimeOffset.UtcNow, default);
                Assert.True(referenceResult.IsSuccess);
            }
            else if (order == "import-first")
            {
                importResult = await service.CompleteAsync(importRequest, DateTimeOffset.UtcNow, default);
                referenceResult = await service.CompleteAsync(referenceRequest, DateTimeOffset.UtcNow, default);
                Assert.True(importResult.IsSuccess);
            }
            else
            {
                var first = service.CompleteAsync(referenceRequest, DateTimeOffset.UtcNow, default).AsTask();
                var second = service.CompleteAsync(importRequest, DateTimeOffset.UtcNow, default).AsTask();
                await Task.WhenAll(first, second);
                referenceResult = await first;
                importResult = await second;
            }
            Assert.NotEqual(referenceResult.IsSuccess, importResult.IsSuccess);
            Assert.Equal(ResourceObservationAcceptanceStatus.Conflict, referenceResult.IsSuccess ? importResult.Status : referenceResult.Status);
            // An ignored success response is recovered concurrently from the same durable group.
            var winner = referenceResult.IsSuccess ? referenceRequest : importRequest;
            var receipt = (await service.FindAsync(winner.Operations[0].Key, default))!.Id;
            var retries = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => service.CompleteAsync(winner, DateTimeOffset.UtcNow, default).AsTask()));
            Assert.All(retries, result => Assert.Equal(ResourceObservationAcceptanceStatus.Existing, result.Status));
            Assert.Equal(receipt, (await service.FindAsync(winner.Operations[0].Key, default))!.Id);
        }
    }
    [Fact]
    public async Task Cancellation_before_commit_is_atomic_and_lost_commit_reply_recovers_exact_durable_confirmation()
    {
        foreach (var beforeCommit in new[] { true, false })
        {
            var connection = await CreateDatabaseAsync("reference_commit_fault_");
            var boundary = new Boundary();
            var item = await PrepareAsync(connection, boundary, mixed: false);
            ResourceObservationAcceptance request;
            await using (var stage = Services(connection, boundary)) request = await StageAsync(stage, item, Referenced);
            using var cancellation = new CancellationTokenSource();
            var fault = new CommitFault(beforeCommit, cancellation);
            await using (var act = Services(connection, boundary, interceptor: fault))
            {
                var result = await act.GetRequiredService<IResourceObservationAcceptance>().CompleteAsync(request, DateTimeOffset.UtcNow, cancellation.Token);
                Assert.Equal(!beforeCommit, result.IsSuccess);
                Assert.True(cancellation.IsCancellationRequested);
            }
            await using var restart = Services(connection, boundary);
            var service = restart.GetRequiredService<IResourceObservationAcceptance>();
            var receipt = await service.FindAsync(request.Operations[0].Key, default);
            await using (var verify = await restart.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            {
                Assert.Equal(beforeCommit ? 0 : 1, await verify.ResourceObservationReceipts.CountAsync());
                Assert.Equal(beforeCommit ? 0 : 2, await verify.OperationOutputs.CountAsync());
                Assert.Equal(beforeCommit ? 0 : 1, await verify.ResourceIdentityClaims.CountAsync());
            }
            if (beforeCommit)
            {
                Assert.Null(receipt);
                Assert.Null(await ReadAsync(restart, item));
            }
            else
            {
                Assert.NotNull(receipt);
                Assert.Equal(ResourceObservationAcceptanceStatus.Existing, (await service.CompleteAsync(request, DateTimeOffset.UtcNow, default)).Status);
                Assert.Equal(receipt.Id, (await service.FindAsync(request.Operations[1].Key, default))!.Id);
                Assert.NotNull(await ReadAsync(restart, item));
            }
        }
    }
    [Fact]
    public async Task Superseded_catalog_history_refuses_before_schema_mutation_and_never_resets_records()
    {
        var connection = await CreateDatabaseAsync("reference_reset_guard_");
        var boundary = new Boundary();
        // This unconfirmed Import has no Reference claim or newer origin requiring the new schema.
        _ = await PrepareAsync(connection, boundary, mixed: false, singleMode: ResourceAcquisitionMode.ImportUseAndRetain);
        long count;
        await using (var arrange = Services(connection, boundary))
        await using (var db = await arrange.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            count = await db.DurableChangeSets.LongCountAsync();
            await db.GetService<IMigrator>().MigrateAsync("20261122081845_PersistUnconfirmedImportCorrection");
        }
        await using (var act = Services(connection, boundary))
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => act.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync());
            Assert.Contains("explicitly initialized fresh catalog", failure.Message, StringComparison.Ordinal);
        }
        await using var verify = Services(connection, boundary);
        await using var catalog = await verify.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(count, await catalog.DurableChangeSets.LongCountAsync());
        Assert.DoesNotContain(await catalog.Database.GetAppliedMigrationsAsync(), x => x.EndsWith("_ResourceObservationKernel", StringComparison.Ordinal));
        Assert.Equal(0, boundary.Observations);
    }
    private sealed class CommitFault(bool beforeCommit, CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (beforeCommit) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            return new(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            throw new IOException("Injected lost catalog commit reply.");
        }
    }
    private sealed class Handler(ResourceAcquisitionMode mode, IDurableOperationJournal journal, IExternalOperationOutputs outputs,
        IResourceObservationAcceptance acceptance, IProviderConfigurationStore configurations, Boundary boundary) : IDurableActionAdmissionHandler, IDurableResourcePlanComposer, IReadOnlyResourceObservationHandler, IDurableResourceReusePolicy, IDurableResourceAdditionPolicy
    {
        public bool CanReuse(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource,
            DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => new ResourceDelta(accepted, target).Entries.Any(x => x.Resource == resource && x.Kind == ResourceDeltaKind.Unchanged);
        public bool CanAdd(NormalizedManifestSpec accepted, NormalizedManifestSpec target, ManifestLocalId resource,
            DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => mode == ResourceAcquisitionMode.Reference &&
            new ResourceDelta(accepted, target).Entries.Any(x => x.Resource == resource && x.Kind == ResourceDeltaKind.Added);
        public DurableActionExecutionKey Key => ResourceReferenceKernelTests.Key(mode);
        public bool RequiresExternalOutput => true;
        public ExternalOperationOutputIdentity OutputIdentity => Output;
        public bool Validate(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs) => Decode(inputs.Acquisitions.Resource(resourceId).Input!) is not null;
        public IReadOnlyList<DurableOperationNode> Plan(ManifestLocalId resourceId, NormalizedResource resource, DurablePlanningInputs inputs)
        {
            var p = inputs.Providers.Resource(resourceId);
            DurableOperationNode Node(string suffix, IReadOnlyList<OperationId> dependencies) => new(OperationId.Create(resourceId.Value + suffix), resourceId, Key,
                new(p.Environment, p.Instance, p.Configuration, p.Implementation.Value), inputs.Acquisitions.Resource(resourceId).Input!, dependencies)
            { RequiresExternalOutput = true, RequiredOutput = Output };
            return mode == ResourceAcquisitionMode.Reference
                ? [Node("-first", []), Node("-second", boundary.SecondMemberDependsOnImport ? [OperationId.Create("imported-verify")] : [])]
                : [Node("-verify", [])];
        }
        public bool Validate(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => Validate(resource, spec.Resources[resource], inputs);
        public IReadOnlyList<DurableOperationNode> Plan(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration) => Plan(resource, spec.Resources[resource], inputs);
        public bool ValidateGraph(NormalizedManifestSpec spec, ManifestLocalId resource, DurablePlanningInputs inputs, PreparedProviderConfiguration configuration, DurableOperationGraph graph) =>
            graph.Nodes.Count(x => x.ResourceId == resource) == Plan(resource, spec.Resources[resource], inputs).Count &&
            Plan(resource, spec.Resources[resource], inputs).All(expected => graph.Nodes.Any(actual => actual.ResourceId == resource && actual.Id == expected.Id && actual.Action == expected.Action &&
                actual.Input == expected.Input && actual.References == expected.References && actual.Dependencies.SequenceEqual(expected.Dependencies) && actual.RequiredOutput == expected.RequiredOutput));
        public bool Supports(DurableOperationNode node) => node.Action == Key && Decode(node.Input) is not null;
        public async ValueTask<DurableExecutionResult> ExecuteAsync(DurableOperation operation, CancellationToken token)
        {
            Interlocked.Increment(ref boundary.HandlerCalls);
            var accepted = (await outputs.FindAcceptedAsync(operation.Key, token))!;
            var members = accepted.Graph.ObservationGroups.Single(x => x.Operations.Contains(operation.Node.Id));
            var confirmations = new List<ObservationOperationConfirmation>();
            foreach (var id in members.Operations)
            {
                var key = operation.Key with { OperationId = id };
                var current = (await journal.FindAsync(key, token))!;
                Guid attempt;
                if (current.State == DurableOperationState.Dispatching) attempt = current.AttemptId!.Value;
                else
                {
                    attempt = Assert.IsType<DurableBeginAttemptResult.Started>(await journal.TryBeginAttemptAsync(key, DateTimeOffset.UtcNow, token)).AttemptId;
                    Assert.IsType<DurableMarkerResult.Committed>(await journal.TryCommitDispatchMarkerAsync(key, attempt, DateTimeOffset.UtcNow, token));
                }
                confirmations.Add(new(key, attempt, new(Output.Contract, Output.Key, current.Node.Input.Json)));
            }
            var pinned = await configurations.FindRevisionAsync<SampleConfiguration>(operation.Environment, operation.ProviderInstance, operation.ConfigurationRevision, token);
            Assert.Equal("sample.impl/v1", pinned!.Configuration.ImplementationId);
            Assert.Equal("verified-test-service", pinned.Configuration.Target);
            Interlocked.Increment(ref boundary.Observations);
            boundary.CancelBeforeAcceptance?.Cancel();
            token.ThrowIfCancellationRequested();
            var identity = Decode(operation.Node.Input)!.Identity;
            var request = new ResourceObservationAcceptance(confirmations, [], [new(accepted.Provider.Instance, accepted.Inputs.Environment,
                accepted.Inputs.Revision.ApplicationRecordId, operation.Node.ResourceId, "test.service", identity)], Observation(accepted));
            var result = await acceptance.CompleteAsync(request, DateTimeOffset.UtcNow, token);
            Assert.True(result.IsSuccess, result.Status.ToString());
            return new(result.IsSuccess ? DurableExecutionOutcome.Succeeded : DurableExecutionOutcome.InputUnavailable, await journal.FindAsync(operation.Key, token));
        }
    }
    private sealed class OutputOnlyEvidence(ResourceAcquisitionMode mode) : IExternalOperationOutputContract
    {
        public DurableActionExecutionKey Producer => Key(mode);
        public ExternalOperationOutputIdentity Identity => Output;
        public bool Validate(DurableOperationNode node, ExternalOperationOutput output) => new Evidence(mode).Validate(node, output);
    }
    private sealed class ObservationOnlyEvidence(ResourceAcquisitionMode mode) : IResourceObservationOutputContract
    {
        public DurableActionExecutionKey Producer => Key(mode);
        public ExternalOperationOutputIdentity Identity => Output;
        public bool Validate(DurableOperationNode node, ExternalOperationOutput output) => new Evidence(mode).Validate(node, output);
        public bool ValidateObservationEvidence(AcceptedOperation accepted, DurableActionPayload evidence) => new Evidence(mode).ValidateObservationEvidence(accepted, evidence);
    }
    private sealed class ReferenceOnlyEvidence(ResourceAcquisitionMode mode) : IResourceReferenceOutputContract
    {
        private readonly Evidence evidence = new(mode);
        public DurableActionExecutionKey Producer => evidence.Producer;
        public ExternalOperationOutputIdentity Identity => evidence.Identity;
        public bool Validate(DurableOperationNode node, ExternalOperationOutput output) => evidence.Validate(node, output);
        public bool ValidateObservationEvidence(AcceptedOperation accepted, DurableActionPayload payload) => evidence.ValidateObservationEvidence(accepted, payload);
        public bool ValidateReferenceIdentities(AcceptedOperation accepted, IReadOnlyList<ProviderResourceReservation> identities) => evidence.ValidateReferenceIdentities(accepted, identities);
    }
    private sealed class Evidence(ResourceAcquisitionMode mode, string sharingContract = "test.reference-sharing/v1", bool sharingAvailable = true) : IResourceReferenceSharingContract
    {
        public string SharingContract => sharingContract;
        public DurableActionPayload? AuthorizeSharing(DurableOperationNode operation, DurableActionPayload request)
        {
            if (!sharingAvailable || request.Format != "test.sharing-request/v1" || mode != ResourceAcquisitionMode.Reference) return null;
            try
            {
                var value = JsonSerializer.Deserialize(request.Json, CapabilityJsonContext.Default.SharingRequestDocument);
                var input = Decode(operation.Input);
                return value?.Group is { Length: > 0 and <= 64 } && value.Group == input?.SharingClass
                    ? new("test.sharing-decision/v1", JsonSerializer.Serialize(new SharingDecisionDocument(value.Group, input.Identity,
                        operation.References.ConfigurationRevision.Value), CapabilityJsonContext.Default.SharingDecisionDocument)) : null;
            }
            catch (JsonException) { return null; }
        }
        public bool AreCompatible(ReferenceSharingParticipant own, ReferenceSharingParticipant peer)
        {
            var left = JsonSerializer.Deserialize(own.Authorization.Decision.Json, CapabilityJsonContext.Default.SharingDecisionDocument);
            var right = JsonSerializer.Deserialize(peer.Authorization.Decision.Json, CapabilityJsonContext.Default.SharingDecisionDocument);
            return left is not null && right is not null && (left.Group == "open" || left.Group == right.Group) && left.Identity == right.Identity &&
                own.Identity.Namespace == "test.service" && peer.Identity.Namespace == "test.service" &&
                own.Identity.Value == left.Identity && peer.Identity.Value == right.Identity;
        }
        public DurableActionExecutionKey Producer => Key(mode);
        public ExternalOperationOutputIdentity Identity => Output;
        public bool Validate(DurableOperationNode node, ExternalOperationOutput output) => node.Action == Producer && output.Contract == Output.Contract && output.Key == Output.Key && output.Payload == node.Input.Json && Decode(node.Input) is not null;
        public bool ValidateObservationEvidence(AcceptedOperation accepted, DurableActionPayload evidence) => accepted.Node.Action == Producer && evidence == Observation(accepted);
        public bool ValidateReferenceIdentities(AcceptedOperation accepted, IReadOnlyList<ProviderResourceReservation> identities) => identities.Count == 1 &&
            identities[0] == new ProviderResourceReservation(accepted.Provider.Instance, accepted.Inputs.Environment, accepted.Inputs.Revision.ApplicationRecordId,
                accepted.Node.ResourceId, "test.service", Decode(accepted.Node.Input)!.Identity);
    }
    internal sealed record SharingObservationDocument(string Environment, string Application, string Resource, string Provider, long Revision, string Identity);
    private static DurableActionPayload Observation(AcceptedOperation accepted) => accepted.Node.Sharing is null
        ? new("test.observed/v1", accepted.Node.Input.Json)
        : new("test.shared-observed/v1", JsonSerializer.Serialize(new SharingObservationDocument(accepted.Inputs.Environment.ToString(),
            accepted.Inputs.Revision.ApplicationRecordId.ToString(), accepted.Node.ResourceId.Value, accepted.Provider.Instance.ToString(),
            accepted.Provider.Configuration.Value, Decode(accepted.Node.Input)!.Identity), CapabilityJsonContext.Default.SharingObservationDocument));
    internal sealed record SharingRequestDocument(string Group);
    internal sealed record SharingDecisionDocument(string Group, string Identity, long Revision);
    private static DurableActionPayload SharingRequest(string group) => new("test.sharing-request/v1",
        JsonSerializer.Serialize(new SharingRequestDocument(group), CapabilityJsonContext.Default.SharingRequestDocument));
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);
    }
    private sealed class Ready : IProviderRevisionPreflight
    {
        public ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment, ProviderAliasBinding target, CancellationToken token) => new(new ProviderPreflightResult(ProviderPreflightOutcome.Ready));
    }
    [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
    [JsonSerializable(typeof(CapabilityInput))]
    [JsonSerializable(typeof(SharingRequestDocument))]
    [JsonSerializable(typeof(SharingObservationDocument))]
    [JsonSerializable(typeof(SharingDecisionDocument))]
    internal partial class CapabilityJsonContext : JsonSerializerContext;
}
