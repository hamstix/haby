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
        var scenario = await InternalScenarioAsync(initialOnly: initial,
            configureManifest: configured ? e => ConfiguredDocuments(e, secondDocument: true) : null,
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
                Assert.Equal(lost ? "revoked" : configured ? "available" : "notApplicable", Assert.Single(replay.Progress.Members).RuntimeAuthority);
                Assert.Equal(1, deployment.Writes); Assert.Equal(configured ? 1 : 0, identity.Writes);
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
}
