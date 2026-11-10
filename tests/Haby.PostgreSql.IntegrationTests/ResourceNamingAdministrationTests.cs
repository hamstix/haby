using Google.Protobuf;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    private sealed class NamingPrecisionClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);
    }

    [Fact]
    public async Task Naming_creation_and_selection_audit_roundtrip_database_precision_and_equivalent_repeats()
    {
        var catalog = await NamingCatalog();
        await using var services = Services(catalog.Connection, full: true);
        var store = new PostgreSqlResourceNamingStore(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), new NamingPrecisionClock());
        var reference = new ResourceNamingReference("literal", 1);
        var created = await store.CreateAsync(catalog.Environment, reference, ResourceNamingPolicy.Exact, "first", default);
        Assert.Equal(created.State, (await store.GetAsync(catalog.Environment, reference, default)).State);
        var repeated = await store.CreateAsync(catalog.Environment, reference, ResourceNamingPolicy.Exact, "second", default);
        Assert.Equal(ResourceNamingOutcome.Unchanged, repeated.Outcome); Assert.Equal(created.State, repeated.State);
        Assert.Equal(ResourceNamingOutcome.RevisionConflict, (await store.CreateAsync(catalog.Environment, reference, new("prefixed", "staging_"), "second", default)).Outcome);
        Assert.Equal(created.State, (await store.GetAsync(catalog.Environment, reference, default)).State);
        var alias = ProviderAlias.Create("postgresql.primary"); var binding = new ProviderAliasBinding(catalog.Provider, ProviderConfigurationRevisionNumber.Create(1));
        var changed = await store.SelectAsync(catalog.Environment, alias, binding, null, reference, "first", default);
        Assert.Equal(changed.State, (await store.GetSelectionAsync(catalog.Environment, alias, default)).State);
        var unchanged = await store.SelectAsync(catalog.Environment, alias, binding, changed.State!.Token, reference, "second", default);
        Assert.Equal(ResourceNamingOutcome.Unchanged, unchanged.Outcome); Assert.Equal(changed.State, unchanged.State);
    }

    [Fact]
    public async Task Naming_policy_selection_cas_audit_absence_freshness_and_exact_mode_are_atomic()
    {
        var catalog = await NamingCatalog();
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + catalog.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        var envelope = PostgreSqlPairPlanningTests.Example();
        envelope["manifest"]!["spec"]!["resources"]!["db"]!["properties"]!["name"] = "exact_" + Guid.NewGuid().ToString("N");
        var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(envelope)) });
        var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = imported.Created.Revision.Reference, Precondition = PlanningPrecondition.ExpectedAbsence });
        var original = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
        Assert.Equal(PlanningStatus.Planned, original.Status);
        foreach (var revision in new[] { 1L, 2L })
            Assert.Equal(ResourceNamingStatus.Created, (await client.CreateResourceNamingAsync(new() { Reference = new() { Environment = "pilot", Name = "literal", Revision = revision }, DeclarationJson = "{\"mode\":\"exact\"}" })).Status);
        // Creating revisions and unrelated Environment/binding edits do not stale the absent/default selection.
        await client.CreateEnvironmentAsync(new() { Name = "unrelated" });
        Assert.Equal(ResourceNamingStatus.Created, (await client.CreateResourceNamingAsync(new() { Reference = new() { Environment = "unrelated", Name = "foreign", Revision = 1 }, DeclarationJson = "{\"mode\":\"exact\"}" })).Status);
        var foreign = Select(1, null); foreign.Name = "foreign";
        Assert.Equal(ResourceNamingStatus.RevisionNotFound, (await client.SelectResourceNamingAsync(foreign)).Status);
        // A different binding in the SAME Environment is not an input to this Plan.
        var providers = host.Services.GetRequiredService<IProviderConfigurationStore>();
        var configuration = (await providers.FindRevisionAsync<PostgreSqlProviderConfiguration>(catalog.Environment,
            catalog.Provider, ProviderConfigurationRevisionNumber.Create(1), default))!.Configuration;
        var unrelatedProvider = await providers.CreateInstanceAsync(catalog.Environment, configuration, default);
        await providers.AddRevisionAsync(catalog.Environment, unrelatedProvider, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
        var unrelatedAlias = ProviderAlias.Create("postgresql.secondary");
        var unrelatedBinding = new ProviderAliasBinding(unrelatedProvider, ProviderConfigurationRevisionNumber.Create(1));
        Assert.Equal(ProviderAliasChangeOutcome.Changed, (await host.Services.GetRequiredService<IProviderAliasStore>().ChangeAsync(
            catalog.Environment, unrelatedAlias, null, unrelatedBinding, "fixture", default)).Outcome);
        Assert.Equal(ResourceNamingOutcome.Changed, (await host.Services.GetRequiredService<IResourceNamingStore>().SelectAsync(
            catalog.Environment, unrelatedAlias, unrelatedBinding, null, new("literal", 1), "fixture", default)).Outcome);
        Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(new() { PlanId = original.Plan.PlanId })).Status);
        var requests = new[] { Select(1, null), Select(2, null) };
        var race = await Task.WhenAll(requests.Select(r => client.SelectResourceNamingAsync(r).ResponseAsync));
        var winner = Assert.Single(race, r => r.Status == ResourceNamingStatus.Changed);
        Assert.Single(race, r => r.Status == ResourceNamingStatus.StaleExpectation);
        Assert.Equal(PlanningStatus.PlanStale, (await client.CheckPlanAsync(new() { PlanId = original.Plan.PlanId })).Status);
        Assert.Equal(CommitStatus.PlanStale, (await client.CommitDesiredStateAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId, PlanId = original.Plan.PlanId })).Status);
        var repeat = await client.SelectResourceNamingAsync(Select(winner.Revision, winner.Token));
        Assert.Equal(ResourceNamingStatus.Unchanged, repeat.Status);
        Assert.Equal(winner.Token, repeat.Token); Assert.Equal(winner.Actor, repeat.Actor); Assert.Equal(winner.ChangedAtUtc, repeat.ChangedAtUtc);
        Assert.Equal(ResourceNamingStatus.StaleExpectation, (await client.SelectResourceNamingAsync(Select(winner.Revision, Guid.NewGuid().ToString("D")))).Status);
        Assert.Equal(ResourceNamingStatus.RevisionNotFound, (await client.SelectResourceNamingAsync(Select(999, winner.Token))).Status);
        var wrong = Select(winner.Revision, winner.Token); wrong.ExpectedProviderInstanceId = ProviderInstanceId.New().ToString();
        Assert.Equal(ResourceNamingStatus.StaleExpectation, (await client.SelectResourceNamingAsync(wrong)).Status);
        var beforeReturn = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
        var other = await client.SelectResourceNamingAsync(Select(winner.Revision == 1 ? 2 : 1, winner.Token));
        Assert.Equal(ResourceNamingStatus.Changed, other.Status);
        var returned = await client.SelectResourceNamingAsync(Select(winner.Revision, other.Token));
        Assert.Equal(ResourceNamingStatus.Changed, returned.Status);
        Assert.NotEqual(winner.Token, returned.Token);
        Assert.Equal(PlanningStatus.PlanStale, (await client.CheckPlanAsync(new() { PlanId = beforeReturn.Plan.PlanId })).Status);
        var plan = await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId });
        Assert.Equal(PlanningStatus.Planned, plan.Status);
        Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId, PlanId = plan.Plan.PlanId })).Status);
        var saved = (await host.Services.GetRequiredService<IDurableDeploymentPlanner>().FindPlanAsync(DeploymentPlanId.Parse(plan.Plan.PlanId), default))!;
        var receipt = (await host.Services.GetRequiredService<IDurableDesiredStateCommitter>().FindCommitAsync(DeploymentChangeSetId.Parse(change.ChangeSet.ChangeSetId), default))!;
        var logical = (string)envelope["manifest"]!["spec"]!["resources"]!["db"]!["properties"]!["name"]!;
        Assert.Equal(GenerationExecutionStatus.Succeeded, (await client.ExecuteGenerationAsync(new() { DeploymentId = receipt.DeploymentId.ToString(), Generation = 1 })).Status);
        Assert.NotNull(await DatabaseOid(logical));
        Assert.Equal(ResourceNamingPolicy.Exact, saved.Inputs.Naming.Policy);
        await using var verify = Context(catalog.Connection);
        Assert.Equal(3, await verify.ResourceNamingPolicies.CountAsync());
        Assert.Equal(returned.Token, (await verify.ResourceNamingSelections.AsNoTracking().SingleAsync(x => x.Alias == "postgresql.primary")).Token.ToString("D"));
        Assert.Equal(1, await verify.DeploymentGenerations.CountAsync());
        await host.StopAsync();

        SelectResourceNamingRequest Select(long revision, string? token)
        {
            var result = new SelectResourceNamingRequest
            {
                Binding = new() { Environment = "pilot", Alias = "postgresql.primary" },
                Name = "literal",
                Revision = revision,
                ExpectedProviderInstanceId = catalog.Provider.ToString(),
                ExpectedConfigurationRevision = 1
            };
            if (token is null) result.ExpectedAbsence = true; else result.ExpectedToken = token;
            return result;
        }
    }

    [Fact]
    public async Task Naming_corrupt_or_unsupported_selected_snapshot_is_unavailable_not_an_exact_default()
    {
        var catalog = await NamingCatalog();
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + catalog.Connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"]);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
        await client.CreateResourceNamingAsync(new() { Reference = new() { Environment = "pilot", Name = "contour", Revision = 1 }, DeclarationJson = "{\"mode\":\"prefixed\",\"prefix\":\"staging_\"}" });
        Assert.Equal(ResourceNamingStatus.Changed, (await client.SelectResourceNamingAsync(new()
        {
            Binding = new() { Environment = "pilot", Alias = "postgresql.primary" },
            Name = "contour",
            Revision = 1,
            ExpectedProviderInstanceId = catalog.Provider.ToString(),
            ExpectedConfigurationRevision = 1,
            ExpectedAbsence = true
        })).Status);
        var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(PostgreSqlPairPlanningTests.Example())) });
        var change = await client.CreateChangeSetAsync(new() { EnvironmentId = "pilot", Reference = imported.Created.Revision.Reference, Precondition = PlanningPrecondition.ExpectedAbsence });
        await using (var corrupt = Context(catalog.Connection))
            await corrupt.ResourceNamingPolicies.ExecuteUpdateAsync(s => s.SetProperty(x => x.Snapshot, "{\"version\":99}"));
        Assert.Equal(ResourceNamingStatus.StoreUnavailable, (await client.GetResourceNamingAsync(new() { Environment = "pilot", Name = "contour", Revision = 1 })).Status);
        Assert.Equal(ResourceNamingStatus.StoreUnavailable, (await client.GetResourceNamingSelectionAsync(new() { Environment = "pilot", Alias = "postgresql.primary" })).Status);
        Assert.Equal(PlanningStatus.PlanInputUnavailable, (await client.CreatePlanAsync(new() { ChangeSetId = change.ChangeSet.ChangeSetId })).Status);
        await using (var verify = Context(catalog.Connection))
        { Assert.Empty(await verify.DurablePlans.ToArrayAsync()); Assert.Empty(await verify.GeneratedCredentialSlots.ToArrayAsync()); }
        await host.StopAsync();
    }

    [Fact]
    public async Task Naming_commit_waits_for_actual_selection_lock_and_cancellation_rolls_back_every_acceptance_write()
    {
        var catalog = await NamingCatalog();
        await using var services = Services(catalog.Connection, full: true);
        var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
            services.GetRequiredService<IManifestImportStore>(), PostgreSqlPairPlanningTests.Catalog, TimeProvider.System)
            .ExecuteAsync(PostgreSqlPairPlanningTests.Bytes(PostgreSqlPairPlanningTests.Example()), "tester"));
        var planner = services.GetRequiredService<IDurableDeploymentPlanner>();
        var change = (await planner.CreateChangeSetAsync(catalog.Environment, imported.ManifestRevisionRef.ApplicationRecordId, imported.ManifestRevisionRef,
            new DeploymentPrecondition.ExpectedAbsence(), "tester", default)).ChangeSet!;
        var plan = (await planner.CreatePlanAsync(change.Id, "tester", default)).Plan!;
        await using var blocker = new NpgsqlConnection(catalog.Connection); await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        var lockKey = $"haby-naming:{Guid.Parse(catalog.Environment.ToString()):N}:postgresql.primary";
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", blocker, transaction))
        { command.Parameters.AddWithValue(lockKey); await command.ExecuteNonQueryAsync(); }
        using var cancellation = new CancellationTokenSource();
        var commit = services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, plan.Id, "tester", cancellation.Token).AsTask();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var observer = new NpgsqlConnection(catalog.Connection))
        {
            await observer.OpenAsync(deadline.Token);
            while (true)
            {
                await using var waiting = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE $1 = ANY(pg_blocking_pids(pid)))", observer);
                waiting.Parameters.AddWithValue(blocker.ProcessID);
                if (await waiting.ExecuteScalarAsync(deadline.Token) is true) break;
                Assert.False(commit.IsCompleted); await Task.Delay(10, deadline.Token);
            }
        }
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit);
        await transaction.RollbackAsync();
        await using (var verify = Context(catalog.Connection))
        {
            Assert.Empty(await verify.ApplicationDeployments.ToArrayAsync()); Assert.Empty(await verify.ChangeSetCommits.ToArrayAsync());
            Assert.Empty(await verify.DeploymentGenerations.ToArrayAsync()); Assert.Empty(await verify.DurableOperations.ToArrayAsync());
            Assert.Empty(await verify.ResourceManagement.ToArrayAsync()); Assert.Empty(await verify.GeneratedCredentialSlots.ToArrayAsync());
        }
        Assert.Equal(DurablePlanCurrentStatus.Current, await planner.CheckPlanAsync(plan.Id, default));
        Assert.IsType<DurableCommitResult.Committed>(await services.GetRequiredService<IDurableDesiredStateCommitter>().CommitAsync(change.Id, plan.Id, "tester", default));
    }
}
