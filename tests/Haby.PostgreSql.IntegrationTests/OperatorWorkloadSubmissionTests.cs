using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    public async Task Operator_submit_uses_real_acceptance_durable_pipeline_and_independent_Hosts(bool initial, bool configured, bool lost, bool concurrent)
    {
        var diagnostic = new SubmissionFailureDiagnostics(initial, configured, lost, concurrent);
        await HostFailureDiagnostics.RunAsync(async _ =>
        {
            var deployment = new ConfiguredSubmissionFixture { LoseResponse = lost };
            var identity = new IdentityFixture();
            var mixedRecipients = initial && configured && !lost && !concurrent;
            var scenario = await InternalScenarioAsync(initialOnly: initial,
                configureManifest: configured ? e =>
                {
                    ConfiguredDocuments(e, secondDocument: true);
                    if (mixedRecipients)
                    {
                        var workloads = e["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!;
                        workloads["z-plain"] = workloads["main"]!.DeepClone();
                        workloads["z-plain"]!["properties"]!.AsObject().Remove("configuration");
                    }
                }
            : null,
                acceptGeneration: async (services, environment, reference) =>
                {
                    DurableChangeSetCommit? accepted = null;
                    await HostFailureDiagnostics.RunAsync(async defer =>
                    {
                        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
                        string connection, environmentKey; long? baseline;
                        await using (var read = await factory.CreateDbContextAsync())
                        {
                            connection = read.Database.GetConnectionString()!;
                            environmentKey = (await read.Environments.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(environment.ToString()))).EnvironmentId;
                            baseline = await read.ApplicationDeployments.AsNoTracking().Select(x => (long?)x.DesiredGeneration).SingleOrDefaultAsync();
                        }
                        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
                        void Compose(IServiceCollection collection) => collection.AddSingleton<InternalDurableActions>(p => new(
                            p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), [deployment, identity],
                            p.GetServices<IProviderConfigurationCodec>(), p.GetServices<IExternalOperationOutputContract>(), p.GetRequiredService<DurableActionAdmission>(),
                            p.GetRequiredService<DurableOperationSerialization>(), p.GetRequiredService<TimeProvider>(),
                            configurationPreparation: p.GetRequiredService<InternalWorkloadConfiguration>()));
                        diagnostic.At(SubmissionCheckpoint.InitialHostBuild);
                        var host = OperatorHostApplication.Build(args, Compose);
                        defer(host.DisposeAsync);
                        diagnostic.At(SubmissionCheckpoint.InitialHostStart);
                        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
                        diagnostic.At(SubmissionCheckpoint.InitialChannel);
                        var channel = GrpcChannel.ForAddress(host.Urls.Single());
                        defer(() => { channel.Dispose(); return ValueTask.CompletedTask; });
                        var client = OperatorTestCredentials.Client(channel);
                        var changeRequest = new CreateChangeSetRequest
                        {
                            EnvironmentId = environmentKey,
                            Reference = new() { ApplicationRecordId = reference.ApplicationRecordId.ToString(), RevisionNumber = reference.RevisionNumber.Value },
                            Precondition = baseline is null ? PlanningPrecondition.ExpectedAbsence : PlanningPrecondition.ExpectedGeneration
                        };
                        if (baseline is not null) changeRequest.ExpectedGeneration = baseline.Value;
                        diagnostic.At(SubmissionCheckpoint.ChangeSet);
                        var change = (await client.CreateChangeSetAsync(changeRequest)).ChangeSet;
                        diagnostic.At(SubmissionCheckpoint.Plan);
                        var plan = await client.CreatePlanAsync(new ChangeSetRequest { ChangeSetId = change.ChangeSetId });
                        diagnostic.At(SubmissionCheckpoint.PlanStatus);
                        Assert.Equal(PlanningStatus.Planned, plan.Status);
                        diagnostic.At(SubmissionCheckpoint.Commit);
                        var commit = await client.CommitDesiredStateAsync(new() { ChangeSetId = change.ChangeSetId, PlanId = plan.Plan.PlanId });
                        diagnostic.At(SubmissionCheckpoint.CommitStatus);
                        Assert.Equal(CommitStatus.Committed, commit.Status);
                        var request = new GenerationRequest { DeploymentId = commit.Commit.DeploymentId, Generation = commit.Commit.Generation };
                        diagnostic.At(SubmissionCheckpoint.OperatorGrant);
                        var created = await host.Services.GetRequiredService<IOperatorAccessStore>().CreateAsync("submit actor", "test-owner", null, default);
                        var headers = new Metadata { { "authorization", "Bearer " + created.Credential } };
                        var submitter = new ManifestOperator.ManifestOperatorClient(channel);
                        diagnostic.At(SubmissionCheckpoint.DeniedCredentials);
                        foreach (var forbidden in new[] { new Metadata(), new Metadata { { "authorization", "Bearer " + ConfigurationDeliveryCredential.Create(Guid.CreateVersion7()) } } })
                            Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await submitter.SubmitGenerationAsync(request, forbidden))).StatusCode);
                        WorkloadSubmissionReply first = null!;
                        diagnostic.At(SubmissionCheckpoint.InitialSubmit);
                        if (concurrent)
                        {
                            await HostFailureDiagnostics.RunAsync(async release =>
                            {
                                var peer = OperatorHostApplication.Build(args, Compose);
                                release(peer.DisposeAsync);
                                await OperatorTestCredentials.StartWithIssuedOperatorAsync(peer);
                                var peerChannel = GrpcChannel.ForAddress(peer.Urls.Single());
                                release(() => { peerChannel.Dispose(); return ValueTask.CompletedTask; });
                                var peerClient = new ManifestOperator.ManifestOperatorClient(peerChannel);
                                Assert.NotSame(host.Services.GetRequiredService<DurableOperationSerialization>(), peer.Services.GetRequiredService<DurableOperationSerialization>());
                                diagnostic.At(SubmissionCheckpoint.InitialSubmit);
                                var results = await Task.WhenAll(submitter.SubmitGenerationAsync(request, headers).ResponseAsync,
                                    peerClient.SubmitGenerationAsync(request, headers).ResponseAsync);
                                diagnostic.At(SubmissionCheckpoint.ConcurrentOutcome);
                                Assert.Contains(results, x => x.Outcome == "submitted");
                                diagnostic.At(SubmissionCheckpoint.InitialSubmit);
                                first = await submitter.SubmitGenerationAsync(request, headers);
                            }, () => HostFailurePhase.Catalog, () => null, diagnostic.Record);
                        }
                        else
                        {
                            diagnostic.At(SubmissionCheckpoint.InitialSubmit);
                            first = await submitter.SubmitGenerationAsync(request, headers);
                        }
                        diagnostic.At(SubmissionCheckpoint.InitialOutcome);
                        diagnostic.Values(lost ? SubmissionDiagnosticValue.Stopped : SubmissionDiagnosticValue.Submitted, first.Outcome);
                        Assert.Equal(lost ? "stopped" : "submitted", first.Outcome);
                        if (lost)
                        {
                            diagnostic.At(SubmissionCheckpoint.InitialProgress);
                            var firstProgress = first.Progress.Submission;
                            diagnostic.Values(SubmissionDiagnosticValue.Partial, firstProgress);
                            Assert.Equal("partial", firstProgress);
                            diagnostic.At(SubmissionCheckpoint.RevokePrepare);
                            var permissions = await host.Services.GetRequiredService<InternalRuntimePublication>().PreparationRequestsAsync(
                                new(DeploymentChangeSetId.Parse(commit.Commit.ChangeSetId), DeploymentPlanId.Parse(commit.Commit.PlanId), ApplicationDeploymentId.Parse(request.DeploymentId),
                                    DeploymentGeneration.Create(request.Generation), DateTimeOffset.Parse(commit.Commit.CommittedAtUtc), commit.Commit.TechnicalActorId), "test-revoke", default);
                            diagnostic.At(SubmissionCheckpoint.RevokeRead);
                            var original = (await host.Services.GetRequiredService<InternalRuntimePublication>().ReadAsync(permissions[0].Key, default))!;
                            diagnostic.At(SubmissionCheckpoint.RevokeApply);
                            await host.Services.GetRequiredService<InternalRuntimePublication>().ApplyAsync(permissions[0] with
                            { Key = "operator-submit-revoke", Operation = RuntimePublicationOperation.RevokePermission, ExpectedToken = original.CurrentPermission!.Token, Origin = null }, default);
                        }
                        diagnostic.At(SubmissionCheckpoint.RestartStop);
                        await host.StopAsync();
                        diagnostic.At(SubmissionCheckpoint.RestartBuild);
                        var restarted = OperatorHostApplication.Build(args, Compose);
                        defer(restarted.DisposeAsync);
                        diagnostic.At(SubmissionCheckpoint.RestartStart);
                        await OperatorTestCredentials.StartWithIssuedOperatorAsync(restarted);
                        diagnostic.At(SubmissionCheckpoint.RestartChannel);
                        var freshChannel = GrpcChannel.ForAddress(restarted.Urls.Single());
                        defer(() => { freshChannel.Dispose(); return ValueTask.CompletedTask; });
                        var freshClient = OperatorTestCredentials.Client(freshChannel);
                        diagnostic.At(SubmissionCheckpoint.Replay);
                        var replay = await freshClient.SubmitGenerationAsync(request);
                        diagnostic.At(SubmissionCheckpoint.ReplayOutcome);
                        diagnostic.Values(SubmissionDiagnosticValue.Submitted, replay.Outcome);
                        Assert.Equal("submitted", replay.Outcome);
                        diagnostic.At(SubmissionCheckpoint.NativeRequired);
                        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
                        if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
                        if (binary is not null)
                        {
                            diagnostic.At(SubmissionCheckpoint.NativeCli);
                            using var native = System.Text.Json.JsonDocument.Parse(await OperatorHostIntegrationTests.RunNativeCommitCli(binary, restarted.Urls.Single(),
                                ["deployment", "generation", "submit", request.DeploymentId, request.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
                            diagnostic.At(SubmissionCheckpoint.NativeOutcome);
                            var nativeOutcome = native.RootElement.GetProperty("outcome"u8).GetString();
                            diagnostic.Values(SubmissionDiagnosticValue.Submitted, nativeOutcome);
                            Assert.Equal("submitted", nativeOutcome);
                        }
                        diagnostic.At(SubmissionCheckpoint.RuntimeAuthority);
                        var authority = replay.Progress.Members.Single(x => x.Workload == "main").RuntimeAuthority;
                        diagnostic.Values(lost ? SubmissionDiagnosticValue.Revoked : configured ? SubmissionDiagnosticValue.Available : SubmissionDiagnosticValue.NotApplicable, authority);
                        Assert.Equal(lost ? "revoked" : configured ? "available" : "notApplicable", authority);
                        diagnostic.At(SubmissionCheckpoint.ReadinessReads);
                        Assert.Equal(0, deployment.ReadinessReads);
                        await using (var beforeWait = await factory.CreateDbContextAsync())
                        {
                            diagnostic.At(SubmissionCheckpoint.NoRolloutCheckpoint);
                            Assert.Empty(await beforeWait.GenerationStageCheckpoints.AsNoTracking().Where(x =>
                                x.DeploymentId == Guid.Parse(request.DeploymentId) && x.Generation == request.Generation && x.Stage == "RolloutReady").ToArrayAsync());
                        }
                        var expectedReadinessReads = lost ? 0 : mixedRecipients ? 2 : 1;
                        diagnostic.At(SubmissionCheckpoint.ReadinessReads);
                        var waitRequest = new GenerationWaitRequest
                        {
                            DeploymentId = request.DeploymentId,
                            Generation = request.Generation,
                            RequestId = "public-wait",
                            Settings = new() { MaximumRounds = 1 }
                        };
                        var readiness = await freshClient.WaitGenerationAsync(waitRequest);
                        Assert.Equal("observed", readiness.Outcome);
                        Assert.Equal(!lost, readiness.CurrentReady); Assert.Equal(!lost, readiness.OriginalRequestReady);
                        Assert.Equal(mixedRecipients ? 2 : 1, readiness.Members.Count);
                        if (lost) Assert.Contains(readiness.Members, x => x.Outcome == "Refused" && x.Code == "runtime-authority-unavailable");
                        else Assert.True(readiness.HasSavedReadyId);
                        diagnostic.At(SubmissionCheckpoint.ReadinessReads);
                        Assert.Equal(expectedReadinessReads, deployment.ReadinessReads);
                        diagnostic.At(SubmissionCheckpoint.Replay);
                        var readsBeforeReplay = deployment.ReadinessReads;
                        var replayWait = await freshClient.WaitGenerationAsync(waitRequest);
                        diagnostic.At(SubmissionCheckpoint.ReadinessReads);
                        Assert.Equal(readsBeforeReplay, deployment.ReadinessReads);
                        diagnostic.At(SubmissionCheckpoint.Replay);
                        Assert.True(replayWait.Replayed); Assert.False(replayWait.CurrentReady);
                        Assert.Equal(readiness.OriginalRequestReady, replayWait.OriginalRequestReady);
                        Assert.Equal(readiness.Members, replayWait.Members);
                        diagnostic.At(SubmissionCheckpoint.DeploymentWrites);
                        Assert.Equal(mixedRecipients ? 2 : 1, deployment.Writes);
                        diagnostic.At(SubmissionCheckpoint.IdentityWrites);
                        Assert.Equal(configured ? 1 : 0, identity.Writes);
                        if (mixedRecipients)
                        {
                            diagnostic.At(SubmissionCheckpoint.PlainMember);
                            var plain = replay.Progress.Members.Single(x => x.Workload == "z-plain");
                            diagnostic.At(SubmissionCheckpoint.PlainDocuments);
                            Assert.Equal("notApplicable", plain.Documents);
                            diagnostic.At(SubmissionCheckpoint.PlainIdentity);
                            Assert.Equal("notApplicable", plain.Identity);
                            diagnostic.At(SubmissionCheckpoint.PlainPublication);
                            Assert.Equal("notApplicable", plain.Publication);
                            diagnostic.At(SubmissionCheckpoint.PlainRuntimeAuthority);
                            Assert.Equal("notApplicable", plain.RuntimeAuthority);
                        }
                        diagnostic.At(SubmissionCheckpoint.ReadinessReads);
                        Assert.Equal(expectedReadinessReads, deployment.ReadinessReads);
                        diagnostic.At(SubmissionCheckpoint.GenerationRead);
                        var get = await freshClient.GetGenerationAsync(request);
                        diagnostic.At(SubmissionCheckpoint.ProgressEquality);
                        Assert.Equal(replay.Progress, get.WorkloadSubmission);
                        diagnostic.At(SubmissionCheckpoint.AppliedGeneration);
                        Assert.Equal(initial ? null : (long?)1, get.WorkloadSubmission.HasAppliedGeneration ? get.WorkloadSubmission.AppliedGeneration : null);
                        await HostFailureDiagnostics.RunAsync(async release =>
                        {
                            diagnostic.At(SubmissionCheckpoint.AuditContext);
                            var verify = await factory.CreateDbContextAsync();
                            release(verify.DisposeAsync);
                            diagnostic.At(SubmissionCheckpoint.IntentRead);
                            var intents = await verify.ActionIntents.AsNoTracking().Where(x => x.Generation == request.Generation).ToArrayAsync();
                            diagnostic.At(SubmissionCheckpoint.IntentActors);
                            Assert.All(intents, x => Assert.Equal(OperatorCredential.Actor(created.Grant.Id), x.Actor));
                            diagnostic.At(SubmissionCheckpoint.AttemptRead);
                            var attempts = await verify.DurableOperationAttempts.AsNoTracking().Where(x => x.Generation == request.Generation).ToArrayAsync();
                            diagnostic.At(SubmissionCheckpoint.AttemptActors);
                            Assert.All(attempts, x => Assert.Equal(OperatorCredential.Actor(created.Grant.Id), x.Actor));
                            diagnostic.At(SubmissionCheckpoint.TechnicalActor);
                            Assert.NotEqual(commit.Commit.TechnicalActorId, intents[0].Actor);
                            diagnostic.At(SubmissionCheckpoint.RecipientCount);
                            Assert.Equal(configured ? 2 : 0, await verify.WorkloadConfigurationRecipients.CountAsync());
                            diagnostic.At(SubmissionCheckpoint.SubmittedCheckpoint);
                            Assert.Single(await verify.GenerationStageCheckpoints.AsNoTracking().Where(x => x.Generation == request.Generation && x.Stage == "WorkloadsSubmitted").ToArrayAsync());
                            diagnostic.At(SubmissionCheckpoint.RolloutCheckpoint);
                            var readyCheckpoints = await verify.GenerationStageCheckpoints.AsNoTracking().Where(x =>
                                x.DeploymentId == Guid.Parse(request.DeploymentId) && x.Generation == request.Generation && x.Stage == "RolloutReady").ToArrayAsync();
                            if (lost) Assert.Empty(readyCheckpoints);
                            else Assert.Equal(Guid.Parse(readiness.SavedReadyId), Assert.Single(readyCheckpoints).Id);
                            if (lost)
                            {
                                diagnostic.At(SubmissionCheckpoint.ObservationActor);
                                Assert.NotEqual(intents[0].Actor, (await verify.ActionObservations.AsNoTracking().SingleAsync()).Actor);
                            }
                        }, () => HostFailurePhase.Catalog, () => null, diagnostic.Record);
                        diagnostic.At(SubmissionCheckpoint.ReturnCommit);
                        accepted = new DurableChangeSetCommit(DeploymentChangeSetId.Parse(commit.Commit.ChangeSetId), DeploymentPlanId.Parse(commit.Commit.PlanId),
                            ApplicationDeploymentId.Parse(request.DeploymentId), DeploymentGeneration.Create(request.Generation), DateTimeOffset.Parse(commit.Commit.CommittedAtUtc), commit.Commit.TechnicalActorId);
                    }, () => HostFailurePhase.Catalog, () => null, diagnostic.Record);
                    diagnostic.At(SubmissionCheckpoint.ScenarioCleanup);
                    return accepted!;
                });
            diagnostic.At(SubmissionCheckpoint.CommitPresent);
            Assert.NotNull(scenario.Commit);
        }, () => HostFailurePhase.Catalog, () => null, diagnostic.Record);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operator_submit_refuses_missing_or_corrupt_first_receipt_without_accepting_or_dispatching_second(bool corrupt)
    {
        var deployment = new ConfiguredSubmissionFixture();
        var identity = new IdentityFixture();
        var interrupt = true;
        await InternalScenarioAsync(initialOnly: true, configureManifest: envelope =>
        {
            var workloads = envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!;
            workloads["z-last"] = workloads["main"]!.DeepClone();
        }, acceptGeneration: async (services, environment, reference) =>
        {
            var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            string connection, environmentKey;
            await using (var read = await factory.CreateDbContextAsync())
            {
                connection = read.Database.GetConnectionString()!;
                environmentKey = (await read.Environments.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(environment.ToString()))).EnvironmentId;
            }
            await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"],
                collection => collection.AddSingleton<InternalDurableActions>(p => new(p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
                    [deployment, identity], p.GetServices<IProviderConfigurationCodec>(), p.GetServices<IExternalOperationOutputContract>(),
                    p.GetRequiredService<DurableActionAdmission>(), p.GetRequiredService<DurableOperationSerialization>(), p.GetRequiredService<TimeProvider>(),
                    fault: point => { if (point == InternalActionFault.AfterCompletionCommit && interrupt) { interrupt = false; throw new IOException("Controlled confirmed-prefix interruption."); } },
                    configurationPreparation: p.GetRequiredService<InternalWorkloadConfiguration>())));
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var client = OperatorTestCredentials.Client(channel);
            var change = (await client.CreateChangeSetAsync(new CreateChangeSetRequest
            {
                EnvironmentId = environmentKey,
                Reference = new() { ApplicationRecordId = reference.ApplicationRecordId.ToString(), RevisionNumber = reference.RevisionNumber.Value },
                Precondition = PlanningPrecondition.ExpectedAbsence
            })).ChangeSet;
            var plan = await client.CreatePlanAsync(new ChangeSetRequest { ChangeSetId = change.ChangeSetId });
            var commit = await client.CommitDesiredStateAsync(new() { ChangeSetId = change.ChangeSetId, PlanId = plan.Plan.PlanId });
            Assert.Equal(CommitStatus.Committed, commit.Status);
            var request = new GenerationRequest { DeploymentId = commit.Commit.DeploymentId, Generation = commit.Commit.Generation };
            var partial = await client.SubmitGenerationAsync(request);
            Assert.Equal("unavailable", partial.Outcome);
            Assert.Equal("confirmed", partial.Progress.Members.Single(x => x.Workload == "main").Submission);
            Assert.Equal("notAttempted", partial.Progress.Members.Single(x => x.Workload == "z-last").Submission);
            Assert.Equal(1, deployment.Writes);
            await using (var arrange = await factory.CreateDbContextAsync())
            {
                var first = await arrange.ActionIntents.AsNoTracking().SingleAsync(x => x.LocalId == "main");
                var output = arrange.OperationOutputs.Where(x => x.DeploymentId == first.DeploymentId && x.Generation == first.Generation && x.OperationId == first.OperationId);
                if (corrupt) Assert.Equal(1, await output.ExecuteUpdateAsync(x => x.SetProperty(row => row.Payload, "corrupt-confirmation")));
                else Assert.Equal(1, await output.ExecuteDeleteAsync());
            }
            async Task<string[]> Snapshot()
            {
                await using var read = await factory.CreateDbContextAsync();
                var intents = await read.ActionIntents.AsNoTracking().ToArrayAsync();
                var operations = await read.DurableOperations.AsNoTracking().ToArrayAsync();
                var attempts = await read.DurableOperationAttempts.AsNoTracking().ToArrayAsync();
                var observations = await read.ActionObservations.AsNoTracking().ToArrayAsync();
                var outputs = await read.OperationOutputs.AsNoTracking().ToArrayAsync();
                return [.. intents.Select(x => $"intent:{x.OperationId}:{x.Marker}:{x.Actor}"),
                    .. operations.Select(x => $"operation:{x.OperationId}:{x.State}:{x.CurrentAttemptId}:{x.AttemptNumber}:{x.DispatchedAt}:{x.FinalizedAt}"),
                    .. attempts.Select(x => $"attempt:{x.AttemptId}:{x.Outcome}:{x.Actor}:{x.DispatchedAt}"),
                    .. observations.Select(x => $"observation:{x.Id}:{x.Ordinal}:{x.Confirmed}:{x.Actor}"),
                    .. outputs.Select(x => $"output:{x.OperationId}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(x.Payload)))}")];
            }
            var damaged = await Snapshot();
            Assert.Equal("unknown", (await client.GetGenerationAsync(request)).WorkloadSubmission.Submission);
            var refused = await client.SubmitGenerationAsync(request);
            Assert.Equal("stopped", refused.Outcome); Assert.True(refused.RequiresInspection);
            Assert.Equal("unknown", refused.Progress.Submission);
            Assert.Equal(1, deployment.Writes); Assert.Equal(0, deployment.ReadinessReads);
            Assert.Equal(damaged.Order(StringComparer.Ordinal), (await Snapshot()).Order(StringComparer.Ordinal));
            await using (var verify = await factory.CreateDbContextAsync())
            {
                Assert.Single(await verify.ActionIntents.AsNoTracking().ToArrayAsync());
                Assert.Single(await verify.ManagedWorkloads.AsNoTracking().ToArrayAsync());
                Assert.Empty(await verify.ActionIntents.AsNoTracking().Where(x => x.LocalId == "z-last").ToArrayAsync());
                Assert.Empty(await verify.GenerationStageCheckpoints.AsNoTracking().Where(x => x.Stage == "WorkloadsSubmitted").ToArrayAsync());
                Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            }
            return new(DeploymentChangeSetId.Parse(commit.Commit.ChangeSetId), DeploymentPlanId.Parse(commit.Commit.PlanId), ApplicationDeploymentId.Parse(request.DeploymentId),
                DeploymentGeneration.Create(request.Generation), DateTimeOffset.Parse(commit.Commit.CommittedAtUtc), commit.Commit.TechnicalActorId);
        });
    }

    [Fact]
    public async Task Operator_submit_request_budget_expires_while_waiting_for_generation_gate_without_starting_work()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true);
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection,
            "--Haby:Tls:Enabled=false", "--Haby:Port=0", "--Haby:WorkloadSubmission:RequestMilliseconds=10"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = host.Services.GetRequiredService<DurableOperationSerialization>();
        var held = gate.RunAsync(new(scenario.Commit.DeploymentId, scenario.Commit.Generation, OperationId.Create("budget-test")), async () =>
        { entered.TrySetResult(); await release.Task; return true; }, default).AsTask();
        try
        {
            await entered.Task;
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var reply = await OperatorTestCredentials.Client(channel).SubmitGenerationAsync(new GenerationRequest
            { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = scenario.Commit.Generation.Value }, deadline: DateTime.UtcNow.AddSeconds(30));
            Assert.Equal("cancelled", reply.Outcome); Assert.True(reply.RequiresInspection);
            var factory = host.Services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Empty(await verify.ActionIntents.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.ConfigurationDocuments.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.GenerationStageCheckpoints.AsNoTracking().ToArrayAsync());
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        }
        finally { release.TrySetResult(); await held; await host.StopAsync(); }
    }

}

