using Haby.Application;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    private static InternalRuntimePublication Publications(ServiceProvider services, IInternalActionHandler handler) =>
        new(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), ConfigurationPreparation(services).Preparation,
            IdentityActions(services, handler), services.GetRequiredService<TimeProvider>());
    private static RuntimePublicationRequest Revoke(RuntimePublicationRequest published, Guid? token, string? key = null) =>
        published with { Key = key ?? "revoke-" + Guid.NewGuid().ToString("N"), Operation = RuntimePublicationOperation.RevokePermission, ExpectedToken = token, Origin = null };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Runtime_publication_uses_actual_initial_and_mixed_preparation_and_survives_restore(bool initial)
    {
        var scenario = await InternalScenarioAsync(initialOnly: initial, configureManifest: e => ConfiguredDocuments(e, secondDocument: true, shared: true));
        var handler = new IdentityFixture(); RuntimePublicationRequest[] requests; RuntimePublicationResult[] first;
        await using (var services = InternalServices(scenario.Connection, scenario.Time))
        {
            await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
            Assert.Equal(2, (await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default)).Count);
            var publication = Publications(services, handler);
            requests = await publication.PreparationRequestsAsync(scenario.Commit, "tester", default);
            Assert.Equal(4, requests.Length); Assert.Equal(2, requests.Select(x => x.Binding).Distinct().Count());
            var batch = await new InternalRuntimePublicationPreparation(publication).PrepareAsync(scenario.Commit, "tester", default);
            Assert.Null(batch.Failed); Assert.Null(batch.Reason); first = batch.Completed.ToArray();
            Assert.All(first, x => Assert.Equal(RuntimePublicationOutcome.Published, x.Receipt.Outcome));
            foreach (var request in requests)
            {
                var authority = await publication.AuthorizeMetadataAsync(request.Binding, request.Revision!.Value, default);
                Assert.NotNull(authority); Assert.Equal(request.Origin!.Workload, authority.Workload);
            }
            var revocation = await publication.ApplyAsync(Revoke(requests[0], first[0].CurrentPermission!.Token), default);
            Assert.Equal("Revoked", revocation.CurrentPermission!.Status);
            Assert.NotNull(await publication.AuthorizeMetadataAsync(requests[1].Binding, requests[1].Revision!.Value, default));
        }
        var restored = await RestoreRuntimeCatalog(scenario.Connection);
        await using var restarted = InternalServices(restored, scenario.Time);
        await restarted.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var saved = await Publications(restarted, handler).ApplyAsync(requests[0], default);
        Assert.Equal(first[0].Receipt, saved.Receipt); Assert.Equal("Revoked", saved.CurrentPermission!.Status);
        Assert.Null(await Publications(restarted, handler).AuthorizeMetadataAsync(requests[0].Binding, requests[0].Revision!.Value, default));
        await using var verify = await restarted.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(4, await verify.RuntimeConfigurationPermissions.CountAsync());
        Assert.Equal(5, await verify.RuntimePublicationReceipts.CountAsync()); Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
        Assert.Equal(initial ? null : (long?)1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Fact]
    public async Task Old_publication_and_automatic_preparation_cannot_repair_revoke_or_reactivate_binding()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e));
        var handler = new IdentityFixture(); await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default);
        var publication = Publications(services, handler); var request = Assert.Single(await publication.PreparationRequestsAsync(scenario.Commit, "tester", default));
        Assert.Null(await publication.AuthorizeMetadataAsync(request.Binding, request.Revision!.Value, default));
        var first = await publication.ApplyAsync(request, default);
        var alreadyAuthorized = await publication.AuthorizeMetadataAsync(request.Binding, request.Revision.Value, default);
        Assert.NotNull(alreadyAuthorized);
        var revoked = await publication.ApplyAsync(Revoke(request, first.CurrentPermission!.Token), default);
        var old = await publication.ApplyAsync(request, default);
        Assert.Equal(first.Receipt, old.Receipt); Assert.Equal(revoked.CurrentPermission, old.CurrentPermission);
        Assert.Null(await publication.AuthorizeMetadataAsync(request.Binding, request.Revision.Value, default));
        Assert.Equal(request.Revision.Value, alreadyAuthorized.Revision); // An earlier context is not retroactively erased.
        var batch = await new InternalRuntimePublicationPreparation(publication).PrepareAsync(scenario.Commit, "tester", default);
        Assert.NotNull(batch.Failed); Assert.Empty(batch.Completed);
        var republished = await publication.ApplyAsync(request with { Key = "explicit-republish", ExpectedToken = revoked.CurrentPermission!.Token }, default);
        Assert.Equal(RuntimePublicationOutcome.Published, republished.Receipt.Outcome);
        var bindingRevoke = new RuntimePublicationRequest("binding-revoke", RuntimePublicationOperation.RevokeBinding, request.Binding,
            null, republished.CurrentBinding.Token, "operator");
        var blocked = await publication.ApplyAsync(bindingRevoke, default);
        var identityReplay = Assert.Single(await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default));
        Assert.Equal(InternalActionOutcome.Refused, identityReplay.Result.Outcome); Assert.Equal("Revoked", identityReplay.CurrentBinding!.Status);
        Assert.Equal(1, handler.Writes);
        Assert.Null(await publication.AuthorizeMetadataAsync(request.Binding, request.Revision.Value, default));
        var attempt = await publication.ApplyAsync(request with { Key = "cannot-override-binding", ExpectedToken = republished.CurrentPermission!.Token }, default);
        Assert.Equal(RuntimePublicationOutcome.BindingRevoked, attempt.Receipt.Outcome);
        Assert.Equal(blocked.CurrentBinding, attempt.CurrentBinding);
        Assert.Equal(first.Receipt, (await publication.ReadAsync(request.Key, default))!.Receipt);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Single(await verify.RuntimeIdentityBindings.AsNoTracking().ToArrayAsync()); Assert.Single(await verify.OwnedObjects.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.ManagedWorkloads.AsNoTracking().ToArrayAsync()); Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
        Assert.Equal("Revoked", (await verify.RuntimeIdentityBindings.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(first.Receipt.Request.Origin, RuntimePublicationCodec.Origin((await verify.RuntimeConfigurationPermissions.AsNoTracking().SingleAsync()).OriginPayload));
        var restored = await RestoreRuntimeCatalog(scenario.Connection);
        await using var restarted = InternalServices(restored, scenario.Time);
        var restoredReplay = await Publications(restarted, handler).ApplyAsync(request, default);
        Assert.Equal(first.Receipt, restoredReplay.Receipt); Assert.Equal(blocked.CurrentBinding, restoredReplay.CurrentBinding);
        Assert.Null(await Publications(restarted, handler).AuthorizeMetadataAsync(request.Binding, request.Revision.Value, default));
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("revision")]
    [InlineData("component")]
    [InlineData("workload")]
    [InlineData("environment")]
    [InlineData("fingerprint")]
    [InlineData("checkpoint")]
    [InlineData("origin")]
    public async Task New_publication_refuses_foreign_or_changed_exact_authority_without_a_permission(string mismatch)
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e));
        var handler = new IdentityFixture(); await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default);
        var publication = Publications(services, handler); var request = Assert.Single(await publication.PreparationRequestsAsync(scenario.Commit, "tester", default));
        var changed = mismatch switch
        {
            "binding" => request with { Binding = Guid.NewGuid() },
            "revision" => request with { Revision = Guid.NewGuid() },
            "component" => request with { Origin = request.Origin! with { Component = "foreign" } },
            "workload" => request with { Origin = request.Origin! with { Workload = "foreign" } },
            "environment" => request with { Origin = request.Origin! with { Environment = Guid.NewGuid() } },
            "fingerprint" => request with { Origin = request.Origin! with { Fingerprint = "changed" } },
            "checkpoint" => request with { Origin = request.Origin! with { Checkpoint = Guid.NewGuid() } },
            _ => request with { Origin = request.Origin! with { IdentityOperation = "effect-foreign" } }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => publication.ApplyAsync(changed, default));
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verify.RuntimeConfigurationPermissions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.RuntimePublicationReceipts.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Publication_revalidates_all_recipient_and_identity_evidence_inside_each_acceptance()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e, secondDocument: true));
        var handler = new IdentityFixture(); await using var services = InternalServices(scenario.Connection, scenario.Time);
        var publication = Publications(services, handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => publication.PreparationRequestsAsync(scenario.Commit, "tester", default));
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        await Assert.ThrowsAsync<InvalidDataException>(() => publication.PreparationRequestsAsync(scenario.Commit, "tester", default));
        await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default);
        var request = (await publication.PreparationRequestsAsync(scenario.Commit, "tester", default))[0];
        await using (var arrange = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            await arrange.WorkloadConfigurationRecipients.Where(x => x.Document == "z-last").ExecuteUpdateAsync(s => s.SetProperty(x => x.InputFingerprint, "corrupt"));
        await Assert.ThrowsAsync<InvalidDataException>(() => publication.ApplyAsync(request, default));
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verify.RuntimeConfigurationPermissions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.RuntimePublicationReceipts.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Same_key_races_converge_and_conflicting_replay_cannot_redirect_current_state()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e, secondDocument: true));
        var handler = new IdentityFixture(); await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default);
        var publication = Publications(services, handler); var requests = await publication.PreparationRequestsAsync(scenario.Commit, "tester", default);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<RuntimePublicationResult?> Compete(RuntimePublicationRequest member)
        {
            await start.Task;
            try { return await Publications(services, handler).ApplyAsync(member, default); }
            catch (InvalidDataException) { return null; }
        }
        var first = Compete(requests[0]); var second = Compete(requests[1] with { Key = requests[0].Key }); start.SetResult();
        var winner = Assert.Single((await Task.WhenAll(first, second)).OfType<RuntimePublicationResult>());
        var winningRequest = winner.Receipt.Request;
        var other = requests.Single(x => x.Revision != winningRequest.Revision) with { Key = winningRequest.Key };
        var winners = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Publications(services, handler).ApplyAsync(winningRequest, default)));
        Assert.All(winners, x => Assert.Equal(winners[0], x));
        await Assert.ThrowsAsync<InvalidDataException>(() => publication.ApplyAsync(other, default));
        Assert.Null(await publication.AuthorizeMetadataAsync(other.Binding, other.Revision!.Value, default));
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Single(await verify.RuntimePublicationReceipts.AsNoTracking().ToArrayAsync());
        var permission = Assert.Single(await verify.RuntimeConfigurationPermissions.AsNoTracking().ToArrayAsync());
        Assert.Equal(winners[0].CurrentPermission!.Token, permission.ConcurrencyToken);
        Assert.Equal(winners[0].Receipt.Request.Key, permission.LastRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_revoke_and_republish_binding_revoke_races_have_persisted_linearizable_outcomes(bool bindingRevoke)
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e));
        var handler = new IdentityFixture(); await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default);
        var publication = Publications(services, handler); var request = Assert.Single(await publication.PreparationRequestsAsync(scenario.Commit, "tester", default));
        var initial = await publication.ApplyAsync(request, default);
        if (bindingRevoke) initial = await publication.ApplyAsync(Revoke(request, initial.CurrentPermission!.Token), default);
        var update = request with { Key = "race-publish", ExpectedToken = initial.CurrentPermission!.Token };
        var revoke = bindingRevoke ? new RuntimePublicationRequest("race-binding-revoke", RuntimePublicationOperation.RevokeBinding,
            request.Binding, null, initial.CurrentBinding.Token, "operator") : Revoke(request, initial.CurrentPermission.Token, "race-revoke");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<RuntimePublicationResult> Run(RuntimePublicationRequest member)
        { await start.Task; return await Publications(services, handler).ApplyAsync(member, default); }
        var publishTask = Run(update); var revokeTask = Run(revoke); start.SetResult();
        var results = await Task.WhenAll(publishTask, revokeTask);
        Assert.Equal(RuntimePublicationOutcome.Revoked, results[1].Receipt.Outcome);
        Assert.Contains(results[0].Receipt.Outcome, bindingRevoke ? [RuntimePublicationOutcome.Published, RuntimePublicationOutcome.BindingRevoked] :
            new[] { RuntimePublicationOutcome.Unchanged, RuntimePublicationOutcome.PreconditionFailed });
        Assert.Null(await publication.AuthorizeMetadataAsync(request.Binding, request.Revision!.Value, default));
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        var permission = await verify.RuntimeConfigurationPermissions.AsNoTracking().SingleAsync();
        var binding = await verify.RuntimeIdentityBindings.AsNoTracking().SingleAsync();
        Assert.Equal(bindingRevoke ? "Revoked" : "Prepared", binding.Status);
        if (!bindingRevoke) { Assert.Equal("Revoked", permission.Status); Assert.Equal(results[1].CurrentPermission!.Token, permission.ConcurrencyToken); }
        foreach (var result in results)
        {
            var persisted = RuntimePublicationCodec.Receipt((await verify.RuntimePublicationReceipts.AsNoTracking().SingleAsync(x => x.RequestKey == result.Receipt.Request.Key)).Payload);
            Assert.Equal(result.Receipt, persisted);
        }
    }

    [Fact]
    public async Task Stale_preconditions_precede_equality_and_valid_noops_keep_token_and_audit()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e));
        var handler = new IdentityFixture(); await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default);
        var publication = Publications(services, handler); var request = Assert.Single(await publication.PreparationRequestsAsync(scenario.Commit, "tester", default));
        var first = await publication.ApplyAsync(request, default);
        var stale = await publication.ApplyAsync(request with { Key = "stale-absence" }, default);
        Assert.Equal(RuntimePublicationOutcome.PreconditionFailed, stale.Receipt.Outcome);
        var noop = await publication.ApplyAsync(request with { Key = "current-noop", ExpectedToken = first.CurrentPermission!.Token }, default);
        Assert.Equal(RuntimePublicationOutcome.Unchanged, noop.Receipt.Outcome); Assert.Equal(first.CurrentPermission, noop.CurrentPermission);
        var revoked = await publication.ApplyAsync(Revoke(request, first.CurrentPermission!.Token), default);
        var staleRevoke = await publication.ApplyAsync(Revoke(request, first.CurrentPermission.Token), default);
        Assert.Equal(RuntimePublicationOutcome.PreconditionFailed, staleRevoke.Receipt.Outcome);
        Assert.Equal(revoked.CurrentPermission, staleRevoke.CurrentPermission);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        var persisted = await verify.RuntimeConfigurationPermissions.AsNoTracking().SingleAsync();
        Assert.Equal(revoked.CurrentPermission!.Token, persisted.ConcurrencyToken); Assert.Equal(revoked.Receipt.Request.Key, persisted.LastRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_batch_cancellation_and_response_loss_preserve_exact_committed_prefix(bool loseResponse)
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e, secondDocument: true));
        var handler = new IdentityFixture(); await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var publication = new InternalRuntimePublication(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(),
            ConfigurationPreparation(services).Preparation, IdentityActions(services, handler), scenario.Time, _ =>
            {
                calls++;
                if (loseResponse) throw new IOException("injected-response-loss");
                cancellation.Cancel();
            });
        var partial = await new InternalRuntimePublicationPreparation(publication).PrepareAsync(scenario.Commit, "tester", cancellation.Token);
        Assert.NotNull(partial.Failed); Assert.Equal(1, calls); Assert.Equal(loseResponse ? 0 : 1, partial.Completed.Count);
        Assert.Equal(!loseResponse, partial.Cancelled);
        RuntimePublicationReceipt saved;
        await using (var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            saved = RuntimePublicationCodec.Receipt(Assert.Single(await verify.RuntimePublicationReceipts.AsNoTracking().ToArrayAsync()).Payload);
            Assert.Single(await verify.RuntimeConfigurationPermissions.AsNoTracking().ToArrayAsync());
        }
        var resumed = await new InternalRuntimePublicationPreparation(Publications(services, handler)).PrepareAsync(scenario.Commit, "tester", default);
        Assert.Null(resumed.Failed); Assert.Equal(2, resumed.Completed.Count); Assert.Equal(saved, resumed.Completed[0].Receipt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrupt_identity_origin_or_lifecycle_cannot_authorize_a_new_publication(bool lifecycle)
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e));
        var handler = new IdentityFixture(); await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default);
        var publication = Publications(services, handler); var request = Assert.Single(await publication.PreparationRequestsAsync(scenario.Commit, "tester", default));
        await using (var arrange = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            if (lifecycle) await arrange.RuntimeIdentityBindings.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Revoked")
                .SetProperty(x => x.RevokedBy, "").SetProperty(x => x.RevokedAt, scenario.Time.GetUtcNow()));
            else await arrange.OwnedObjects.ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{}"));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => publication.ApplyAsync(request, default));
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verify.RuntimePublicationReceipts.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.RuntimeConfigurationPermissions.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Historical_receipt_replay_remains_readable_but_cannot_accept_a_new_publish_without_current_authority()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e));
        var handler = new IdentityFixture(); await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default);
        var publication = Publications(services, handler); var request = Assert.Single(await publication.PreparationRequestsAsync(scenario.Commit, "tester", default));
        var first = await publication.ApplyAsync(request, default);
        await publication.ApplyAsync(Revoke(request, first.CurrentPermission!.Token), default);
        await using (var arrange = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            await arrange.ApplicationDeployments.ExecuteUpdateAsync(s => s.SetProperty(x => x.DesiredGeneration, scenario.Commit.Generation.Value + 1));
        var replay = await publication.ApplyAsync(request, default);
        Assert.Equal(first.Receipt, replay.Receipt); Assert.Equal("Revoked", replay.CurrentPermission!.Status);
        Assert.Equal(replay, await publication.ReadAsync(request.Key, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => publication.ApplyAsync(request with { Key = "new-historical-intent", ExpectedToken = replay.CurrentPermission.Token }, default));
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(2, await verify.RuntimePublicationReceipts.CountAsync());
        Assert.Equal("Revoked", (await verify.RuntimeConfigurationPermissions.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Existing_Prepared_binding_migration_initializes_only_lifecycle_metadata_and_retains_origin()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e));
        var handler = new IdentityFixture(); await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        var first = Assert.Single(await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default));
        string inventory;
        await using (var arrange = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            inventory = (await arrange.OwnedObjects.AsNoTracking().SingleAsync()).Payload;
            await arrange.GetService<IMigrator>().MigrateAsync("20261114033757_StableWorkloadRuntimeIdentity");
        }
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var replay = Assert.Single(await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default));
        Assert.Equal(first.Binding, replay.Binding); Assert.Equal(first.Result, replay.Result); Assert.Equal(1, handler.Writes);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(inventory, (await verify.OwnedObjects.AsNoTracking().SingleAsync()).Payload);
        var binding = await verify.RuntimeIdentityBindings.AsNoTracking().SingleAsync();
        Assert.NotEqual(Guid.Empty, binding.ConcurrencyToken); Assert.Equal("Prepared", binding.Status);
        Assert.Null(binding.RevokedAt); Assert.Null(binding.RevokedBy); Assert.Empty(await verify.RuntimePublicationReceipts.AsNoTracking().ToArrayAsync());
    }
}
