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
            } : null,
            acceptGeneration: async (services, environment, reference) =>
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
                await using var host = OperatorHostApplication.Build(args, Compose);
                await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
                using var channel = GrpcChannel.ForAddress(host.Urls.Single());
                var client = OperatorTestCredentials.Client(channel);
                var changeRequest = new CreateChangeSetRequest { EnvironmentId = environmentKey,
                    Reference = new() { ApplicationRecordId = reference.ApplicationRecordId.ToString(), RevisionNumber = reference.RevisionNumber.Value },
                    Precondition = baseline is null ? PlanningPrecondition.ExpectedAbsence : PlanningPrecondition.ExpectedGeneration };
                if (baseline is not null) changeRequest.ExpectedGeneration = baseline.Value;
                var change = (await client.CreateChangeSetAsync(changeRequest)).ChangeSet;
                var plan = await client.CreatePlanAsync(new ChangeSetRequest { ChangeSetId = change.ChangeSetId });
                Assert.Equal(PlanningStatus.Planned, plan.Status);
                var commit = await client.CommitDesiredStateAsync(new() { ChangeSetId = change.ChangeSetId, PlanId = plan.Plan.PlanId });
                Assert.Equal(CommitStatus.Committed, commit.Status);
                var request = new GenerationRequest { DeploymentId = commit.Commit.DeploymentId, Generation = commit.Commit.Generation };
                var created = await host.Services.GetRequiredService<IOperatorAccessStore>().CreateAsync("submit actor", "test-owner", null, default);
                var headers = new Metadata { { "authorization", "Bearer " + created.Credential } };
                var submitter = new ManifestOperator.ManifestOperatorClient(channel);
                foreach (var forbidden in new[] { new Metadata(), new Metadata { { "authorization", "Bearer " + ConfigurationDeliveryCredential.Create(Guid.CreateVersion7()) } } })
                    Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await submitter.SubmitGenerationAsync(request, forbidden))).StatusCode);
                WorkloadSubmissionReply first;
                if (concurrent)
                {
                    await using var peer = OperatorHostApplication.Build(args, Compose);
                    await OperatorTestCredentials.StartWithIssuedOperatorAsync(peer);
                    using var peerChannel = GrpcChannel.ForAddress(peer.Urls.Single());
                    var peerClient = new ManifestOperator.ManifestOperatorClient(peerChannel);
                    Assert.NotSame(host.Services.GetRequiredService<DurableOperationSerialization>(), peer.Services.GetRequiredService<DurableOperationSerialization>());
                    var results = await Task.WhenAll(submitter.SubmitGenerationAsync(request, headers).ResponseAsync,
                        peerClient.SubmitGenerationAsync(request, headers).ResponseAsync);
                    Assert.Contains(results, x => x.Outcome == "submitted");
                    first = await submitter.SubmitGenerationAsync(request, headers);
                }
                else first = await submitter.SubmitGenerationAsync(request, headers);
                Assert.Equal(lost ? "stopped" : "submitted", first.Outcome);
                if (lost)
                {
                    Assert.Equal("partial", first.Progress.Submission);
                    var permissions = await host.Services.GetRequiredService<InternalRuntimePublication>().PreparationRequestsAsync(
                        new(DeploymentChangeSetId.Parse(commit.Commit.ChangeSetId), DeploymentPlanId.Parse(commit.Commit.PlanId), ApplicationDeploymentId.Parse(request.DeploymentId),
                            DeploymentGeneration.Create(request.Generation), DateTimeOffset.Parse(commit.Commit.CommittedAtUtc), commit.Commit.TechnicalActorId), "test-revoke", default);
                    var original = (await host.Services.GetRequiredService<InternalRuntimePublication>().ReadAsync(permissions[0].Key, default))!;
                    await host.Services.GetRequiredService<InternalRuntimePublication>().ApplyAsync(permissions[0] with
                    { Key = "operator-submit-revoke", Operation = RuntimePublicationOperation.RevokePermission, ExpectedToken = original.CurrentPermission!.Token, Origin = null }, default);
                }
                await host.StopAsync();
                await using var restarted = OperatorHostApplication.Build(args, Compose);
                await OperatorTestCredentials.StartWithIssuedOperatorAsync(restarted);
                using var freshChannel = GrpcChannel.ForAddress(restarted.Urls.Single());
                var freshClient = OperatorTestCredentials.Client(freshChannel);
                var replay = await freshClient.SubmitGenerationAsync(request);
                Assert.Equal("submitted", replay.Outcome);
                var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
                if (Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1") Assert.False(string.IsNullOrWhiteSpace(binary));
                if (binary is not null)
                {
                    using var native = System.Text.Json.JsonDocument.Parse(await OperatorHostIntegrationTests.RunNativeCommitCli(binary, restarted.Urls.Single(),
                        ["deployment", "generation", "submit", request.DeploymentId, request.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
                    Assert.Equal("submitted", native.RootElement.GetProperty("outcome"u8).GetString());
                }
                Assert.Equal(lost ? "revoked" : configured ? "available" : "notApplicable", replay.Progress.Members.Single(x => x.Workload == "main").RuntimeAuthority);
                Assert.Equal(mixedRecipients ? 2 : 1, deployment.Writes); Assert.Equal(configured ? 1 : 0, identity.Writes);
                if (mixedRecipients)
                {
                    var plain = replay.Progress.Members.Single(x => x.Workload == "z-plain");
                    Assert.Equal("notApplicable", plain.Documents); Assert.Equal("notApplicable", plain.Identity);
                    Assert.Equal("notApplicable", plain.Publication); Assert.Equal("notApplicable", plain.RuntimeAuthority);
                }
                Assert.Equal(0, deployment.ReadinessReads);
                var get = await freshClient.GetGenerationAsync(request);
                Assert.Equal(replay.Progress, get.WorkloadSubmission);
                Assert.Equal(initial ? null : (long?)1, get.WorkloadSubmission.HasAppliedGeneration ? get.WorkloadSubmission.AppliedGeneration : null);
                await using (var verify = await factory.CreateDbContextAsync())
                {
                    var intents = await verify.ActionIntents.AsNoTracking().Where(x => x.Generation == request.Generation).ToArrayAsync();
                    Assert.All(intents, x => Assert.Equal(OperatorCredential.Actor(created.Grant.Id), x.Actor));
                    var attempts = await verify.DurableOperationAttempts.AsNoTracking().Where(x => x.Generation == request.Generation).ToArrayAsync();
                    Assert.All(attempts, x => Assert.Equal(OperatorCredential.Actor(created.Grant.Id), x.Actor));
                    Assert.NotEqual(commit.Commit.TechnicalActorId, intents[0].Actor);
                    Assert.Equal(configured ? 2 : 0, await verify.WorkloadConfigurationRecipients.CountAsync());
                    Assert.Single(await verify.GenerationStageCheckpoints.AsNoTracking().Where(x => x.Generation == request.Generation && x.Stage == "WorkloadsSubmitted").ToArrayAsync());
                    Assert.Empty(await verify.GenerationStageCheckpoints.AsNoTracking().Where(x => x.Generation == request.Generation && x.Stage == "RolloutReady").ToArrayAsync());
                    if (lost) Assert.NotEqual(intents[0].Actor, (await verify.ActionObservations.AsNoTracking().SingleAsync()).Actor);
                }
                return new DurableChangeSetCommit(DeploymentChangeSetId.Parse(commit.Commit.ChangeSetId), DeploymentPlanId.Parse(commit.Commit.PlanId),
                    ApplicationDeploymentId.Parse(request.DeploymentId), DeploymentGeneration.Create(request.Generation), DateTimeOffset.Parse(commit.Commit.CommittedAtUtc), commit.Commit.TechnicalActorId);
            });
        Assert.NotNull(scenario.Commit);
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
            var change = (await client.CreateChangeSetAsync(new CreateChangeSetRequest { EnvironmentId = environmentKey,
                Reference = new() { ApplicationRecordId = reference.ApplicationRecordId.ToString(), RevisionNumber = reference.RevisionNumber.Value },
                Precondition = PlanningPrecondition.ExpectedAbsence })).ChangeSet;
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
