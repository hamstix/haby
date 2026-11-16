using System.Text;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    internal static async Task<DurableChangeSetCommit> AcceptBatchPeerAsync(ServiceProvider services, InternalScenario scenario, JsonNode envelope)
    {
        var copy = envelope.DeepClone(); copy["manifest"]!["metadata"]!["id"] = "batch-" + Guid.NewGuid().ToString("N")[..12];
        var catalog = new ManifestContractCatalog([PostgreSqlDatabaseContract.Definition], [KubernetesContract.Definition], [], []);
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), catalog, scenario.Time).ExecuteAsync(Encoding.UTF8.GetBytes(copy.ToJsonString()), "tester"));
        return await new InternalAcceptedGenerationBuilder(services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>(),
            (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>(), services.GetServices<IInternalActionHandler>().ToArray())
            .AcceptForEffectAsync(scenario.Environment, imported.ManifestRevisionRef.ApplicationRecordId, imported.ManifestRevisionRef,
                new DeploymentPrecondition.ExpectedAbsence(), "tester", default);
    }
    private static JsonNode BatchEnvelope()
    {
        var envelope = JsonNode.Parse(Envelope)!;
        envelope["manifest"]!["spec"]!.AsObject().Remove("resources");
        envelope["manifest"]!["spec"]!["components"] = JsonNode.Parse("""{"api":{"workloads":{"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary","properties":{"image":{"reference":"registry.example/app:v1"}}}}}}"""u8);
        return envelope;
    }
    private static BatchObservationMember BatchMember(DurableChangeSetCommit commit, string batch) => new(commit.DeploymentId, commit.Generation,
        BatchObservationKeys.Member(batch, commit.DeploymentId.ToString(), commit.Generation.Value));
    private static PostgreSqlOperatorRolloutObservation BatchFacade(ServiceProvider services, InternalScenario scenario,
        InternalDurableActions actions, ConfiguredSubmissionFixture deployment, IdentityFixture identity, BatchObservationOptions? options = null) => new(
            services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), ConfigurationPreparation(services).Preparation,
            actions, new(PollMilliseconds: 100), scenario.Time, deployment.Contract.Action, identity.Contract.Action, options);

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public async Task Batch_worker_bound_limits_actual_concurrent_reads_at_default_and_ceiling(int concurrency)
    {
        var scenario = await RolloutScenario(configured: false); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured: false); await SubmitRollout(actions, scenario, deployment);
        var commits = new List<DurableChangeSetCommit> { scenario.Commit };
        for (var i = 1; i < 8; i++)
        {
            var peer = await AcceptBatchPeerAsync(services, scenario, BatchEnvelope());
            await SubmitRollout(actions, scenario with { Commit = peer }, deployment); commits.Add(peer);
        }
        var gate = new object(); var active = 0; var maximum = 0;
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        deployment.BeforeRead = async token =>
        {
            lock (gate) { active++; maximum = Math.Max(maximum, active); if (active == concurrency) full.TrySetResult(); }
            try { await full.Task.WaitAsync(token); }
            finally { lock (gate) active--; }
        };
        var facade = BatchFacade(services, scenario, actions, deployment, identity, new(Concurrency: concurrency, StartsPerSecond: 100));
        var result = await facade.WaitBatchAsync("concurrent", "operator", commits.Select(x => BatchMember(x, "concurrent")).ToArray(),
            null, new(MaximumRounds: 1), null, default);
        Assert.True(result.CurrentReady); Assert.Equal(concurrency, maximum); Assert.Equal(0, active); Assert.Equal(8, deployment.ReadinessReads);
    }

    [Fact]
    public async Task Batch_ordered_mixed_results_fresh_complete_and_replay_preserve_keys_actor_and_stable_settings()
    {
        var scenario = await RolloutScenario(configured: false); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured: false); await SubmitRollout(actions, scenario, deployment);
        var peer = await AcceptBatchPeerAsync(services, scenario, BatchEnvelope());
        await SubmitRollout(actions, scenario with { Commit = peer }, deployment);
        var facade = BatchFacade(services, scenario, actions, deployment, identity);
        var first = BatchMember(scenario.Commit, "mixed"); var second = BatchMember(peer, "mixed");
        var absent = new BatchObservationMember(ApplicationDeploymentId.New(), DeploymentGeneration.Create(1), "absent");
        var mixed = await facade.WaitBatchAsync("mixed", "operator", [first, absent, second], null, new(MaximumRounds: 1), null, default);
        Assert.False(mixed.CurrentReady); Assert.Equal(new[] { first, absent, second }, mixed.Members.Select(x => x.Member));
        Assert.True(mixed.Members[0].CurrentReady); Assert.Equal(GenerationReadinessOutcome.NotFound, mixed.Members[1].Outcome); Assert.True(mixed.Members[2].CurrentReady);
        var complete = await facade.WaitBatchAsync("fresh", "operator", [BatchMember(peer, "fresh"), BatchMember(scenario.Commit, "fresh")], null, new(MaximumRounds: 1), null, default);
        Assert.True(complete.CurrentReady); Assert.Equal(4, deployment.ReadinessReads);
        var replay = await facade.WaitBatchAsync("mixed", "operator", [second, first], new(RequestMilliseconds: 2000), new(MaximumRounds: 1), null, default);
        Assert.False(replay.CurrentReady); Assert.All(replay.Members, x => { Assert.True(x.Replayed); Assert.True(x.OriginalRequestReady); Assert.False(x.CurrentReady); });
        Assert.Equal(4, deployment.ReadinessReads);
        foreach (var changed in new[] { (Actor: "different", Settings: new Haby.Application.GenerationObservationSettings(MaximumRounds: 1)),
            (Actor: "operator", Settings: new Haby.Application.GenerationObservationSettings(MaximumRounds: 2)) })
        {
            var conflict = await facade.WaitBatchAsync("mixed", changed.Actor, [first], null, changed.Settings, null, default);
            var summary = Assert.Single(conflict.Members); Assert.Equal(GenerationReadinessOutcome.Conflict, summary.Outcome);
            Assert.Null(summary.SavedReady); Assert.Null(summary.Submitted); Assert.False(summary.OriginalRequestReady);
        }
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.All(await verify.ApplicationDeployments.AsNoTracking().ToArrayAsync(), x => Assert.Equal(x.DesiredGeneration, x.AppliedGeneration));
        Assert.Equal(2, deployment.Writes); Assert.Equal(0, identity.Writes);
    }
    [Fact]
    public async Task Batch_unstarted_count_exhaustion_creates_no_request_attempt_or_checkpoint_for_the_next_member()
    {
        var scenario = await RolloutScenario(configured: false); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured: false); await SubmitRollout(actions, scenario, deployment);
        var peer = await AcceptBatchPeerAsync(services, scenario, BatchEnvelope()); await SubmitRollout(actions, scenario with { Commit = peer }, deployment);
        var first = BatchMember(scenario.Commit, "limited"); var second = BatchMember(peer, "limited");
        Guid[] existingAttempts;
        var id = Guid.Parse(peer.DeploymentId.ToString());
        await using (var arrange = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            existingAttempts = await arrange.ObservationAttempts.AsNoTracking().Where(x => x.DeploymentId == id).Select(x => x.Id).ToArrayAsync();
        var result = await BatchFacade(services, scenario, actions, deployment, identity).WaitBatchAsync("limited", "operator", [first, second],
            new(Concurrency: 1, MaximumReads: 1), new(MaximumRounds: 1), null, default);
        Assert.True(result.Members[0].CurrentReady); Assert.Equal(GenerationReadinessOutcome.NotStartedBudget, result.Members[1].Outcome);
        Assert.False(result.CurrentReady); Assert.Equal(1, deployment.ReadinessReads);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.False(await verify.ObservationRecords.AsNoTracking().AnyAsync(x => x.DeploymentId == id));
        Assert.Equal(existingAttempts, await verify.ObservationAttempts.AsNoTracking().Where(x => x.DeploymentId == id).Select(x => x.Id).ToArrayAsync());
        Assert.False(await verify.GenerationStageCheckpoints.AsNoTracking().AnyAsync(x => x.DeploymentId == id && x.Stage == "RolloutReady"));
    }
    [Fact]
    public async Task Batch_generation_gate_wait_consumes_total_deadline_without_losing_another_completed_member()
    {
        var scenario = await RolloutScenario(configured: false); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured: false); await SubmitRollout(actions, scenario, deployment);
        var peer = await AcceptBatchPeerAsync(services, scenario, BatchEnvelope()); await SubmitRollout(actions, scenario with { Commit = peer }, deployment);
        var gate = services.GetRequiredService<DurableOperationSerialization>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = gate.RunAsync(new(scenario.Commit.DeploymentId, scenario.Commit.Generation, OperationId.Create("held")), async () =>
            { entered.SetResult(); await release.Task; return true; }, default).AsTask();
        await entered.Task;
        try
        {
            var result = await BatchFacade(services, scenario, actions, deployment, identity).WaitBatchAsync("queued", "operator",
                [BatchMember(scenario.Commit, "queued"), BatchMember(peer, "queued")], new(Concurrency: 2, RequestMilliseconds: 1000), new(MaximumRounds: 1), null, default);
            Assert.True(result.Deadline); Assert.Equal(GenerationReadinessOutcome.NotStartedBudget, result.Members[0].Outcome);
            Assert.True(result.Members[1].CurrentReady); Assert.False(result.CurrentReady);
            await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.False(await verify.ObservationRecords.AsNoTracking().AnyAsync(x => x.Request == BatchMember(scenario.Commit, "queued").RequestId));
        }
        finally { release.SetResult(); await held; }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_shared_read_count_charges_active_and_expired_recovery_without_renewing_attempts(bool expired)
    {
        var scenario = await RolloutScenario(shared: true, configured: false); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured: false); await SubmitRollout(actions, scenario, deployment); await SubmitRollout(actions, scenario, deployment, "second");
        if (expired) ((AdjustableTimeProvider)scenario.Time).Advance(TimeSpan.FromSeconds(5));
        var facade = BatchFacade(services, scenario, actions, deployment, identity);
        var result = await facade.WaitBatchAsync("reads", "operator", [BatchMember(scenario.Commit, "reads")], new(MaximumReads: 1), new(MaximumRounds: 2), null, default);
        Assert.False(result.CurrentReady); Assert.Equal(1, deployment.ReadinessReads);
        var summary = Assert.Single(result.Members); Assert.Equal(GenerationReadinessOutcome.BudgetExhausted, summary.Outcome);
        var replay = await facade.WaitBatchAsync("reads", "operator", [BatchMember(scenario.Commit, "reads")], null, new(MaximumRounds: 2), null, default);
        Assert.True(Assert.Single(replay.Members).Replayed); Assert.False(replay.CurrentReady); Assert.Equal(1, deployment.ReadinessReads);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.All(await verify.ObservationAttempts.AsNoTracking().ToArrayAsync(), x => Assert.Equal(1, x.Ordinal));
        Assert.Equal(expired ? "recovery" : "active", (await verify.ObservationRecords.AsNoTracking().SingleAsync(x => x.Kind == "Read" && x.FinishedAt != null)).InputJson);
        Assert.Equal(1, await verify.ObservationRecords.AsNoTracking().CountAsync(x => x.Kind == "Read" && x.FinishedAt == null));
    }
    [Fact]
    public async Task Batch_generated_authenticated_RPC_rejects_all_invalid_input_before_work_and_serializes_same_keys_across_Hosts()
    {
        var scenario = await RolloutScenario(configured: false); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured: false); await SubmitRollout(actions, scenario, deployment);
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        void Compose(IServiceCollection collection)
        {
            collection.AddSingleton(scenario.Time);
            collection.AddSingleton<InternalDurableActions>(p => new(p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
                [deployment, identity], p.GetServices<IProviderConfigurationCodec>(), p.GetServices<IExternalOperationOutputContract>(),
                p.GetRequiredService<DurableActionAdmission>(), p.GetRequiredService<DurableOperationSerialization>(), scenario.Time,
                configurationPreparation: p.GetRequiredService<InternalWorkloadConfiguration>()));
        }
        await using var host = OperatorHostApplication.Build(args, Compose); await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        await using var peer = OperatorHostApplication.Build(args, Compose); await OperatorTestCredentials.StartWithIssuedOperatorAsync(peer);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); using var other = GrpcChannel.ForAddress(peer.Urls.Single());
        var client = OperatorTestCredentials.Client(channel); var second = OperatorTestCredentials.Client(other, OperatorTestCredentials.Credential(host.Urls.Single()));
        var request = new GenerationBatchWaitRequest { RequestId = "wire", MemberSettings = new() { MaximumRounds = 1 } };
        request.Members.Add(new GenerationBatchMember
        {
            DeploymentId = scenario.Commit.DeploymentId.ToString(),
            Generation = scenario.Commit.Generation.Value,
            RequestId = BatchMember(scenario.Commit, "wire").RequestId
        });
        var unauthenticated = new ManifestOperator.ManifestOperatorClient(channel);
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await unauthenticated.WaitGenerationBatchAsync(request))).StatusCode);
        var duplicate = request.Clone(); duplicate.Members.Add(duplicate.Members[0].Clone());
        var tooMany = request.Clone(); tooMany.Members.AddRange(Enumerable.Range(0, 200).Select(_ => request.Members[0].Clone()));
        var oversized = request.Clone(); oversized.RequestId = new string('x', 131073);
        var wrongKey = request.Clone(); wrongKey.Members[0].RequestId = "wrong";
        var wider = request.Clone(); wider.Settings = new() { Concurrency = 5 };
        foreach (var invalid in new[] { new GenerationBatchWaitRequest(), duplicate, tooMany, oversized, wrongKey, wider })
            Assert.True((await client.WaitGenerationBatchAsync(invalid)).InvalidRequest);
        Assert.Equal(0, deployment.ReadinessReads);
        long beforeCompletionToken;
        await using (var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.False(await verify.ObservationRecords.AsNoTracking().AnyAsync());
            beforeCompletionToken = (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).ConcurrencyToken;
        }
        var results = await Task.WhenAll(client.WaitGenerationBatchAsync(request).ResponseAsync, second.WaitGenerationBatchAsync(request).ResponseAsync);
        Assert.Equal(1, results.Count(x => x.CurrentReady)); Assert.Equal(1, results.Count(x => x.Members.Single().Replayed));
        Assert.Equal(1, deployment.ReadinessReads);
        Assert.Equal(1, results.Count(x => x.CurrentApplied));
        // A concurrent loser can read the reserved request before the winner commits its outcome.
        // A subsequent historical replay must expose the one durable completion.
        var replay = await second.WaitGenerationBatchAsync(request);
        Assert.True(replay.Members.Single().Replayed); Assert.True(replay.Members.Single().OriginalRequestApplied);
        Assert.False(replay.CurrentApplied); Assert.Equal(1, deployment.ReadinessReads);
        await using (var verify = await peer.Services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            await AssertAtomicCompletionAsync(verify, "wire", present: true);
            Assert.Equal(beforeCompletionToken + 1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).ConcurrencyToken);
        }
        Assert.Equal(1, deployment.Writes); Assert.Equal(0, identity.Writes);
        var conflict = request.Clone(); conflict.MemberSettings.MaximumRounds = 2;
        Assert.Equal(BatchReadinessOutcome.Conflict, (await second.WaitGenerationBatchAsync(conflict)).Members.Single().Outcome);
        var full = new GenerationBatchWaitRequest { RequestId = "two-hundred" };
        for (var i = 1; i <= 200; i++)
        {
            var id = ApplicationDeploymentId.New().ToString();
            full.Members.Add(new GenerationBatchMember { DeploymentId = id, Generation = 1, RequestId = BatchObservationKeys.Member(full.RequestId, id, 1) });
        }
        var missing = await client.WaitGenerationBatchAsync(full);
        Assert.Equal(200, missing.Total); Assert.Equal(full.Members, missing.Members.Select(x => x.Member));
        Assert.All(missing.Members, x => Assert.Equal(BatchReadinessOutcome.NotFound, x.Outcome));
        await host.StopAsync(); await peer.StopAsync();
    }
    [Fact]
    public async Task Batch_rechecks_revocation_after_rate_wait_and_preserves_safe_same_actor_historical_replay()
    {
        var scenario = await RolloutScenario(shared: true); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity); await SubmitRollout(actions, scenario, deployment); await SubmitRollout(actions, scenario, deployment, "second");
        var facade = BatchFacade(services, scenario, actions, deployment, identity);
        var history = BatchMember(scenario.Commit, "history");
        var publication = Publications(services, identity);
        // The retained request offset rotates the second call: second reads first, main waits for pacing.
        var permission = (await publication.PreparationRequestsAsync(scenario.Commit, "tester", default)).Single(x => x.Origin!.Workload == "main");
        var before = (await publication.ReadAsync(permission.Key, default))!;
        Assert.True((await facade.WaitBatchAsync("history", "operator", [history], null, new(MaximumRounds: 1), null, default)).CurrentApplied);
        var member = BatchMember(scenario.Commit, "revoked-in-queue");
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var revoke = RevokeQueued();
        var result = await facade.WaitBatchAsync("revoked-in-queue", "operator", [member], new(Concurrency: 1, StartsPerSecond: 1), new(MaximumRounds: 1), null, bounded.Token);
        await revoke;
        var refused = Assert.Single(result.Members); Assert.Equal(GenerationReadinessOutcome.Refused, refused.Outcome);
        Assert.Null(refused.Submitted); Assert.Null(refused.SavedReady); Assert.False(refused.OriginalRequestReady); Assert.Equal(3, deployment.ReadinessReads);
        var replay = await facade.WaitBatchAsync("history", "operator", [history], null, new(MaximumRounds: 1), null, default);
        Assert.False(replay.CurrentReady); Assert.True(replay.Members[0].Replayed); Assert.True(replay.Members[0].OriginalRequestReady);
        Assert.Equal(3, deployment.ReadinessReads); Assert.Equal(2, deployment.Writes);

        async Task RevokeQueued()
        {
            // This concurrent read-only probe owns a separate context/connection; it never shares the observer's tracking state.
            await using (var verify = await contexts.CreateDbContextAsync(bounded.Token))
            {
                await verify.Database.OpenConnectionAsync(bounded.Token);
                while (!await verify.ObservationRecords.AsNoTracking().AnyAsync(x => x.Request == member.RequestId && x.Kind == "Read" &&
                    x.Subject == "Workload/api/main" && x.FinishedAt == null, bounded.Token)) await Task.Delay(10, bounded.Token);
            }
            await publication.ApplyAsync(Revoke(permission, before.CurrentPermission!.Token), bounded.Token);
        }
    }
}
