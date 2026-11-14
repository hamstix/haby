using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Kubernetes.Contracts;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using System.Data.Common;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("invalid-length")]
    public async Task Mixed_authentication_origin_ignores_application_payload_but_normal_preparation_publication_and_effects_stay_guarded(string fault)
    {
        using var baseline = new RuntimeAuthenticationLoginScenario(fixture);
        var scenario = await baseline.PrepareMixedAsync();
        var identity = new IdentityFixture();
        await using var services = ApplicationLoginScenario.Services(scenario.Connection, full: true, access: true, internalActions: true);
        await ConfigurationPreparation(services, rendering: PostgreSqlConnectionRendering.CreateCatalog(),
            formatters: PostgreSqlConnectionRendering.FormatterCatalog()).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        var prepared = Assert.Single(await IdentityPreparation(services, identity).PrepareAsync(scenario.Commit, default));
        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var publication = Publications(services, identity);
        var request = Assert.Single(await publication.PreparationRequestsAsync(scenario.Commit, "tester", default));
        var original = (await new PostgreSqlRuntimeIdentityStore(factory, IdentityActions(services, identity)).ReadAsync(prepared.Binding!.Value, default))!;
        Assert.True(baseline.OwnsCatalog(scenario.Connection), "Corruption requires this case's owned catalog.");
        await using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(scenario.Connection) { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var arrange = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
            await using var corrupt = await arrange.Database.BeginTransactionAsync();
            // LOCAL resets the corruption override before act and connection close.
            await arrange.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
            var versions = await arrange.GeneratedCredentialSlots.Select(x => x.VersionId).ToArrayAsync();
            Assert.NotEmpty(versions); // A genuine mixed Resource/application-login contour, not an empty stage.
            if (fault == "missing") Assert.Equal(1, await arrange.SecretPayloads.Where(x => versions.Contains(x.SecretVersionId)).ExecuteDeleteAsync());
            else Assert.Equal(1, await arrange.SecretPayloads.Where(x => versions.Contains(x.SecretVersionId)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Bytes, new byte[] { 1 })));
            await corrupt.CommitAsync();
        }
        await using (var verifyCorruption = await factory.CreateDbContextAsync())
        {
            var versions = await verifyCorruption.GeneratedCredentialSlots.Select(x => x.VersionId).ToArrayAsync();
            Assert.Single(versions);
            var lengths = await verifyCorruption.SecretPayloads.Where(x => versions.Contains(x.SecretVersionId)).Select(x => x.Bytes.Length).ToArrayAsync();
            if (fault == "missing") Assert.Empty(lengths);
            else Assert.Equal(1, Assert.Single(lengths));
            await AssertAuthenticationCorruptionGuardsRestoredAsync(verifyCorruption);
        }

        // The exact pre-fix helper remains required by normal paths. This independent SQL
        // oracle detects its application-payload query, without returning material bytes.
        var oldOracle = new NoAuthenticationDocuments();
        IDbContextFactory<ManifestCatalogDbContext> oldFactory = new AuthenticationContextFactory(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(scenario.Connection).AddInterceptors(oldOracle).Options);
        await using (var counterfactual = await oldFactory.CreateDbContextAsync())
        {
            var metadata = await DurableAggregateCompletion.InspectMetadataAsync(counterfactual,
                Guid.Parse(scenario.Commit.DeploymentId.ToString()), scenario.Commit.Generation.Value,
                services.GetServices<IExternalOperationOutputContract>(), services.GetServices<IProviderConfigurationCodec>(), default,
                services.GetRequiredService<DurableActionAdmission>(), internalResourceStage: true);
            Assert.NotNull(metadata); Assert.NotEmpty(metadata.Credentials);
            await Assert.ThrowsAsync<AuthenticationMaterialQueryException>(() => DurableAggregateCompletion.CheckCredentialMetadataAsync(counterfactual, metadata, default));
            Assert.Equal(1, oldOracle.ApplicationPayloadQueries);
        }

        var oracle = new NoAuthenticationDocuments();
        var isolated = new AuthenticationContextFactory(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(scenario.Connection).AddInterceptors(oracle).Options);
        var store = new PostgreSqlRuntimeIdentityStore(isolated, IdentityActions(services, identity));
        var adapter = new AuthenticationProbe(original); adapter.Continue.TrySetResult();
        using var limits = new RuntimeAuthenticationAdmission(new(), TimeProvider.System);
        var authentication = new RuntimeAuthentication(store, [adapter], limits, TimeProvider.System);
        var result = await authentication.AuthenticateAsync(original.Binding, "projected"u8.ToArray(), default);
        Assert.Equal(RuntimeAuthenticationOutcome.Authenticated, result.Outcome); Assert.NotNull(result.Context);
        Assert.Equal(1, adapter.Calls); Assert.True(oracle.Commands > 0);
        Assert.Equal(0, oracle.DocumentQueries); Assert.Equal(0, oracle.ApplicationPayloadQueries);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => IdentityPreparation(services, identity).PrepareAsync(scenario.Commit, default));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => publication.ApplyAsync(request, default));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => IdentityActions(services, identity).AcceptAsync(scenario.Commit, RuntimeSubject,
            identity.Contract.Action, "mixed-effect-after-payload-fault", default).AsTask());
        Assert.Equal(1, identity.Writes);
        await publication.ApplyAsync(new("mixed-payload-binding-revoke", RuntimePublicationOperation.RevokeBinding, original.Binding,
            null, prepared.CurrentBinding!.Token, "operator"), default);
        Assert.Equal(RuntimeAuthenticationOutcome.PermissionDenied,
            (await authentication.AuthenticateAsync(original.Binding, "replacement"u8.ToArray(), default)).Outcome);
        Assert.Equal(1, adapter.Calls); Assert.Equal(0, oracle.ApplicationPayloadQueries); Assert.Equal(0, oracle.DocumentQueries);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
        Assert.Empty(await verify.RuntimeConfigurationPermissions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData("slot")]
    [InlineData("version")]
    [InlineData("owner")]
    [InlineData("audit")]
    public async Task Mixed_authentication_origin_still_requires_exact_application_credential_metadata(string fault)
    {
        using var baseline = new RuntimeAuthenticationLoginScenario(fixture);
        var scenario = await baseline.PrepareMixedAsync();
        var identity = new IdentityFixture();
        await using var services = ApplicationLoginScenario.Services(scenario.Connection, full: true, access: true, internalActions: true);
        await ConfigurationPreparation(services, rendering: PostgreSqlConnectionRendering.CreateCatalog(),
            formatters: PostgreSqlConnectionRendering.FormatterCatalog()).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        var prepared = Assert.Single(await IdentityPreparation(services, identity).PrepareAsync(scenario.Commit, default));
        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var original = (await new PostgreSqlRuntimeIdentityStore(factory, IdentityActions(services, identity)).ReadAsync(prepared.Binding!.Value, default))!;
        Assert.True(baseline.OwnsCatalog(scenario.Connection), "Corruption requires this case's owned catalog.");
        await using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(scenario.Connection) { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var arrange = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
            await using var corrupt = await arrange.Database.BeginTransactionAsync();
            await arrange.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
            var slots = await arrange.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync(); Assert.NotEmpty(slots);
            var versions = slots.Select(x => x.VersionId).ToArray(); var secrets = slots.Select(x => x.SecretId).ToArray();
            if (fault == "slot") Assert.Equal(1, await arrange.GeneratedCredentialSlots.ExecuteDeleteAsync());
            else if (fault == "version") Assert.Equal(1, await arrange.SecretVersions.Where(x => versions.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Number, 2)));
            else if (fault == "owner") Assert.Equal(1, await arrange.Secrets.Where(x => secrets.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResourceLocalId, "foreign-resource")));
            else Assert.Equal(1, await arrange.GeneratedCredentialCreationAudits.Where(x => secrets.Contains(x.SecretId)).ExecuteDeleteAsync());
            await corrupt.CommitAsync();
        }
        await using (var verifyCorruption = await factory.CreateDbContextAsync())
        {
            if (fault == "slot") Assert.Empty(await verifyCorruption.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
            else if (fault == "version") Assert.Equal(2, await verifyCorruption.SecretVersions.Where(x => verifyCorruption.GeneratedCredentialSlots.Any(s => s.VersionId == x.Id)).Select(x => x.Number).SingleAsync());
            else if (fault == "owner") Assert.Equal("foreign-resource", await verifyCorruption.Secrets.Where(x => x.Purpose == "ApplicationLogin").Select(x => x.ResourceLocalId).SingleAsync());
            else Assert.Empty(await verifyCorruption.GeneratedCredentialCreationAudits.AsNoTracking().ToArrayAsync());
            await AssertAuthenticationCorruptionGuardsRestoredAsync(verifyCorruption);
        }
        var oracle = new NoAuthenticationDocuments();
        var isolated = new AuthenticationContextFactory(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(scenario.Connection).AddInterceptors(oracle).Options);
        var adapter = new AuthenticationProbe(original); adapter.Continue.TrySetResult();
        using var limits = new RuntimeAuthenticationAdmission(new(), TimeProvider.System);
        var result = await new RuntimeAuthentication(new PostgreSqlRuntimeIdentityStore(isolated, IdentityActions(services, identity)), [adapter], limits, TimeProvider.System)
            .AuthenticateAsync(original.Binding, "projected"u8.ToArray(), default);
        Assert.Equal(RuntimeAuthenticationOutcome.Unauthenticated, result.Outcome); Assert.Null(result.Context);
        Assert.Equal(0, adapter.Calls); Assert.Equal(0, oracle.ApplicationPayloadQueries); Assert.Equal(0, oracle.DocumentQueries);
    }

    [Fact]
    public async Task Established_identity_authenticates_without_document_queries_while_new_preparation_and_publication_stay_guarded()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e));
        var identity = new IdentityFixture(); await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        var prepared = Assert.Single(await IdentityPreparation(services, identity).PrepareAsync(scenario.Commit, default));
        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var publication = Publications(services, identity);
        var request = Assert.Single(await publication.PreparationRequestsAsync(scenario.Commit, "tester", default));
        await using (var arrange = await factory.CreateDbContextAsync())
            await arrange.ConfigurationDocuments.ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "invalid-current-document"));
        var noDocuments = new NoAuthenticationDocuments();
        var isolated = new AuthenticationContextFactory(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(scenario.Connection)
            .AddInterceptors(noDocuments).Options);
        var store = new PostgreSqlRuntimeIdentityStore(isolated, IdentityActions(services, identity));
        var trust = (await store.ReadAsync(prepared.Binding!.Value, default))!;
        var adapter = new AuthenticationProbe(trust); adapter.Continue.TrySetResult();
        using var limits = new RuntimeAuthenticationAdmission(new(), TimeProvider.System);
        Assert.Equal(RuntimeAuthenticationOutcome.Authenticated,
            (await new RuntimeAuthentication(store, [adapter], limits, TimeProvider.System).AuthenticateAsync(trust.Binding, "projected"u8.ToArray(), default)).Outcome);
        Assert.True(noDocuments.Commands > 0); Assert.Equal(0, noDocuments.DocumentQueries);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => IdentityPreparation(services, identity).PrepareAsync(scenario.Commit, default));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => publication.ApplyAsync(request, default));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => IdentityActions(services, identity).AcceptAsync(scenario.Commit, RuntimeSubject,
            identity.Contract.Action, "new-effect-after-corruption", default).AsTask());
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Empty(await verify.RuntimeConfigurationPermissions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Runtime_authentication_reloads_original_persisted_identity_and_denies_completed_inflight_revocation()
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e));
        var identity = new IdentityFixture();
        RuntimeTrust trust;
        await using (var services = InternalServices(scenario.Connection, scenario.Time))
        {
            await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
            var prepared = Assert.Single(await IdentityPreparation(services, identity).PrepareAsync(scenario.Commit, default));
            var store = new PostgreSqlRuntimeIdentityStore(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), IdentityActions(services, identity));
            trust = (await store.ReadAsync(prepared.Binding!.Value, default))!;
            Assert.NotNull(trust); Assert.False(trust.Revoked);
            Assert.Equal("haby.kubernetes.token-review/v1", trust.Authenticator);
        }
        await using var restarted = InternalServices(scenario.Connection, scenario.Time);
        var current = new PostgreSqlRuntimeIdentityStore(restarted.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), IdentityActions(restarted, identity));
        Assert.Equal(trust, await current.ReadAsync(trust.Binding, default));
        var adapter = new AuthenticationProbe(trust);
        using var limits = new RuntimeAuthenticationAdmission(new(), TimeProvider.System);
        var authentication = new RuntimeAuthentication(current, [adapter], limits, TimeProvider.System);
        var waiting = authentication.AuthenticateAsync(trust.Binding, "projected-credential"u8.ToArray(), default).AsTask();
        await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        RuntimeBindingState binding;
        await using (var arrange = await restarted.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync())
            binding = InternalRuntimePublication.BindingState(await arrange.RuntimeIdentityBindings.AsNoTracking().SingleAsync());
        await Publications(restarted, identity).ApplyAsync(new("inflight-authentication-revoke", RuntimePublicationOperation.RevokeBinding,
            trust.Binding, null, binding.Token, "operator"), default);
        adapter.Continue.TrySetResult();
        Assert.Equal(RuntimeAuthenticationOutcome.PermissionDenied, (await waiting).Outcome);
        Assert.False(await current.IsCurrentAsync(trust, default));
        Assert.Equal(RuntimeAuthenticationOutcome.PermissionDenied,
            (await authentication.AuthenticateAsync(trust.Binding, "rotated-credential"u8.ToArray(), default)).Outcome);
        Assert.Equal(1, adapter.Calls);
        await using var verify = await restarted.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verify.RuntimeConfigurationPermissions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
        Assert.Equal(trust.Payload, (await verify.RuntimeIdentityBindings.AsNoTracking().SingleAsync()).ValidationPayload);
    }

    [Theory]
    [InlineData("audience")]
    [InlineData("authenticator")]
    [InlineData("validation")]
    [InlineData("inventory")]
    [InlineData("receipt")]
    [InlineData("attempt")]
    [InlineData("checkpoint")]
    [InlineData("input-pin")]
    [InlineData("intent")]
    public async Task Corrupt_saved_runtime_identity_fails_before_remote_authentication(string fault)
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e));
        var identity = new IdentityFixture(); await using var services = InternalServices(scenario.Connection, scenario.Time);
        await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default);
        var prepared = Assert.Single(await IdentityPreparation(services, identity).PrepareAsync(scenario.Commit, default));
        var factory = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var store = new PostgreSqlRuntimeIdentityStore(factory, IdentityActions(services, identity));
        var adapter = new AuthenticationProbe((await store.ReadAsync(prepared.Binding!.Value, default))!);
        await using (var arrange = await factory.CreateDbContextAsync())
        {
            if (fault == "audience") await arrange.RuntimeIdentityBindings.ExecuteUpdateAsync(s => s.SetProperty(x => x.Audience, "foreign"));
            else if (fault == "authenticator") await arrange.RuntimeIdentityBindings.ExecuteUpdateAsync(s => s.SetProperty(x => x.Authenticator, "unsupported"));
            else if (fault == "validation") await arrange.RuntimeIdentityBindings.ExecuteUpdateAsync(s => s.SetProperty(x => x.ValidationPayload, "{}"));
            else if (fault == "receipt") await arrange.OperationOutputs.Where(x => x.Key == "receipt").ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{}"));
            else if (fault == "attempt") await arrange.DurableOperationAttempts.Where(x => x.OperationId.StartsWith("effect-runtime-")).ExecuteUpdateAsync(s => s.SetProperty(x => x.Outcome, "Uncertain"));
            else if (fault == "checkpoint") await arrange.GenerationStageCheckpoints.ExecuteUpdateAsync(s => s.SetProperty(x => x.EvidenceJson, "{}"));
            else if (fault == "input-pin") await arrange.InternalPlanInputs.ExecuteDeleteAsync();
            else if (fault == "intent") await arrange.ActionIntents.ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{}"));
            else await arrange.OwnedObjects.ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{}"));
        }
        using var limits = new RuntimeAuthenticationAdmission(new(), TimeProvider.System);
        Assert.Equal(RuntimeAuthenticationOutcome.Unauthenticated,
            (await new RuntimeAuthentication(store, [adapter], limits, TimeProvider.System).AuthenticateAsync(prepared.Binding.Value, "projected"u8.ToArray(), default)).Outcome);
        Assert.Equal(0, adapter.Calls);
    }

    // A narrow bridge to the existing pair fixture, using the same owned server.
    // Application credentials are created ONLY by its normal Plan/Commit/Execute.
    internal sealed class RuntimeAuthenticationLoginScenario(PostgreSqlFixture fixture) : ApplicationLoginScenario(fixture)
    {
        private string? ownedCatalog;
        internal bool OwnsCatalog(string connection) => ownedCatalog is not null && connection == ownedCatalog;

        internal async Task<InternalScenario> PrepareMixedAsync(
            Func<ISecretStore, EnvironmentRecordId, Task<KubernetesProviderConfiguration>>? suppliedConfiguration = null, string namespaceName = "test",
            Func<ISecretStore, EnvironmentRecordId, ProviderAliasBinding, KubernetesProviderConfiguration, CancellationToken, ValueTask<ProviderPreflightResult>>? actualProbe = null)
        {
            var baseline = await Prepare(full: true, render: true);
            ownedCatalog = baseline.Connection;
            await using (var execute = ApplicationLoginScenario.Services(baseline.Connection, full: true, access: true))
                Assert.Null((await execute.GetRequiredService<DurableGenerationRunner>().RunAsync(baseline.Commit.DeploymentId, baseline.Commit.Generation, default)).StopOutcome);
            ProviderAliasBinding? exactProvider = null;
            Haby.Tests.InternalKubernetesFixtureProbe? probe = actualProbe is null ? null : (secrets, scopeEnvironment, scopeConfiguration, token) =>
                actualProbe(secrets, scopeEnvironment, exactProvider ?? throw new InvalidDataException(), scopeConfiguration, token);
            await using var services = ApplicationLoginScenario.Services(baseline.Connection, full: true, access: true, internalActions: true, kubernetesProbe: probe);
            var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
            await using (var verify = await contexts.CreateDbContextAsync())
            {
                Assert.Equal(1, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
                var slot = Assert.Single(await verify.GeneratedCredentialSlots.AsNoTracking().ToArrayAsync());
                var output = await verify.OperationOutputs.AsNoTracking().SingleAsync(x => x.OperationId == "app-login-material");
                var reference = GeneratedCredentialOutputCodec.Decode(output.Contract, output.Payload)!.Reference;
                Assert.Equal(slot.SecretId.ToString(), reference.SecretId.ToString());
                Assert.Equal(slot.VersionId.ToString(), reference.SecretVersionId.ToString());
                Assert.Equal("Succeeded", (await verify.DurableOperations.AsNoTracking().SingleAsync(x => x.OperationId == "app-login-material")).State);
            }
            var environment = baseline.Plan.Inputs.Environment;
            var admin = new SecretRef(SecretId.New(), SecretVersionId.New());
            await services.GetRequiredService<ISecretStore>().CreateAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator),
                admin, "fixture-token"u8.ToArray(), default);
            var configuration = suppliedConfiguration is null ? new KubernetesProviderConfiguration(new Uri("https://127.0.0.1:6443"), null, admin, null, null) :
                await suppliedConfiguration(services.GetRequiredService<ISecretStore>(), environment);
            var providers = services.GetRequiredService<IProviderConfigurationStore>();
            var provider = await providers.CreateInstanceAsync(environment, configuration, default);
            await providers.AddRevisionAsync(environment, provider, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
            exactProvider = new(provider, ProviderConfigurationRevisionNumber.Create(1));
            var alias = ProviderAlias.Create("kubernetes.primary");
            Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment,
                alias, null, new(provider, ProviderConfigurationRevisionNumber.Create(1)), "tester", default)).Outcome);
            var codec = new KubernetesPlacementCodec();
            var placement = codec.Decode(Encoding.UTF8.GetBytes("""{"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"standard","revision":1},"spec":{"namespace":"NAMESPACE"}}""".Replace("NAMESPACE", namespaceName, StringComparison.Ordinal)));
            var placementKey = new PlacementRevisionKey(environment, alias, placement.Name, placement.Revision);
            await new PostgreSqlPlacementRevisionStore(contexts, codec).CompareAndCreateAsync(placementKey, placement, new("tester", DateTimeOffset.UtcNow), default);
            await new PostgreSqlPlacementSelectionStore(contexts, codec).ApplyAsync(PlacementSelectionTarget.Create("environment", "", "", ""), placementKey,
                new PlacementSelectionPrecondition.ExpectedAbsence(), new("tester", DateTimeOffset.UtcNow, null), default);
            var envelope = UpdateEnvelope(baseline, 2);
            envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"] = JsonNode.Parse("""{"main":{"type":"kubernetes.workload/v1alpha1","providerRef":"kubernetes.primary","properties":{"image":{"reference":"registry.example/app:v2"},"configuration":{"documents":["settings"]}}}}"""u8);
            var catalog = new ManifestContractCatalog([PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition],
                [KubernetesContract.Definition], [new(OutputContractId.Create(PostgreSqlApplicationConnectionContract.Contract), PostgreSqlConsumerOptions.Schema)],
                [new(PostgreSqlConnectionRendering.Renderer)]);
            var imported = Assert.IsType<ImportApplicationManifestResult.Succeeded>(await new ImportApplicationManifest(
                services.GetRequiredService<IManifestImportStore>(), catalog, TimeProvider.System).ExecuteAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), "tester"));
            var accepted = await new InternalAcceptedGenerationBuilder(services.GetRequiredService<PostgreSqlDurableDeploymentPlanner>(),
                (PostgreSqlDurableDesiredStateCommitter)services.GetRequiredService<IDurableDesiredStateCommitter>(), services.GetServices<IInternalActionHandler>().ToArray())
                .AcceptForEffectAsync(environment, imported.ManifestRevisionRef.ApplicationRecordId, imported.ManifestRevisionRef, baseline.Commit.Generation, "tester", default);
            return new(baseline.Connection, TimeProvider.System, environment, accepted, imported.ManifestRevisionRef);
        }
    }

    private static async Task AssertAuthenticationCorruptionGuardsRestoredAsync(ManifestCatalogDbContext fresh)
    {
        await fresh.Database.OpenConnectionAsync();
        await using var command = fresh.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SHOW session_replication_role";
        Assert.Equal("origin", await command.ExecuteScalarAsync());
        // A no-op update must still hit the original immutable-version trigger.
        Assert.True(await fresh.SecretVersions.AnyAsync());
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => fresh.SecretVersions.ExecuteUpdateAsync(s => s.SetProperty(x => x.Number, x => x.Number)));
        Assert.Equal("P0001", refusal.SqlState);
    }

    private sealed class AuthenticationProbe(RuntimeTrust trust) : IRuntimeAuthenticator
    {
        internal int Calls;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RuntimeAuthenticatorContract Contract => new(trust.Authenticator, trust.Contract, trust.Format, trust.ProviderKind,
            trust.ConfigurationShape, trust.ConfigurationFormat, trust.Implementation);
        public bool Validate(RuntimeTrust saved) => saved with { Revoked = false } == trust;
        public async ValueTask<RuntimeAuthenticationOutcome> AuthenticateAsync(RuntimeTrust saved, ReadOnlyMemory<byte> credential, CancellationToken token)
        { Calls++; Entered.TrySetResult(); await Continue.Task.WaitAsync(token); return RuntimeAuthenticationOutcome.Authenticated; }
    }

    private sealed class AuthenticationContextFactory(DbContextOptions<ManifestCatalogDbContext> options) : IDbContextFactory<ManifestCatalogDbContext>
    {
        public ManifestCatalogDbContext CreateDbContext() => new(options);
    }
    private sealed class NoAuthenticationDocuments : DbCommandInterceptor
    {
        internal int Commands, DocumentQueries, ApplicationPayloadQueries;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            if (command.CommandText.Contains("configuration_document_revisions", StringComparison.Ordinal) ||
                command.CommandText.Contains("workload_configuration_recipients", StringComparison.Ordinal))
            { DocumentQueries++; throw new InvalidOperationException("Authentication attempted a document lookup."); }
            if (command.CommandText.Contains("secret_payloads", StringComparison.Ordinal))
            { ApplicationPayloadQueries++; throw new AuthenticationMaterialQueryException(); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class AuthenticationMaterialQueryException : InvalidOperationException;
}
