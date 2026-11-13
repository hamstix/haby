using System.Data.Common;
using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Fact]
    public async Task Reversed_component_alias_captures_use_one_global_lock_order_and_preserve_exact_inputs()
    {
        var scenario = await InternalScenarioAsync();
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var aliases = services.GetRequiredService<IProviderAliasStore>();
        var primary = (await aliases.ResolveAsync(scenario.Environment, ProviderAlias.Create("kubernetes.primary"), default))!;
        var secondaryAlias = ProviderAlias.Create("kubernetes.secondary");
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        await services.GetRequiredService<ISecretStore>().CreateAsync(new(new SecretOwner.Environment(scenario.Environment), SecretPurpose.ProviderAdministrator),
            secret, "fixture-token"u8.ToArray(), default);
        var configuration = new KubernetesProviderConfiguration(new Uri("https://127.0.0.1:6443"), null, secret, null, null);
        var providers = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await providers.CreateInstanceAsync(scenario.Environment, configuration, default);
        var revision = ProviderConfigurationRevisionNumber.Create(1);
        await providers.AddRevisionAsync(scenario.Environment, instance, revision, configuration, default);
        Assert.Equal(ProviderAliasChangeOutcome.Changed, (await aliases.ChangeAsync(scenario.Environment, secondaryAlias, null, new(instance, revision), "tester", default)).Outcome);
        var secondary = (await aliases.ResolveAsync(scenario.Environment, secondaryAlias, default))!;
        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var codec = new KubernetesPlacementCodec();
        var placement = codec.Decode("""{"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"secondary","revision":1},"spec":{"namespace":"second"}}"""u8.ToArray());
        var placementKey = new PlacementRevisionKey(scenario.Environment, secondaryAlias, placement.Name, placement.Revision);
        await new PostgreSqlPlacementRevisionStore(factory, codec).CompareAndCreateAsync(placementKey, placement, new("tester", scenario.Time.GetUtcNow()), default);
        Assert.Equal(PlacementSelectionOutcome.Created, (await new PostgreSqlPlacementSelectionStore(factory, codec).ApplyAsync(
            PlacementSelectionTarget.Create("environment"), placementKey, new PlacementSelectionPrecondition.ExpectedAbsence(), new("tester", scenario.Time.GetUtcNow(), null), default)).Outcome);
        var importer = new ImportApplicationManifest(services.GetRequiredService<IManifestImportStore>(), new ManifestContractCatalog([], [KubernetesContract.Definition], [], []), scenario.Time);
        async Task<NormalizedApplicationManifest> Import(bool reversed)
        {
            var envelope = JsonNode.Parse("""{"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"placeholder"},"spec":{"components":{"api":{"workloads":{"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary","properties":{"image":{"reference":"registry.example/app:v1"}}}}},"worker":{"workloads":{"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.secondary","properties":{"image":{"reference":"registry.example/app:v1"}}}}}}}}}"""u8)!;
            envelope["manifest"]!["metadata"]!["id"] = "lock-order-" + Guid.NewGuid().ToString("N")[..12];
            if (reversed)
            {
                envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["providerRef"] = secondaryAlias.Value;
                envelope["manifest"]!["spec"]!["components"]!["worker"]!["workloads"]!["main"]!["providerRef"] = primary.Alias.Value;
            }
            var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await importer.ExecuteAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), "tester"));
            return (await services.GetRequiredService<IManifestRevisionQuery>().FindAsync(imported.ManifestRevisionRef))!.Declaration;
        }
        var forward = await Import(false); var reverse = await Import(true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var owner = new CaptureScopeSignal(pause: true); var contender = new CaptureScopeSignal(pause: false);
        var planner = services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>();
        async Task<ExactProviderResolutionSet> Capture(NormalizedApplicationManifest manifest, CaptureScopeSignal signal)
        {
            await using var db = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>()
                .UseNpgsql(scenario.Connection).AddInterceptors(signal).Options);
            await using var transaction = await db.Database.BeginTransactionAsync(deadline.Token);
            var result = await planner.CaptureInternalProvidersAsync(db, manifest, scenario.Environment, [], deadline.Token);
            await transaction.CommitAsync(deadline.Token);
            return result;
        }
        var first = Capture(forward, owner); Task<ExactProviderResolutionSet>? second = null;
        var completed = false;
        try
        {
            await owner.Acquired.Task.WaitAsync(deadline.Token);
            second = Capture(reverse, contender);
            await contender.Requested.Task.WaitAsync(deadline.Token);
            // Before the correction, the reversed mapping requests secondary first while
            // the owner holds primary: releasing the owner would create a lock cycle.
            Assert.Equal($"haby-placement:{Guid.Parse(scenario.Environment.ToString()):N}:{primary.Alias.Value}", owner.FirstKey);
            Assert.Equal(owner.FirstKey, contender.FirstKey);
            Assert.Equal(new[] { primary.Alias.Value, secondaryAlias.Value }, owner.ProviderAliases);
            Assert.Equal(new[] { primary.Alias.Value, secondaryAlias.Value }, contender.ProviderAliases);
            await using var observer = new NpgsqlConnection(scenario.Connection);
            await observer.OpenAsync(deadline.Token);
            while (true)
            {
                Assert.False(second.IsCompleted);
                await using var command = new NpgsqlCommand("SELECT @owner = ANY(pg_blocking_pids(@contender))", observer);
                command.Parameters.AddWithValue("owner", owner.BackendPid);
                command.Parameters.AddWithValue("contender", contender.BackendPid);
                if (await command.ExecuteScalarAsync(deadline.Token) is true) break;
            }
            owner.Release.TrySetResult();
            var results = await Task.WhenAll(first, second).WaitAsync(deadline.Token);
            foreach (var result in results)
            {
                Assert.Equal(2, result.Entries.Count);
                foreach (var (component, declaration) in result.Declaration.Spec.Components)
                {
                    var input = new ProviderInput.Workload(component, ManifestLocalId.Create("main"));
                    var expected = declaration.Workloads[ManifestLocalId.Create("main")].ProviderRef == primary.Alias ? primary : secondary;
                    var actual = result.Entries[input];
                    Assert.Equal(input, actual.Input); Assert.Equal(scenario.Environment, actual.Environment); Assert.Null(actual.Naming);
                    Assert.Equal(expected.Alias, actual.Alias); Assert.Equal(expected.Binding.InstanceId, actual.Instance);
                    Assert.Equal(expected.Binding.RevisionNumber, actual.Configuration); Assert.Equal(expected.ImplementationId, actual.Implementation.Value);
                    var frozen = InternalPlacementInput.Decode(actual.Placement!);
                    await using var verify = await factory.CreateDbContextAsync(deadline.Token);
                    var stored = await verify.PlacementRevisions.AsNoTracking().SingleAsync(x => x.Id == frozen.Id, deadline.Token);
                    Assert.Equal(expected.Alias.Value, stored.ProviderAlias); Assert.Equal(stored.Payload, frozen.Declaration.Payload);
                    Assert.Equal(stored.Name, frozen.Declaration.Name.Value); Assert.Equal(stored.Revision, frozen.Declaration.Revision);
                    Assert.Equal(stored.Contract, frozen.Declaration.Contract); Assert.Equal(stored.FormatVersion, frozen.Declaration.FormatVersion);
                }
            }
            completed = true;
        }
        finally
        {
            if (!completed) await deadline.CancelAsync();
            owner.Release.TrySetResult();
            try { await Task.WhenAll(second is null ? [first] : new[] { first, second }); }
            catch when (!completed) { }
        }
    }

    private sealed class CaptureScopeSignal(bool pause) : DbCommandInterceptor
    {
        internal TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? FirstKey { get; private set; }
        internal int BackendPid { get; private set; }
        internal List<string> ProviderAliases { get; } = [];
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM provider_aliases", StringComparison.Ordinal) && command.CommandText.Contains("FOR SHARE", StringComparison.Ordinal))
                ProviderAliases.Add(command.Parameters.Cast<DbParameter>().Select(x => x.Value).OfType<string>().Single());
            if (FirstKey is null && command.CommandText.Contains("pg_advisory_xact_lock(", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Select(x => x.Value).OfType<string>().FirstOrDefault(x => x.StartsWith("haby-placement:", StringComparison.Ordinal)) is { } key)
            {
                FirstKey = key; BackendPid = ((NpgsqlConnection)command.Connection!).ProcessID; Requested.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (FirstKey is not null && command.Parameters.Cast<DbParameter>().Any(x => Equals(x.Value, FirstKey)) && Acquired.TrySetResult() && pause)
                await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
