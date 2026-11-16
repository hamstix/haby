using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operator_observation_expired_resume_is_atomic_audited_and_concurrent_across_independent_Hosts(bool sameKey)
    {
        var scenario = await RolloutScenario(shared: true, configured: false);
        var deployment = new ConfiguredSubmissionFixture { Pending = true }; var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured: false);
        await SubmitRollout(actions, scenario, deployment); await SubmitRollout(actions, scenario, deployment, "second");
        ((AdjustableTimeProvider)scenario.Time).Advance(TimeSpan.FromSeconds(5));
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + scenario.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        void Compose(IServiceCollection collection)
        {
            collection.AddSingleton(scenario.Time);
            collection.AddSingleton<InternalDurableActions>(p => new(p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
                [deployment, identity], p.GetServices<IProviderConfigurationCodec>(), p.GetServices<IExternalOperationOutputContract>(),
                p.GetRequiredService<DurableActionAdmission>(), p.GetRequiredService<DurableOperationSerialization>(), scenario.Time,
                configurationPreparation: p.GetRequiredService<InternalWorkloadConfiguration>()));
        }
        await using var host = OperatorHostApplication.Build(args, Compose);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        await using var other = OperatorHostApplication.Build(args, Compose);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(other);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); using var peer = GrpcChannel.ForAddress(other.Urls.Single());
        var authenticated = await host.Services.GetRequiredService<IOperatorAccessStore>().AuthorizeAsync(OperatorTestCredentials.Credential(host.Urls.Single()), default);
        var principal = OperatorCredential.Actor(authenticated!.Id);
        var client = OperatorTestCredentials.Client(channel); var second = OperatorTestCredentials.Client(peer, OperatorTestCredentials.Credential(host.Urls.Single()));
        var wait = new GenerationWaitRequest { DeploymentId = scenario.Commit.DeploymentId.ToString(), Generation = scenario.Commit.Generation.Value, RequestId = "expired" };
        var denied = new ManifestOperator.ManifestOperatorClient(channel);
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await denied.WaitGenerationAsync(wait))).StatusCode);
        Assert.Equal("invalidRequest", (await client.WaitGenerationAsync(new() { DeploymentId = "bad", Generation = 1, RequestId = "bad" })).Outcome);
        Assert.Equal("deadline", (await host.Services.GetRequiredService<IOperatorRolloutObservation>().WaitAsync(scenario.Commit.DeploymentId,
            scenario.Commit.Generation, "arrival-expired", "operator", null, scenario.Time.GetUtcNow().AddSeconds(-1), default)).Outcome.ToString().ToLowerInvariant());
        Assert.Equal(0, deployment.ReadinessReads);
        var widened = wait.Clone(); widened.RequestId = "widened"; widened.Settings = new() { MaximumCalls = 513 };
        Assert.Equal("invalidRequest", (await client.WaitGenerationAsync(widened)).Outcome);
        var oversized = wait.Clone(); oversized.RequestId = new string('x', 131073);
        Assert.Equal("invalidRequest", (await client.WaitGenerationAsync(oversized)).Outcome);
        var oversizedResume = new GenerationResumeRequest { DeploymentId = wait.DeploymentId, Generation = wait.Generation, RequestId = "too-many" };
        oversizedResume.Members.AddRange(Enumerable.Range(0, 513).Select(_ => new GenerationResumeMember { Subject = "Workload/api/main", ExpectedAttemptId = Guid.NewGuid().ToString() }));
        Assert.Equal("invalidRequest", (await client.ResumeGenerationAsync(oversizedResume)).Outcome);
        await using (var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            Assert.Empty(await verify.ObservationRecords.AsNoTracking().Where(x => x.Kind == "Request").ToArrayAsync());
        var expired = await client.WaitGenerationAsync(wait);
        Assert.False(expired.CurrentReady); Assert.Equal(2, deployment.ReadinessReads);
        Assert.All(expired.Members, x => Assert.Equal("Expired", x.Budget));
        var resume = new GenerationResumeRequest { DeploymentId = wait.DeploymentId, Generation = wait.Generation, RequestId = "renew" };
        resume.Members.AddRange(expired.Members.Select(x => new GenerationResumeMember { Subject = x.Subject, ExpectedAttemptId = x.AttemptId }));
        foreach (var forbidden in new[] { new Metadata(), new Metadata { { "authorization", "Bearer " + ConfigurationDeliveryCredential.Create(Guid.CreateVersion7()) } } })
        {
            Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await denied.WaitGenerationAsync(wait, forbidden))).StatusCode);
            Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await denied.ResumeGenerationAsync(resume, forbidden))).StatusCode);
        }
        var mixed = resume.Clone(); mixed.RequestId = "mixed"; mixed.Members[1].ExpectedAttemptId = Guid.NewGuid().ToString();
        Assert.Equal("conflict", (await client.ResumeGenerationAsync(mixed)).Outcome);
        await using (var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            Assert.Equal(2, await verify.ObservationAttempts.CountAsync());
        var racing = resume.Clone(); if (!sameKey) racing.RequestId = "other-renew";
        var results = await Task.WhenAll(client.ResumeGenerationAsync(resume).ResponseAsync, second.ResumeGenerationAsync(racing).ResponseAsync);
        Assert.Equal(sameKey ? 2 : 1, results.Count(x => x.Outcome == "resumed"));
        Assert.Equal(sameKey ? 1 : 0, results.Count(x => x.Replayed));
        Assert.Equal(sameKey ? 0 : 1, results.Count(x => x.Outcome == "conflict"));
        Assert.All(results.Where(x => x.Outcome == "resumed"), x => Assert.False(x.CurrentReady));
        Assert.Equal(2, deployment.ReadinessReads); Assert.Equal(2, deployment.Writes);
        // Task.WhenAll retains request indexes; either independent Host can win.
        var winner = Array.FindIndex(results, x => x.Outcome == "resumed");
        var requests = new[] { resume, racing };
        var replay = await client.ResumeGenerationAsync(requests[winner]);
        var saved = results[winner].Clone(); saved.Replayed = true;
        Assert.Equal(saved, replay);
        if (!sameKey)
        {
            var stale = await client.ResumeGenerationAsync(requests[1 - winner]);
            Assert.Equal("conflict", stale.Outcome); Assert.False(stale.Replayed);
        }
        var duplicate = resume.Clone(); duplicate.RequestId = "duplicate"; duplicate.Members.Add(duplicate.Members[0].Clone());
        Assert.Equal("invalidRequest", (await client.ResumeGenerationAsync(duplicate)).Outcome);
        var actor = await host.Services.GetRequiredService<IOperatorAccessStore>().CreateAsync("another operator", "owner", null, default);
        var foreign = new ManifestOperator.ManifestOperatorClient(channel);
        Assert.Equal("conflict", (await foreign.WaitGenerationAsync(wait, new Metadata { { "authorization", "Bearer " + actor.Credential } })).Outcome);
        await using (var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            var attempts = await verify.ObservationAttempts.AsNoTracking().Where(x => x.Ordinal == 2).ToArrayAsync();
            Assert.Equal(2, attempts.Length);
            Assert.All(attempts, x =>
            {
                Assert.Equal(Guid.Parse(expired.Members.Single(original => original.Subject == x.Subject).AttemptId), x.Previous);
                Assert.Equal(principal, x.Actor);
                Assert.Equal(TimeSpan.FromSeconds(x.BudgetSeconds), x.Deadline - x.StartedAt);
            });
            Assert.Equal(4, await verify.ObservationAttempts.CountAsync());
            Assert.Equal(2, deployment.ReadinessReads); Assert.Equal(2, deployment.Writes);
            Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.DoesNotContain(await verify.ObservationRecords.AsNoTracking().Where(x => x.Kind == "Request").Select(x => x.InputJson).ToArrayAsync(), x => x.Contains("observer", StringComparison.Ordinal));
        }
        await host.StopAsync(); await other.StopAsync();
    }
    [Fact]
    public async Task Operator_observation_revocation_stops_new_reads_preserves_replay_and_refuses_new_resume()
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity); await SubmitRollout(actions, scenario, deployment);
        var facade = new PostgreSqlOperatorRolloutObservation(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
            ConfigurationPreparation(services).Preparation, actions, new(PollMilliseconds: 100), scenario.Time, deployment.Contract.Action, identity.Contract.Action);
        ValueTask<GenerationReadinessResult> Wait(string key, Haby.Application.GenerationObservationSettings? settings = null) => facade.WaitAsync(
            scenario.Commit.DeploymentId, scenario.Commit.Generation, key, "public-operator", settings, null, default);
        var publication = Publications(services, identity);
        var request = (await publication.PreparationRequestsAsync(scenario.Commit, "tester", default))[0];
        var before = (await publication.ReadAsync(request.Key, default))!;
        var ready = await Wait("ready", new(MaximumRounds: 1)); Assert.True(ready.CurrentReady); Assert.True(ready.CurrentApplied);
        deployment.Pending = true;
        deployment.BeforeRead = async _ => { await publication.ApplyAsync(Revoke(request, before.CurrentPermission!.Token), default); };
        var refused = await Wait("revoke-during-read", new(MaximumRounds: 3));
        Assert.Equal(2, deployment.ReadinessReads); // One earlier Ready read plus the already-dispatched Pending read.
        Assert.False(refused.CurrentReady); Assert.Equal(ready.SavedReady, refused.SavedReady);
        Assert.Equal("runtime-authority-unavailable", Assert.Single(refused.Members!).Code);
        var replay = await Wait("ready", new(MaximumRounds: 1));
        Assert.True(replay.Replayed); Assert.True(replay.OriginalRequestReady); Assert.False(replay.CurrentReady);
        Assert.Equal(2, deployment.ReadinessReads);
        ((AdjustableTimeProvider)scenario.Time).Advance(TimeSpan.FromSeconds(3));
        var member = Assert.Single(ready.Members!);
        var resumed = await facade.ResumeAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, "resume-revoked", "public-operator",
            [new(member.Subject, member.Attempt!.Value)], null, default);
        Assert.Equal(GenerationReadinessOutcome.Refused, resumed.Outcome);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, await verify.ObservationAttempts.CountAsync());
        Assert.Equal(1, deployment.Writes); Assert.Equal(1, identity.Writes);
        Assert.Equal(scenario.Commit.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Fact]
    public async Task Operator_observation_request_deadline_includes_generation_gate_and_expired_arrival_starts_no_reads()
    {
        var scenario = await RolloutScenario(configured: false); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured: false); await SubmitRollout(actions, scenario, deployment);
        var facade = new PostgreSqlOperatorRolloutObservation(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
            ConfigurationPreparation(services).Preparation, actions, new(), scenario.Time, deployment.Contract.Action, identity.Contract.Action);
        var gate = services.GetRequiredService<DurableOperationSerialization>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = gate.RunAsync(new(scenario.Commit.DeploymentId, scenario.Commit.Generation, OperationId.Create("held")),
            async () => { entered.SetResult(); await release.Task; return true; }, default).AsTask();
        await entered.Task;
        try
        {
            var blocked = await facade.WaitAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, "gate-expired", "public-operator",
                new(RequestMilliseconds: 100), null, default);
            Assert.Equal(GenerationReadinessOutcome.Deadline, blocked.Outcome); Assert.Equal(0, deployment.ReadinessReads);
        }
        finally { release.SetResult(); await holding; }
        var expired = await facade.WaitAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, "arrival-expired", "public-operator",
            null, scenario.Time.GetUtcNow().AddSeconds(-1), default);
        Assert.Equal(GenerationReadinessOutcome.Deadline, expired.Outcome); Assert.Equal(0, deployment.ReadinessReads);
        var current = await facade.WaitAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, "fresh", "public-operator", new(MaximumRounds: 1), null, default);
        Assert.True(current.CurrentReady); Assert.Equal(1, deployment.ReadinessReads);
    }

}
