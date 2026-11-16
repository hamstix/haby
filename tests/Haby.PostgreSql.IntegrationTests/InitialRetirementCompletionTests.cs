using System.Text.Json;
using Haby.Application;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Public_wait_completes_initial_retirement_and_Applied_atomically_without_external_writes(bool initial, bool configured)
    {
        var scenario = await RolloutScenario(initial, configured: configured);
        var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured);
        await SubmitRollout(actions, scenario, deployment);
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var facade = BatchFacade(services, scenario, actions, deployment, identity);
        var ready = await facade.WaitAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation,
            "complete-initial", "operator", new(MaximumRounds: 1), null, default);
        Assert.True(ready.CurrentReady); Assert.True(ready.CurrentApplied, ready.CompletionReason);
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            Assert.Equal(scenario.Commit.Generation.Value, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
            Assert.Single(await verify.GenerationStageCheckpoints.AsNoTracking().Where(x => x.Generation == scenario.Commit.Generation.Value && x.Stage == "RetirementCompleted").ToArrayAsync());
            Assert.Equal(configured ? 1 : 0, await verify.RuntimeIdentityBindings.CountAsync());
            _ = await InternalGenerationAuthority.RequireReadAsync(verify, scenario.Commit, default);
        }
        var replay = await facade.WaitAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation,
            "complete-initial", "operator", new(MaximumRounds: 1), null, default);
        Assert.True(replay.Replayed); Assert.False(replay.CurrentReady);
        Assert.Equal(1, deployment.ReadinessReads); Assert.Equal(1, deployment.Writes);
        Assert.Equal(configured ? 1 : 0, identity.Writes);
        var fresh = await facade.WaitAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation,
            "confirm-applied", "operator", new(MaximumRounds: 1), null, default);
        Assert.True(fresh.CurrentApplied); Assert.False(fresh.Replayed); Assert.Equal(ready.Retirement, fresh.Retirement);
        Assert.Equal(2, deployment.ReadinessReads); Assert.Equal(1, deployment.Writes);
    }

    [Fact]
    public async Task Completion_rechecks_revocation_after_the_last_API_read_without_republishing()
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity); await SubmitRollout(actions, scenario, deployment);
        var publication = Publications(services, identity);
        var request = (await publication.PreparationRequestsAsync(scenario.Commit, "tester", default))[0];
        var before = (await publication.ReadAsync(request.Key, default))!;
        deployment.BeforeRead = async _ => await publication.ApplyAsync(Revoke(request, before.CurrentPermission!.Token), default);
        var result = await BatchFacade(services, scenario, actions, deployment, identity).WaitAsync(scenario.Commit.DeploymentId,
            scenario.Commit.Generation, "revoke-before-finalization", "operator", new(MaximumRounds: 1), null, default);
        Assert.True(result.CurrentReady); Assert.False(result.CurrentApplied); Assert.Null(result.Retirement);
        Assert.Equal("current-authority-unavailable", result.CompletionReason);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.False(await verify.GenerationStageCheckpoints.AnyAsync(x => x.Stage == "RetirementCompleted"));
        Assert.Equal("Revoked", (await verify.RuntimeConfigurationPermissions.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1, deployment.Writes); Assert.Equal(1, identity.Writes);
    }

    [Fact]
    public async Task Historical_ready_request_cannot_acquire_completion_and_new_wait_binds_its_own_round()
    {
        var scenario = await RolloutScenario(configured: false); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity, configured: false); await SubmitRollout(actions, scenario, deployment);
        var options = new RolloutOptions(MaximumRounds: 1);
        var observed = await actions.ObserveRolloutAsync(scenario.Commit, "before-completion", options, default);
        Assert.True(observed.Ready); Assert.False(observed.Applied);
        var facade = new PostgreSqlOperatorRolloutObservation(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
            ConfigurationPreparation(services).Preparation, actions, new(), scenario.Time, deployment.Contract.Action, identity.Contract.Action);
        var replay = await facade.WaitAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, "before-completion", "observer",
            new(MaximumRounds: 1), null, default);
        Assert.True(replay.Replayed); Assert.False(replay.CurrentApplied); Assert.False(replay.CurrentReady);
        var complete = await facade.WaitAsync(scenario.Commit.DeploymentId, scenario.Commit.Generation, "fresh-completion", "operator",
            new(MaximumRounds: 1), null, default);
        Assert.True(complete.CurrentApplied); Assert.Equal(observed.SavedReady, complete.SavedReady);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        var source = await verify.DeploymentGenerations.AsNoTracking().SingleAsync();
        var proof = await InitialRetirementCompletion.RequireProofAsync(verify, source, default);
        Assert.Equal("fresh-completion", proof.Request);
        foreach (var member in proof.FreshReady.Members)
            Assert.Equal("fresh-completion", (await verify.ObservationRecords.AsNoTracking().SingleAsync(x => x.Id == member.Observation)).Request);
        Assert.Equal(2, deployment.ReadinessReads); Assert.Equal(1, deployment.Writes);
    }

    [Fact]
    public async Task Completed_runtime_identity_and_delivery_survive_restart_but_revocation_still_denies_access()
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        RuntimePublicationRequest request; RuntimePublicationResult published;
        await using (var services = InternalServices(scenario.Connection, scenario.Time))
        {
            var actions = await PrepareRollout(services, scenario, deployment, identity); await SubmitRollout(actions, scenario, deployment);
            var publication = Publications(services, identity);
            request = (await publication.PreparationRequestsAsync(scenario.Commit, "tester", default))[0];
            published = (await publication.ReadAsync(request.Key, default))!;
            var result = await BatchFacade(services, scenario, actions, deployment, identity).WaitAsync(scenario.Commit.DeploymentId,
                scenario.Commit.Generation, "runtime-complete", "operator", new(MaximumRounds: 1), null, default);
            Assert.True(result.CurrentApplied);
        }
        await using var restarted = InternalServices(scenario.Connection, scenario.Time);
        var contexts = restarted.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var actionsAfterRestart = ConfiguredActions(restarted, deployment, identity);
        var trust = (await new PostgreSqlRuntimeIdentityStore(contexts, actionsAfterRestart).ReadAsync(request.Binding, default))!;
        Assert.NotNull(trust); Assert.False(trust.Revoked);
        var delivery = new PostgreSqlRuntimeConfigurationStore(contexts);
        var revision = Haby.Domain.ConfigurationDocumentRevisionId.Parse(request.Revision!.Value.ToString());
        var authenticated = new RuntimeAuthenticatedContext(trust.Binding, trust.Environment, trust.ManagedWorkload, trust.Provider);
        var authority = await delivery.AuthorizeAsync(authenticated, revision, default);
        Assert.NotNull(authority); Assert.NotNull(await delivery.ReadAsync(authority, default));
        await PublicConfigurationRefusesAsync(scenario);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ConfigurationPreparation(restarted).Preparation.PrepareAsync(scenario.Commit, "tester", default));
        await Publications(restarted, identity).ApplyAsync(Revoke(request, published.CurrentPermission!.Token), default);
        Assert.Null(await delivery.AuthorizeAsync(authenticated, revision, default));
        Assert.NotNull(await new PostgreSqlRuntimeIdentityStore(contexts, actionsAfterRestart).ReadAsync(request.Binding, default));
        var replay = await BatchFacade(restarted, scenario, actionsAfterRestart, deployment, identity).WaitAsync(scenario.Commit.DeploymentId,
            scenario.Commit.Generation, "runtime-complete", "operator", new(MaximumRounds: 1), null, default);
        Assert.True(replay.Replayed); Assert.True(replay.OriginalRequestApplied); Assert.False(replay.CurrentApplied);
        Assert.Equal(1, deployment.Writes); Assert.Equal(1, identity.Writes);
    }

    [Theory]
    [InlineData("omitted-all")]
    [InlineData("omitted-one")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("mismatched-binding")]
    [InlineData("mismatched-receipt")]
    [InlineData("mismatched-token")]
    [InlineData("canonical-reference")]
    public async Task Completed_proof_requires_exact_configured_publications_and_canonical_evidence(string fault)
    {
        var scenario = await RolloutScenario(shared: true); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity);
        await SubmitRollout(actions, scenario, deployment); await SubmitRollout(actions, scenario, deployment, "second");
        Assert.True((await BatchFacade(services, scenario, actions, deployment, identity).WaitAsync(scenario.Commit.DeploymentId,
            scenario.Commit.Generation, "publication-proof", "operator", new(MaximumRounds: 1), null, default)).CurrentApplied);
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        await using (var corrupt = await contexts.CreateDbContextAsync())
        {
            var source = await corrupt.DeploymentGenerations.AsNoTracking().SingleAsync();
            var proof = await InitialRetirementCompletion.RequireProofAsync(corrupt, source, default);
            Assert.Equal(2, proof.Publications.Length);
            var first = proof.Publications[0]; var second = proof.Publications[1];
            var replacement = first;
            if (fault == "canonical-reference")
            {
                var original = InternalRuntimePublication.ReadReceipt(await corrupt.RuntimePublicationReceipts.AsNoTracking().SingleAsync(x => x.Id == first.Receipt));
                var key = "alternate-" + Guid.NewGuid().ToString("N"); var id = Guid.NewGuid();
                var alternate = original with { Id = id, Request = original.Request with { Key = key }, After = original.After! with { LastRequest = key } };
                var payload = RuntimePublicationCodec.Encode(alternate);
                // A structurally valid alternative reference must still be bound by the saved catalog hash.
                corrupt.RuntimePublicationReceipts.Add(new() { Id = id, RequestKey = key, BindingId = first.Binding, RevisionId = first.Revision, Payload = payload });
                await corrupt.SaveChangesAsync();
                replacement = first with { Request = key, Receipt = id, ReceiptHash = ResourcesReadyCheckpointCodec.Hash(payload) };
            }
            CompletionPublication[] changed = fault switch
            {
                "omitted-all" => [],
                "omitted-one" => [first],
                "duplicate" => [first, first],
                "extra" => [first, second, first with { Permission = Guid.NewGuid() }],
                "mismatched-binding" => [first with { Binding = second.Binding }, second],
                "mismatched-receipt" => [first with { Receipt = second.Receipt }, second],
                "mismatched-token" => [first with { PermissionToken = Guid.NewGuid() }, second],
                "canonical-reference" => [replacement, second],
                _ => throw new InvalidOperationException()
            };
            Assert.Equal(proof.CatalogHash, await InitialRetirementCompletion.CatalogHashAsync(corrupt, source, proof.Publications.Reverse().ToArray(), default));
            if (fault == "canonical-reference")
                Assert.NotEqual(proof.CatalogHash, await InitialRetirementCompletion.CatalogHashAsync(corrupt, source, changed, default));
            var row = await corrupt.GenerationStageCheckpoints.SingleAsync(x => x.Stage == "RetirementCompleted");
            row.EvidenceJson = JsonSerializer.Serialize(proof with { Publications = changed }, InitialCompletionMetadata.Default.InitialCompletionProof);
            row.EvidenceFingerprint = ResourcesReadyCheckpointCodec.Hash(row.EvidenceJson);
            await corrupt.Database.OpenConnectionAsync();
            await corrupt.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
            await corrupt.GenerationStageCheckpoints.Where(x => x.Id == row.Id).ExecuteUpdateAsync(set =>
                set.SetProperty(x => x.EvidenceJson, row.EvidenceJson).SetProperty(x => x.EvidenceFingerprint, row.EvidenceFingerprint));
        }
        await using (var verify = await contexts.CreateDbContextAsync())
        {
            var source = await verify.DeploymentGenerations.AsNoTracking().SingleAsync();
            await Assert.ThrowsAsync<InvalidDataException>(() => InitialRetirementCompletion.RequireProofAsync(verify, source, default));
        }
        Assert.Equal(2, deployment.Writes); Assert.Equal(2, identity.Writes);
    }

    [Theory]
    [InlineData("ResourcesReady")]
    [InlineData("WorkloadsSubmitted")]
    [InlineData("RolloutReady")]
    [InlineData("nonempty-inventory")]
    [InlineData("artifact")]
    [InlineData("recipient")]
    [InlineData("identity")]
    [InlineData("old-input")]
    [InlineData("missing-inventory")]
    public async Task Missing_or_corrupt_completion_prerequisites_never_advance_Applied(string fault)
    {
        var scenario = await RolloutScenario(); var deployment = new ConfiguredSubmissionFixture(); var identity = new IdentityFixture();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var actions = await PrepareRollout(services, scenario, deployment, identity); await SubmitRollout(actions, scenario, deployment);
        _ = await actions.ObserveRolloutAsync(scenario.Commit, "historical-only", OneRound, default);
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        await using (var corrupt = await contexts.CreateDbContextAsync())
        {
            await corrupt.Database.OpenConnectionAsync();
            // Deliberately corrupt only this test-owned catalog, using the existing
            // session-local fault-injection convention; normal writers retain immutable guards.
            if (fault is "old-input" or "missing-inventory" or "nonempty-inventory")
                await corrupt.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
            var sql = fault switch
            {
                "ResourcesReady" => "UPDATE generation_stage_checkpoints SET evidence_fingerprint = repeat('0',64) WHERE stage = 'ResourcesReady'",
                "WorkloadsSubmitted" => "UPDATE generation_stage_checkpoints SET evidence_fingerprint = repeat('0',64) WHERE stage = 'WorkloadsSubmitted'",
                "RolloutReady" => "UPDATE generation_stage_checkpoints SET evidence_fingerprint = repeat('0',64) WHERE stage = 'RolloutReady'",
                "nonempty-inventory" => "UPDATE application_deployment_generations SET graph_json = jsonb_set(graph_json::jsonb, '{Retirement,ObsoleteObjects}', '[\"obsolete\"]'::jsonb)::text",
                "artifact" => "UPDATE configuration_document_revisions SET payload = '{bad'",
                "recipient" => "DELETE FROM workload_configuration_recipients",
                "identity" => "UPDATE owned_objects SET payload = '{}'",
                "old-input" => "UPDATE application_deployment_generations SET snapshot_format_version = 4",
                "missing-inventory" => "UPDATE application_deployment_generations SET graph_json = (graph_json::jsonb - 'Retirement')::text",
                _ => throw new InvalidOperationException()
            };
            await corrupt.Database.ExecuteSqlRawAsync(sql.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
        }
        var result = await BatchFacade(services, scenario, actions, deployment, identity).WaitAsync(scenario.Commit.DeploymentId,
            scenario.Commit.Generation, "refuse-corrupt", "operator", new(MaximumRounds: 1), null, default);
        Assert.False(result.CurrentApplied); Assert.Null(result.Retirement);
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.False(await verify.GenerationStageCheckpoints.AnyAsync(x => x.Stage == "RetirementCompleted"));
        Assert.Equal(1, deployment.Writes); Assert.Equal(1, identity.Writes);
        if (fault is "old-input" or "missing-inventory" or "nonempty-inventory") Assert.Equal("unsupported-inventory", result.CompletionReason);
    }
}
