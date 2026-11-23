using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Npgsql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ResourceReferenceKernelTests
{
    [Fact]
    public async Task Shared_reference_associations_of_two_applications_confirm_distinct_history()
    {
        var connection = await CreateDatabaseAsync("reference_shared_");
        var boundary = new Boundary();
        var first = await PrepareAsync(connection, boundary, "shared-first", mixed: false, sharingClass: "readers");
        await using (var revisions = Services(connection, boundary))
        {
            await revisions.GetRequiredService<IProviderConfigurationStore>().AddRevisionAsync(first.Environment, first.Provider,
                ProviderConfigurationRevisionNumber.Create(2), new SampleConfiguration("verified-test-service", "sample.impl/v1"), default);
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await revisions.GetRequiredService<IProviderAliasStore>().ChangeAsync(first.Environment,
                ProviderAlias.Create("sample.primary"), new(first.Provider, ProviderConfigurationRevisionNumber.Create(1)),
                new(first.Provider, ProviderConfigurationRevisionNumber.Create(2)), "tester", default)).Outcome);
        }
        var second = await PrepareAsync(connection, boundary, "shared-second", scope: first, mixed: false, sharingClass: "readers");
        Assert.Equal(1, first.Plan.Inputs.Providers.Resource(Referenced).Configuration.Value);
        Assert.Equal(2, second.Plan.Inputs.Providers.Resource(Referenced).Configuration.Value);
        await using (var services = Services(connection, boundary))
        {
            var executor = services.GetRequiredService<IDurableOperationExecutor>();
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await executor.ExecuteAsync(Operation(first, "referenced-first"), default)).Outcome);
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await executor.ExecuteAsync(Operation(second, "referenced-first"), default)).Outcome);
        }
        await using (var services = Services(connection, boundary))
        {
            var receipts = services.GetRequiredService<IResourceObservationAcceptance>();
            var left = Assert.IsType<ResourceObservationReceipt>(await receipts.FindAsync(Operation(first, "referenced-first"), default));
            var right = Assert.IsType<ResourceObservationReceipt>(await receipts.FindAsync(Operation(second, "referenced-first"), default));
            Assert.NotEqual(left.Id, right.Id);
            Assert.NotEqual(left.Evidence, right.Evidence);
            Assert.Equal(2, left.Operations.Count);
            Assert.Equal(2, right.Operations.Count);
            Assert.Empty(left.Operations.Select(x => x.AttemptId).Intersect(right.Operations.Select(x => x.AttemptId)));
            await using var verify = await services.GetRequiredService<IDbContextFactory<Haby.Persistence.PostgreSql.ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.Equal(2, await verify.ResourceIdentityClaims.CountAsync());
            Assert.Equal(2, await verify.ResourceObservationReceipts.CountAsync());
            Assert.Equal(4, await verify.OperationOutputs.CountAsync());
        }
        Assert.Equal(2, boundary.Observations);
    }

    [Fact]
    public async Task Shared_resources_of_one_application_keep_distinct_associations_receipts_and_attempts()
    {
        var connection = await CreateDatabaseAsync("reference_same_app_"); var boundary = new Boundary();
        var item = await PrepareAsync(connection, boundary, mixed: false, sharingClass: "readers", secondReference: true);
        Assert.Equal(2, item.Plan.Graph.ObservationGroups.Count);
        await using (var act = Services(connection, boundary))
        {
            var executor = act.GetRequiredService<IDurableOperationExecutor>();
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await executor.ExecuteAsync(Operation(item, "referenced-first"), default)).Outcome);
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await executor.ExecuteAsync(Operation(item, "shared-peer-first"), default)).Outcome);
            var receipts = act.GetRequiredService<IResourceObservationAcceptance>();
            var first = (await receipts.FindAsync(Operation(item, "referenced-first"), default))!;
            var peer = (await receipts.FindAsync(Operation(item, "shared-peer-first"), default))!;
            Assert.NotEqual(first.Id, peer.Id);
            Assert.NotEqual(first.Evidence, peer.Evidence);
            Assert.Empty(first.Operations.Select(x => x.AttemptId).Intersect(peer.Operations.Select(x => x.AttemptId)));
        }
        await AssertSharedPublicationAsync(connection, boundary, 2);
        Assert.Equal(2, boundary.Observations);
    }

    [Fact]
    public async Task Missing_unavailable_or_changed_sharing_registration_refuses_every_handler_and_direct_acceptance()
    {
        foreach (var fault in new[] { "missing", "unavailable", "changed-contract" })
        {
            var connection = await CreateDatabaseAsync("reference_share_gate_"); var boundary = new Boundary();
            var item = await PrepareAsync(connection, boundary, sharingClass: "readers");
            await using var bad = Services(connection, boundary, sharingCapability: fault != "missing",
                sharingAvailable: fault != "unavailable", sharingContract: fault == "changed-contract" ? "test.reference-sharing/v2" : "test.reference-sharing/v1");
            Assert.Equal(DurableExecutionOutcome.InputUnavailable, (await bad.GetRequiredService<IDurableOperationExecutor>()
                .ExecuteAsync(Operation(item, "imported-verify"), default)).Outcome);
            Assert.Equal(0, boundary.HandlerCalls); Assert.Equal(0, boundary.Observations);
            await using (var verify = await bad.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
                Assert.Empty(await verify.DurableOperationAttempts.AsNoTracking().ToArrayAsync());
            ResourceObservationAcceptance request;
            await using (var stage = Services(connection, boundary)) request = await StageAsync(stage, item, Referenced);
            Assert.Equal(ResourceObservationAcceptanceStatus.InputUnavailable, (await bad.GetRequiredService<IResourceObservationAcceptance>()
                .CompleteAsync(request, DateTimeOffset.UtcNow, default)).Status);
            await AssertSharedPublicationAsync(connection, boundary, 0);
        }
    }

    [Fact]
    public async Task Both_peers_require_explicit_compatible_authority_and_nonsharing_cannot_be_upgraded_by_registration()
    {
        foreach (var groups in new[] { ("readers", "writers"), ("readers", (string?)null), ((string?)null, "readers"), ("open", "strict"), ("strict", "open") })
        {
            var connection = await CreateDatabaseAsync("reference_share_refusal_"); var boundary = new Boundary();
            var first = await PrepareAsync(connection, boundary, "first", mixed: false, sharingClass: groups.Item1);
            var peer = await PrepareAsync(connection, boundary, "peer", scope: first, mixed: false, sharingClass: groups.Item2);
            ResourceObservationAcceptance left; ResourceObservationAcceptance right;
            await using (var stage = Services(connection, boundary))
            { left = await StageAsync(stage, first, Referenced); right = await StageAsync(stage, peer, Referenced); }
            await using (var original = Services(connection, boundary, sharingCapability: groups.Item1 is not null))
                Assert.True((await original.GetRequiredService<IResourceObservationAcceptance>().CompleteAsync(left, DateTimeOffset.UtcNow, default)).IsSuccess);
            await using (var changedRegistration = Services(connection, boundary))
            {
                Assert.Equal(ResourceObservationAcceptanceStatus.Conflict, (await changedRegistration.GetRequiredService<IResourceObservationAcceptance>()
                    .CompleteAsync(right, DateTimeOffset.UtcNow, default)).Status);
                Assert.NotNull(await ReadAsync(changedRegistration, first));
                Assert.Null(await ReadAsync(changedRegistration, peer));
                Assert.NotNull(await changedRegistration.GetRequiredService<IResourceObservationAcceptance>().FindAsync(left.Operations[0].Key, default));
                Assert.Null(await changedRegistration.GetRequiredService<IResourceObservationAcceptance>().FindAsync(right.Operations[0].Key, default));
            }
            await AssertSharedPublicationAsync(connection, boundary, 1);
        }
    }

    [Fact]
    public async Task Shared_and_exclusive_writers_share_the_provider_lock_in_both_orders_and_forced_empty_identity_races()
    {
        foreach (var peerMode in new[] { "shared", "import", "exclusive-reference", "reservation" })
            foreach (var order in new[] { "left-first", "right-first", "concurrent" })
            {
                var connection = await CreateDatabaseAsync("reference_share_race_"); var boundary = new Boundary();
                var left = await PrepareAsync(connection, boundary, "left", mixed: false, sharingClass: "readers");
                var right = await PrepareAsync(connection, boundary, "right", scope: left, mixed: false,
                    singleMode: peerMode == "import" ? ResourceAcquisitionMode.ImportUseAndRetain : ResourceAcquisitionMode.Reference,
                    sharingClass: peerMode == "shared" ? "readers" : null);
                ResourceObservationAcceptance leftRequest; ResourceObservationAcceptance rightRequest;
                await using (var stage = Services(connection, boundary))
                { leftRequest = await StageAsync(stage, left, Referenced); rightRequest = await StageAsync(stage, right, Referenced); }
                await using var first = Services(connection, boundary); await using var second = Services(connection, boundary);
                async Task<ResourceObservationAcceptanceResult> Left() => await first.GetRequiredService<IResourceObservationAcceptance>().CompleteAsync(leftRequest, DateTimeOffset.UtcNow, default);
                async Task<ResourceObservationAcceptanceResult> Right() => peerMode == "reservation"
                    ? new(await second.GetRequiredService<IProviderResourceReservations>().TryReserveAsync(Identity(right, Referenced, "shared-service"), default)
                        ? ResourceObservationAcceptanceStatus.Accepted : ResourceObservationAcceptanceStatus.Conflict)
                    : await second.GetRequiredService<IResourceObservationAcceptance>().CompleteAsync(rightRequest, DateTimeOffset.UtcNow, default);
                ResourceObservationAcceptanceResult[] results;
                if (order == "concurrent")
                {
                    await using var blocker = new NpgsqlConnection(connection); await blocker.OpenAsync();
                    await using var transaction = await blocker.BeginTransactionAsync();
                    await using (var command = new NpgsqlCommand("SELECT id FROM provider_instances WHERE id = @provider FOR UPDATE", blocker, transaction))
                    { command.Parameters.AddWithValue("provider", Guid.Parse(left.Provider.ToString())); await command.ExecuteScalarAsync(); }
                    await using var observer = new NpgsqlConnection(connection); await observer.OpenAsync();
                    var leftTask = Left(); var rightTask = Right();
                    var bothBlocked = false;
                    try
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        while (true)
                        {
                            await using var observe = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND wait_event_type = 'Lock' AND query LIKE '%provider_instances%FOR UPDATE%'", observer);
                            if ((long)(await observe.ExecuteScalarAsync(deadline.Token))! >= 2) { bothBlocked = true; break; }
                            await Task.Delay(20, deadline.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                    finally { await transaction.CommitAsync(); }
                    results = await Task.WhenAll(leftTask, rightTask);
                    Assert.True(bothBlocked, "Both writers must reach the held Provider row before release.");
                }
                else if (order == "left-first") results = [await Left(), await Right()];
                else { var r = await Right(); results = [await Left(), r]; }
                Assert.Equal(peerMode == "shared" ? 2 : 1, results.Count(x => x.IsSuccess));
                Assert.All(results.Where(x => !x.IsSuccess), x => Assert.Equal(ResourceObservationAcceptanceStatus.Conflict, x.Status));
                await using var verify = await first.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
                var expected = peerMode == "reservation" && !results[0].IsSuccess ? 0 : peerMode == "shared" ? 2 : 1;
                Assert.Equal(expected, await verify.ResourceObservationReceipts.CountAsync());
                Assert.Equal(expected * (peerMode == "import" && results[1].IsSuccess ? 1 : 2), await verify.OperationOutputs.CountAsync());
                Assert.Equal(peerMode == "shared" ? 2 : results[0].IsSuccess || peerMode == "exclusive-reference" && results[1].IsSuccess ? 1 : 0,
                    await verify.ResourceIdentityClaims.CountAsync());
            }
    }

    [Fact]
    public async Task Shared_cancel_and_lost_reply_keep_the_peer_and_exact_retry_history_atomic()
    {
        foreach (var beforeCommit in new[] { true, false })
        {
            var connection = await CreateDatabaseAsync("reference_shared_fault_"); var boundary = new Boundary();
            var first = await PrepareAsync(connection, boundary, "first", mixed: false, sharingClass: "readers");
            var peer = await PrepareAsync(connection, boundary, "peer", scope: first, mixed: false, sharingClass: "readers");
            ResourceObservationAcceptance own; ResourceObservationAcceptance other;
            await using (var stage = Services(connection, boundary))
            {
                own = await StageAsync(stage, first, Referenced); other = await StageAsync(stage, peer, Referenced);
                Assert.True((await stage.GetRequiredService<IResourceObservationAcceptance>().CompleteAsync(own, DateTimeOffset.UtcNow, default)).IsSuccess);
            }
            using var cancellation = new CancellationTokenSource();
            await using (var fault = Services(connection, boundary, interceptor: new CommitFault(beforeCommit, cancellation)))
                Assert.Equal(!beforeCommit, (await fault.GetRequiredService<IResourceObservationAcceptance>()
                    .CompleteAsync(other, DateTimeOffset.UtcNow, cancellation.Token)).IsSuccess);
            await AssertSharedPublicationAsync(connection, boundary, beforeCommit ? 1 : 2);
            await using var restart = Services(connection, boundary);
            var acceptance = restart.GetRequiredService<IResourceObservationAcceptance>();
            var firstReceipt = (await acceptance.FindAsync(own.Operations[0].Key, default))!;
            Assert.NotNull(await ReadAsync(restart, first));
            if (beforeCommit) Assert.Null(await acceptance.FindAsync(other.Operations[0].Key, default));
            else
            {
                var peerReceipt = (await acceptance.FindAsync(other.Operations[0].Key, default))!;
                Assert.NotEqual(firstReceipt.Id, peerReceipt.Id);
                var repeats = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => acceptance.CompleteAsync(other, DateTimeOffset.UtcNow, default).AsTask()));
                Assert.All(repeats, result => Assert.Equal(ResourceObservationAcceptanceStatus.Existing, result.Status));
                Assert.Equal(peerReceipt.Id, (await acceptance.FindAsync(other.Operations[1].Key, default))!.Id);
                Assert.NotNull(await ReadAsync(restart, peer));
            }
        }
    }

    [Fact]
    public async Task Shared_owner_identity_switch_foreign_association_and_namespace_evasion_refuse_without_peer_damage()
    {
        var connection = await CreateDatabaseAsync("reference_shared_scope_"); var boundary = new Boundary();
        var first = await PrepareAsync(connection, boundary, "first", mixed: false, sharingClass: "readers");
        var peer = await PrepareAsync(connection, boundary, "peer", scope: first, mixed: false, sharingClass: "readers");
        ResourceObservationAcceptance request;
        await using (var stage = Services(connection, boundary))
        {
            request = await StageAsync(stage, first, Referenced);
            Assert.True((await stage.GetRequiredService<IResourceObservationAcceptance>().CompleteAsync(request, DateTimeOffset.UtcNow, default)).IsSuccess);
            var other = await StageAsync(stage, peer, Referenced);
            Assert.Equal(ResourceObservationAcceptanceStatus.InputUnavailable, (await stage.GetRequiredService<IResourceObservationAcceptance>()
                .CompleteAsync(other with { Evidence = request.Evidence }, DateTimeOffset.UtcNow, default)).Status);
            Assert.Equal(ResourceObservationAcceptanceStatus.InputUnavailable, (await stage.GetRequiredService<IResourceObservationAcceptance>()
                .CompleteAsync(other with { Operations = request.Operations }, DateTimeOffset.UtcNow, default)).Status);
            Assert.False((await stage.GetRequiredService<IResourceObservationAcceptance>().CompleteAsync(other with
            {
                Operations = other.Operations.Select((x, i) => x with { AttemptId = request.Operations[i].AttemptId }).ToArray()
            }, DateTimeOffset.UtcNow, default)).IsSuccess);
            Assert.Equal(ResourceObservationAcceptanceStatus.InputUnavailable, (await stage.GetRequiredService<IResourceObservationAcceptance>().CompleteAsync(
                other with { Reservations = [Identity(peer, Referenced, "shared-service") with { Namespace = "reference.other" }] }, DateTimeOffset.UtcNow, default)).Status);
        }
        ResourceIdentityClaimRow original;
        await using (var arranged = Services(connection, boundary))
        await using (var read = await arranged.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            original = await read.ResourceIdentityClaims.AsNoTracking().SingleAsync();
        foreach (var fault in new[] { "switch", "foreign-environment", "foreign-association", "foreign-provider" })
        {
            await using var services = Services(connection, boundary);
            await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();

            var foreign = fault == "foreign-environment" ? await services.GetRequiredService<ISecretStore>().CreateEnvironmentAsync(EnvironmentId.Create("other"), default) : first.Environment;
            var provider = fault == "foreign-provider"
                ? await services.GetRequiredService<IProviderConfigurationStore>().CreateInstanceAsync(first.Environment, new SampleConfiguration("verified-test-service", "sample.impl/v1"), default)
                : first.Provider;
            db.ResourceIdentityClaims.Add(new()
            {
                Provider = Guid.Parse(provider.ToString()),
                Namespace = original.Namespace,
                Value = fault == "switch" ? "changed-service" : original.Value,
                Environment = Guid.Parse(foreign.ToString()),
                Application = fault == "foreign-association" ? Guid.Parse(peer.Plan.Inputs.Revision.ApplicationRecordId.ToString()) : original.Application,
                Resource = original.Resource,
                Association = original.Association,
                SharingJson = original.SharingJson
            });
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await AssertSharedPublicationAsync(connection, boundary, 1);
    }

    [Fact]
    public async Task Equal_endpoints_on_distinct_provider_instances_keep_separate_logical_claim_scopes()
    {
        var connection = await CreateDatabaseAsync("reference_provider_scope_"); var boundary = new Boundary();
        var first = await PrepareAsync(connection, boundary, "first", mixed: false);
        var peer = await PrepareAsync(connection, boundary, "peer", scope: first, mixed: false, newProvider: true);
        Assert.NotEqual(first.Provider, peer.Provider);
        await using (var act = Services(connection, boundary))
        {
            var executor = act.GetRequiredService<IDurableOperationExecutor>();
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await executor.ExecuteAsync(Operation(first, "referenced-first"), default)).Outcome);
            Assert.Equal(DurableExecutionOutcome.Succeeded, (await executor.ExecuteAsync(Operation(peer, "referenced-first"), default)).Outcome);
        }
        await AssertSharedPublicationAsync(connection, boundary, 2);
    }

    [Fact]
    public async Task Inactive_shared_peer_keeps_independent_active_outputs_and_both_immutable_histories()
    {
        var connection = await CreateDatabaseAsync("reference_shared_active_"); var boundary = new Boundary();
        var first = await PrepareAsync(connection, boundary, "first", mixed: false, sharingClass: "readers");
        var peer = await PrepareAsync(connection, boundary, "peer", scope: first, mixed: false, sharingClass: "readers");
        Guid firstReceipt; Guid peerReceipt;
        await using (var act = Services(connection, boundary))
        {
            await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(Operation(first, "referenced-first"), default);
            await act.GetRequiredService<IDurableOperationExecutor>().ExecuteAsync(Operation(peer, "referenced-first"), default);
            var receipts = act.GetRequiredService<IResourceObservationAcceptance>();
            firstReceipt = (await receipts.FindAsync(Operation(first, "referenced-first"), default))!.Id;
            peerReceipt = (await receipts.FindAsync(Operation(peer, "referenced-first"), default))!.Id;
            Assert.Null(await ReadAsync(act, peer, owner: new(first.Environment, first.Plan.Inputs.Revision.ApplicationRecordId, Referenced)));
        }
        await using (var services = Services(connection, boundary))
        await using (var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            var application = Guid.Parse(first.Plan.Inputs.Revision.ApplicationRecordId.ToString());
            await db.ResourceManagementHeads.Where(x => x.Application == application).ExecuteDeleteAsync();
        }
        await using (var restart = Services(connection, boundary))
        {
            Assert.Null(await ReadAsync(restart, first)); Assert.NotNull(await ReadAsync(restart, peer));
            var receipts = restart.GetRequiredService<IResourceObservationAcceptance>();
            Assert.Equal(firstReceipt, (await receipts.FindAsync(Operation(first, "referenced-first"), default))!.Id);
            Assert.Equal(peerReceipt, (await receipts.FindAsync(Operation(peer, "referenced-first"), default))!.Id);
        }
        await using (var unavailable = Services(connection, boundary, sharingCapability: false))
        {
            Assert.Null(await ReadAsync(unavailable, peer));
            Assert.Equal(peerReceipt, (await unavailable.GetRequiredService<IResourceObservationAcceptance>().FindAsync(Operation(peer, "referenced-first"), default))!.Id);
        }
        Assert.Equal(2, boundary.Observations);
    }

    [Fact]
    public async Task Prior_reference_catalog_history_refuses_sharing_migration_without_reset_or_authority_backfill()
    {
        var connection = await CreateDatabaseAsync("reference_share_history_"); var boundary = new Boundary();
        var item = await PrepareAsync(connection, boundary, mixed: false);
        string[] applied; string graph;
        await using (var services = Services(connection, boundary))
        await using (var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            graph = (await db.DeploymentGenerations.AsNoTracking().SingleAsync()).GraphJson;
            await db.GetService<IMigrator>().MigrateAsync("20261123070805_ResourceObservationKernel");
            applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        }
        await using (var services = Services(connection, boundary))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync());
            Assert.Contains("fresh catalog", error.Message);
        }
        await using (var services = Services(connection, boundary))
        await using (var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(applied, (await verify.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(graph, (await verify.DeploymentGenerations.AsNoTracking().SingleAsync()).GraphJson);
            Assert.Equal(1, await verify.DurablePlans.CountAsync());
            Assert.Empty(await verify.ResourceObservationReceipts.AsNoTracking().ToArrayAsync());
        }
    }

    private static async Task AssertSharedPublicationAsync(string connection, Boundary boundary, int count)
    {
        await using var services = Services(connection, boundary);
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Equal(count, await verify.ResourceIdentityClaims.CountAsync());
        Assert.Equal(count, await verify.ResourceObservationReceipts.CountAsync());
        Assert.Equal(count * 2, await verify.OperationOutputs.CountAsync());
    }
}