internal enum SubmissionCheckpoint
{
    Setup, InitialHostBuild, InitialHostStart, InitialChannel, ChangeSet, Plan, PlanStatus, Commit, CommitStatus, OperatorGrant, DeniedCredentials,
    InitialSubmit, ConcurrentOutcome, InitialOutcome, InitialProgress, RevokePrepare, RevokeRead, RevokeApply,
    RestartStop, RestartBuild, RestartStart, RestartChannel, Replay, ReplayOutcome, NativeRequired, NativeCli, NativeOutcome,
    RuntimeAuthority, DeploymentWrites, IdentityWrites, PlainMember, PlainDocuments, PlainIdentity, PlainPublication, PlainRuntimeAuthority,
    ReadinessReads, GenerationRead, ProgressEquality, AppliedGeneration, AuditContext, IntentRead, IntentActors, AttemptRead, AttemptActors,
    TechnicalActor, RecipientCount, SubmittedCheckpoint, NoRolloutCheckpoint, RolloutCheckpoint, ObservationActor, ReturnCommit, ScenarioCleanup, CommitPresent
}
internal enum SubmissionDiagnosticValue { None, Submitted, Stopped, Partial, Cancelled, Unavailable, Unknown, Revoked, Available, NotApplicable, Missing, Other }

// Only the existing operator-submission theory uses these closed failure fields; no messages, IDs or payloads.
internal sealed class SubmissionFailureDiagnostics(bool initial, bool configured, bool lost, bool concurrent, Action<string>? capture = null)
{
    internal const string Method = "Haby.PostgreSql.IntegrationTests.DurableDeploymentPlanningTests.Operator_submit_uses_real_acceptance_durable_pipeline_and_independent_Hosts";
    private readonly HashSet<Exception> recorded = new(ReferenceEqualityComparer.Instance);
    private SubmissionCheckpoint checkpoint;
    private SubmissionDiagnosticValue expected, actual;
    private bool bodyRecorded;
    internal void At(SubmissionCheckpoint value) { checkpoint = value; expected = actual = SubmissionDiagnosticValue.None; }
    internal void Values(SubmissionDiagnosticValue wanted, string? observed)
    {
        expected = wanted;
        actual = observed switch
        {
            "submitted" => SubmissionDiagnosticValue.Submitted,
            "stopped" => SubmissionDiagnosticValue.Stopped,
            "partial" => SubmissionDiagnosticValue.Partial,
            "cancelled" => SubmissionDiagnosticValue.Cancelled,
            "unavailable" => SubmissionDiagnosticValue.Unavailable,
            "unknown" => SubmissionDiagnosticValue.Unknown,
            "revoked" => SubmissionDiagnosticValue.Revoked,
            "available" => SubmissionDiagnosticValue.Available,
            "notApplicable" => SubmissionDiagnosticValue.NotApplicable,
            null => SubmissionDiagnosticValue.Missing,
            _ => SubmissionDiagnosticValue.Other
        };
    }
    internal void Record(HostFailurePhase phase, Exception error, StatusCode? _)
    {
        var first = recorded.Add(error);
        if (phase != HostFailurePhase.Cleanup && !first) return;
        if (phase != HostFailurePhase.Cleanup && bodyRecorded) phase = HostFailurePhase.Cleanup;
        else if (phase != HostFailurePhase.Cleanup) bodyRecorded = true;
        try
        {
            if (capture is null) HostFailureDiagnostics.RecordSubmission(this, phase, error);
            else capture(Render(phase, error));
        }
        catch (Exception) { Console.Error.WriteLine("Allowlisted submission diagnostic unavailable; primary failure preserved."); }
    }
    internal string Render(HostFailurePhase phase, Exception error)
    {
        var row = (initial, configured, lost, concurrent) switch
        {
            (true, true, false, false) => "initial-configured",
            (false, true, true, false) => "addition-configured-lost",
            (true, false, false, true) => "initial-plain-concurrent",
            (false, false, false, false) => "addition-plain",
            _ => "unknown-row"
        };
        var stage = !Enum.IsDefined(checkpoint) ? "unknown" : checkpoint switch
        {
            SubmissionCheckpoint.InitialSubmit or SubmissionCheckpoint.ConcurrentOutcome or SubmissionCheckpoint.InitialOutcome or SubmissionCheckpoint.InitialProgress => "initial-submit",
            SubmissionCheckpoint.RevokePrepare or SubmissionCheckpoint.RevokeRead or SubmissionCheckpoint.RevokeApply => "revoke",
            SubmissionCheckpoint.RestartStop or SubmissionCheckpoint.RestartBuild or SubmissionCheckpoint.RestartStart or SubmissionCheckpoint.RestartChannel or SubmissionCheckpoint.Replay or SubmissionCheckpoint.ReplayOutcome => "restart-replay",
            SubmissionCheckpoint.NativeRequired or SubmissionCheckpoint.NativeCli or SubmissionCheckpoint.NativeOutcome => "native-cli",
            SubmissionCheckpoint.RuntimeAuthority or SubmissionCheckpoint.DeploymentWrites or SubmissionCheckpoint.IdentityWrites or SubmissionCheckpoint.PlainMember or SubmissionCheckpoint.PlainDocuments or SubmissionCheckpoint.PlainIdentity or SubmissionCheckpoint.PlainPublication or SubmissionCheckpoint.PlainRuntimeAuthority or SubmissionCheckpoint.ReadinessReads or SubmissionCheckpoint.GenerationRead or SubmissionCheckpoint.ProgressEquality or SubmissionCheckpoint.AppliedGeneration => "progress-readback",
            SubmissionCheckpoint.AuditContext or SubmissionCheckpoint.IntentRead or SubmissionCheckpoint.IntentActors or SubmissionCheckpoint.AttemptRead or SubmissionCheckpoint.AttemptActors or SubmissionCheckpoint.TechnicalActor or SubmissionCheckpoint.RecipientCount or SubmissionCheckpoint.SubmittedCheckpoint or SubmissionCheckpoint.NoRolloutCheckpoint or SubmissionCheckpoint.RolloutCheckpoint or SubmissionCheckpoint.ObservationActor or SubmissionCheckpoint.ReturnCommit => "audit-verification",
            SubmissionCheckpoint.ScenarioCleanup => "cleanup",
            _ => "setup"
        };
        var cleanup = phase == HostFailurePhase.Cleanup || checkpoint == SubmissionCheckpoint.ScenarioCleanup;
        if (cleanup) stage = "cleanup";
        var id = cleanup ? "Cleanup" : Enum.IsDefined(checkpoint) ? checkpoint.ToString() : "Unknown";
        var category = error switch
        {
            RpcException => "rpc",
            Xunit.Sdk.XunitException => "assertion",
            OperationCanceledException => "cancellation",
            TimeoutException => "timeout",
            System.Text.Json.JsonException => "json",
            Npgsql.NpgsqlException or DbUpdateException => "storage",
            IOException => "io",
            _ => "other"
        };
        var status = error is RpcException rpc ? Enum.IsDefined(rpc.StatusCode) ? rpc.StatusCode.ToString() : "unknown-status" : "unavailable-evidence";
        static string Value(SubmissionDiagnosticValue value) => Enum.IsDefined(value) ? value.ToString() : "Other";
        return Method + "," + row + "," + stage + "," + id + "," + Value(cleanup ? SubmissionDiagnosticValue.None : expected) + "," +
            Value(cleanup ? SubmissionDiagnosticValue.None : actual) + "," + category + "," + status;
    }
}
