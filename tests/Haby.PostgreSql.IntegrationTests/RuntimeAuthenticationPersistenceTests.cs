using Haby.Application;
using Haby.Persistence.PostgreSql;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
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
        internal int Commands, DocumentQueries;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            if (command.CommandText.Contains("configuration_document_revisions", StringComparison.Ordinal) ||
                command.CommandText.Contains("workload_configuration_recipients", StringComparison.Ordinal))
            { DocumentQueries++; throw new InvalidOperationException("Authentication attempted a document lookup."); }
            return ValueTask.FromResult(result);
        }
    }
}
