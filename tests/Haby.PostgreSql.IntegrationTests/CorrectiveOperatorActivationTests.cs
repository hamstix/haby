using System.Text.Json.Nodes;
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
    [Fact]
    public async Task Authenticated_corrective_RPC_plans_commits_submits_and_Applies_failed_initial_generation()
    {
        JsonNode? envelope = null;
        var scenario = await InternalScenarioAsync(combinedInitial: true, configureManifest: e =>
        { ConfiguredDocuments(e); envelope = e.DeepClone(); }, acceptGeneration: async (setup, environment, reference) =>
        {
            DurableChangeSetCommit? accepted = null;
            await HostFailureDiagnostics.RunAsync(async defer =>
            {
                var factory = setup.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
                string connection, key;
                await using (var read = await factory.CreateDbContextAsync())
                {
                    connection = read.Database.GetConnectionString()!;
                    key = (await read.Environments.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(environment.ToString()))).EnvironmentId;
                }
                var firstHost = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
                defer(firstHost.DisposeAsync);
                await OperatorTestCredentials.StartWithIssuedOperatorAsync(firstHost);
                var firstChannel = GrpcChannel.ForAddress(firstHost.Urls.Single());
                defer(() => { firstChannel.Dispose(); return ValueTask.CompletedTask; });
                var firstClient = OperatorTestCredentials.Client(firstChannel);
                var change = (await firstClient.CreateChangeSetAsync(new() { EnvironmentId = key,
                    Reference = new() { ApplicationRecordId = reference.ApplicationRecordId.ToString(), RevisionNumber = reference.RevisionNumber.Value },
                    Precondition = PlanningPrecondition.ExpectedAbsence })).ChangeSet;
                var plan = await firstClient.CreatePlanAsync(new() { ChangeSetId = change.ChangeSetId });
                Assert.Equal(PlanningStatus.Planned, plan.Status);
                var committed = await firstClient.CommitDesiredStateAsync(new() { ChangeSetId = change.ChangeSetId, PlanId = plan.Plan.PlanId });
                Assert.Equal(CommitStatus.Committed, committed.Status);
                accepted = new(DeploymentChangeSetId.Parse(committed.Commit.ChangeSetId), DeploymentPlanId.Parse(committed.Commit.PlanId),
                    ApplicationDeploymentId.Parse(committed.Commit.DeploymentId), DeploymentGeneration.Create(committed.Commit.Generation),
                    DateTimeOffset.Parse(committed.Commit.CommittedAtUtc), committed.Commit.TechnicalActorId);
            }, () => HostFailurePhase.Catalog, () => null);
            return accepted!;
        });
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var resources = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, default);
        Assert.NotNull(resources.Progress!.ResourcesReady); Assert.Null(resources.Progress.AppliedGeneration);
        var deployment = new ConfiguredSubmissionFixture { Pending = true, ProgressDeadlineExceeded = true };
        var identity = new IdentityFixture(); var update = new UpdateSubmissionFixture();
        var initial = await PrepareRollout(services, scenario, deployment, identity);
        await SubmitRollout(initial, scenario, deployment);
        var failed = await initial.ObserveRolloutAsync(scenario.Commit, "rpc-failed-initial", OneRound, default);
        Assert.False(failed.Ready); Assert.Equal("progress-deadline", Assert.Single(failed.Members).Observation.Code);
        envelope!["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = "registry.example/app:rpc-corrected";
        var revision = await ImportUpdate(services, envelope, "rpc-correction");
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        string environmentKey;
        await using (var read = await contexts.CreateDbContextAsync())
            environmentKey = (await read.Environments.AsNoTracking().SingleAsync()).EnvironmentId;
        await HostFailureDiagnostics.RunAsync(async defer =>
        {
            void Compose(IServiceCollection collection) => collection.AddSingleton<InternalDurableActions>(p => new(
                p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), [deployment, identity, update],
                p.GetServices<IProviderConfigurationCodec>(), p.GetServices<IExternalOperationOutputContract>(), p.GetRequiredService<DurableActionAdmission>(),
                p.GetRequiredService<DurableOperationSerialization>(), p.GetRequiredService<TimeProvider>(),
                configurationPreparation: p.GetRequiredService<InternalWorkloadConfiguration>()));
            var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], Compose);
            defer(host.DisposeAsync);
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            var channel = GrpcChannel.ForAddress(host.Urls.Single());
            defer(() => { channel.Dispose(); return ValueTask.CompletedTask; });
            var raw = new ManifestOperator.ManifestOperatorClient(channel);
            var request = new CreateCorrectiveChangeSetRequest { EnvironmentId = environmentKey,
                Reference = new() { ApplicationRecordId = revision.ApplicationRecordId.ToString(), RevisionNumber = revision.RevisionNumber.Value },
                DeploymentId = scenario.Commit.DeploymentId.ToString(), ExpectedIncompleteDesiredGeneration = scenario.Commit.Generation.Value,
                RequestId = Guid.CreateVersion7().ToString() };
            foreach (var forbidden in new[] { new Metadata(), new Metadata { { "authorization", "Bearer " + ConfigurationDeliveryCredential.Create(Guid.CreateVersion7()) } } })
                Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await raw.CreateCorrectiveChangeSetAsync(request, forbidden))).StatusCode);
            var client = OperatorTestCredentials.Client(channel);
            var change = (await client.CreateCorrectiveChangeSetAsync(request)).ChangeSet;
            Assert.NotNull(change);
            var plan = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSetId });
            Assert.Equal(PlanningStatus.Planned, plan.Status);
            Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(new() { PlanId = plan.Plan.PlanId })).Status);
            var commit = await client.CommitDesiredStateAsync(new() { ChangeSetId = change.ChangeSetId, PlanId = plan.Plan.PlanId });
            Assert.Equal(CommitStatus.Committed, commit.Status);
            var accepted = new GenerationRequest { DeploymentId = commit.Commit.DeploymentId, Generation = commit.Commit.Generation };
            Assert.Equal(scenario.Commit.Generation.Value + 1, accepted.Generation);
            await host.DisposeAsync();
            var restarted = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], Compose);
            defer(restarted.DisposeAsync);
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(restarted);
            var resumedChannel = GrpcChannel.ForAddress(restarted.Urls.Single());
            defer(() => { resumedChannel.Dispose(); return ValueTask.CompletedTask; });
            var resumed = OperatorTestCredentials.Client(resumedChannel);
            Assert.Equal("submitted", (await resumed.SubmitGenerationAsync(accepted)).Outcome);
            var ready = await resumed.WaitGenerationAsync(new() { DeploymentId = accepted.DeploymentId, Generation = accepted.Generation,
                RequestId = "rpc-corrected-wait", Settings = new() { MaximumRounds = 1 } });
            Assert.Equal("observed", ready.Outcome); Assert.True(ready.CurrentReady); Assert.True(ready.CurrentApplied); Assert.True(ready.RetirementCompleted);
            Assert.Equal(1, update.Writes); Assert.Equal(1, deployment.Writes);
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.Equal(accepted.Generation, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Single(await verify.ResourceManagement.AsNoTracking().ToArrayAsync());
            Assert.False(await verify.GenerationStageCheckpoints.AnyAsync(x => x.Generation == scenario.Commit.Generation.Value && x.Stage == InitialRetirementCompletion.Stage));
            var originalResource = ResourcesReadyCheckpointCodec.Encode(resources.Progress.ResourcesReady!);
            var source = await verify.DeploymentGenerations.AsNoTracking().SingleAsync(x => x.Generation == accepted.Generation);
            Assert.Equal(originalResource, DurableOperationGraphCodec.Decode(source.GraphJson).Correction!.Document.ResourceProof);
            Assert.Equal(commit.Commit.TechnicalActorId, source.ActorId);
        }, () => HostFailurePhase.Catalog, () => null);
    }
}
