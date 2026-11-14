using System.Data.Common;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Configuration.Contracts;
using Haby.Domain;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class DurableDeploymentPlanningTests
{
    private async Task<(InternalScenario Scenario, RuntimePublicationRequest Request, RuntimePublicationResult Published, RuntimeAuthenticatedContext Identity)>
        RuntimeDeliverySetup(bool publish = true, bool shared = false)
    {
        var scenario = await InternalScenarioAsync(initialOnly: true, configureManifest: e => ConfiguredDocuments(e, shared: shared));
        await using var services = InternalServices(scenario.Connection, scenario.Time);
        var handler = new IdentityFixture();
        Assert.Null((await ConfigurationPreparation(services).Preparation.PrepareAsync(scenario.Commit, "tester", default)).Failure);
        await IdentityPreparation(services, handler).PrepareAsync(scenario.Commit, default);
        var publication = Publications(services, handler);
        var request = (await publication.PreparationRequestsAsync(scenario.Commit, "tester", default))[0];
        var published = publish ? await publication.ApplyAsync(request, default) : null;
        var trust = (await new PostgreSqlRuntimeIdentityStore(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), IdentityActions(services, handler))
            .ReadAsync(request.Binding, default))!;
        return (scenario, request, published!, new(trust.Binding, trust.Environment, trust.ManagedWorkload, trust.Provider));
    }

    private sealed class RuntimeMetadataQueries : DbCommandInterceptor
    {
        internal bool Violation;
        internal int DocumentQueries;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("configuration_document_revisions", StringComparison.Ordinal))
            {
                DocumentQueries++;
                if (command.CommandText.Contains("payload", StringComparison.Ordinal)) Violation = true;
            }
            if (command.CommandText.Contains("secret_payloads", StringComparison.Ordinal) ||
                command.CommandText.Contains("configuration_json", StringComparison.Ordinal) ||
                command.CommandText.Contains("graph_json", StringComparison.Ordinal)) Violation = true;
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Runtime_authorization_projects_only_metadata_and_binds_all_authenticated_owner_ids()
    {
        var setup = await RuntimeDeliverySetup();
        var oracle = new RuntimeMetadataQueries();
        var contexts = new AuthenticationContextFactory(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(setup.Scenario.Connection).AddInterceptors(oracle).Options);
        var store = new PostgreSqlRuntimeConfigurationStore(contexts);
        var revision = ConfigurationDocumentRevisionId.Parse(setup.Request.Revision!.Value.ToString());
        Assert.NotNull(await store.AuthorizeAsync(setup.Identity, revision, default));
        foreach (var foreign in new[] { setup.Identity with { Environment = Guid.NewGuid() }, setup.Identity with { ManagedWorkload = Guid.NewGuid() },
            setup.Identity with { Provider = Guid.NewGuid() }, setup.Identity with { Binding = Guid.NewGuid() } })
            Assert.Null(await store.AuthorizeAsync(foreign, revision, default));
        Assert.Null(await store.AuthorizeAsync(setup.Identity, ConfigurationDocumentRevisionId.New(), default));
        Assert.True(oracle.DocumentQueries > 0); Assert.False(oracle.Violation);
    }

    [Fact]
    public async Task Runtime_exact_permission_shared_binding_and_persisted_revocation_have_one_read_boundary()
    {
        var setup = await RuntimeDeliverySetup(shared: true);
        await using var services = InternalServices(setup.Scenario.Connection, setup.Scenario.Time);
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>();
        var store = new PostgreSqlRuntimeConfigurationStore(contexts);
        var revision = ConfigurationDocumentRevisionId.Parse(setup.Request.Revision!.Value.ToString());
        var authority = (await store.AuthorizeAsync(setup.Identity, revision, default))!;
        var other = await new PostgreSqlRuntimeIdentityStore(contexts, IdentityActions(services, new IdentityFixture())).ReadAsync(
            (await Publications(services, new IdentityFixture()).PreparationRequestsAsync(setup.Scenario.Commit, "tester", default))[1].Binding, default);
        Assert.Null(await store.AuthorizeAsync(new(other!.Binding, other.Environment, other.ManagedWorkload, other.Provider), revision, default));
        var publication = Publications(services, new IdentityFixture());
        await publication.ApplyAsync(Revoke(setup.Request, setup.Published.CurrentPermission!.Token), default);
        var artifact = await store.ReadAsync(authority, default); Assert.NotNull(artifact);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.ReadAsync(authority, default));
        Assert.Null(await store.AuthorizeAsync(setup.Identity, revision, default));
        await publication.ApplyAsync(setup.Request, default);
        Assert.Null(await store.AuthorizeAsync(setup.Identity, revision, default));
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("payload")]
    [InlineData("source")]
    [InlineData("recipient")]
    public async Task Runtime_authorized_reader_refuses_corrupt_exact_artifact_without_regeneration(string fault)
    {
        var setup = await RuntimeDeliverySetup(); await using var services = InternalServices(setup.Scenario.Connection, setup.Scenario.Time);
        var contexts = services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(); var store = new PostgreSqlRuntimeConfigurationStore(contexts);
        var authority = (await store.AuthorizeAsync(setup.Identity, ConfigurationDocumentRevisionId.Parse(setup.Request.Revision!.Value.ToString()), default))!;
        var deployment = Guid.Parse(setup.Scenario.Commit.DeploymentId.ToString());
        var generation = setup.Scenario.Commit.Generation.Value;
        if (fault == "source")
        {
            // RuntimeDeliverySetup creates a fresh disposable catalog for this case.
            await using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(setup.Scenario.Connection) { Pooling = false }.ConnectionString))
            {
                await connection.OpenAsync();
                await using var arrange = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
                await using var corrupt = await arrange.Database.BeginTransactionAsync();
                await arrange.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
                Assert.Equal(1, await arrange.DeploymentGenerations.Where(x => x.DeploymentId == deployment && x.Generation == generation)
                    .ExecuteUpdateAsync(x => x.SetProperty(r => r.ConfigurationJson, "{}")));
                await corrupt.CommitAsync();
            }
            await using var verifyCorruption = await contexts.CreateDbContextAsync();
            await AssertRuntimeSourceCorruptionGuardsRestoredAsync(verifyCorruption, deployment, generation);
        }
        else await using (var arrange = await contexts.CreateDbContextAsync())
        {
            if (fault == "fingerprint") await arrange.ConfigurationDocuments.ExecuteUpdateAsync(x => x.SetProperty(r => r.Fingerprint, "corrupt"));
            if (fault == "payload") await arrange.ConfigurationDocuments.ExecuteUpdateAsync(x => x.SetProperty(r => r.Payload, "[]"));
            if (fault == "recipient") await arrange.WorkloadConfigurationRecipients.ExecuteDeleteAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.ReadAsync(authority, default));
        await using var verify = await contexts.CreateDbContextAsync();
        if (fault == "source") await AssertRuntimeSourceCorruptionGuardsRestoredAsync(verify, deployment, generation);
        Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }

    private static async Task AssertRuntimeSourceCorruptionGuardsRestoredAsync(ManifestCatalogDbContext fresh, Guid deployment, long generation)
    {
        await fresh.Database.OpenConnectionAsync();
        await using var command = fresh.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SHOW session_replication_role";
        Assert.Equal("origin", await command.ExecuteScalarAsync());
        var source = fresh.DeploymentGenerations.Where(x => x.DeploymentId == deployment && x.Generation == generation);
        Assert.Equal("{}", await source.AsNoTracking().Select(x => x.ConfigurationJson).SingleAsync());
        // Even a no-op update must be refused by the original generation immutability trigger.
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => source.ExecuteUpdateAsync(x => x.SetProperty(r => r.ConfigurationJson, r => r.ConfigurationJson)));
        Assert.Equal("P0001", refusal.SqlState);
    }

    private sealed class RuntimeRpcIdentity : IRuntimeAuthenticator
    {
        public RuntimeAuthenticatorContract Contract => new("haby.kubernetes.token-review/v1", "haby.kubernetes.runtime-validation/v1", 1,
            Haby.Kubernetes.KubernetesProviderConfigurationCodec.Kind, Haby.Kubernetes.KubernetesProviderConfigurationCodec.Shape, 1,
            Haby.Kubernetes.KubernetesProviderConfigurationCodec.Implementation);
        public bool Validate(RuntimeTrust trust) => trust.Authenticator == Contract.Authenticator;
        public ValueTask<RuntimeAuthenticationOutcome> AuthenticateAsync(RuntimeTrust trust, ReadOnlyMemory<byte> credential, CancellationToken token) =>
            ValueTask.FromResult(credential.Span.SequenceEqual("runtime-test"u8) ? RuntimeAuthenticationOutcome.Authenticated : RuntimeAuthenticationOutcome.Unauthenticated);
    }

    private sealed class RuntimeRpcOrder : DbCommandInterceptor
    {
        internal bool InAuthorization, Authorized, Violation;
        internal int SizeProbes, BodyReads;
        internal void BeginRequest() { InAuthorization = false; Authorized = false; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var document = command.CommandText.Contains("configuration_document_revisions", StringComparison.Ordinal);
            var body = document && command.CommandText.Contains("payload", StringComparison.Ordinal);
            var material = command.CommandText.Contains("secret_payloads", StringComparison.Ordinal);
            if (document && !InAuthorization || (body || material) && !Authorized) Violation = true;
            if (body)
            {
                if (command.CommandText.Contains("length(", StringComparison.Ordinal)) SizeProbes++;
                else BodyReads++;
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class RuntimeRpcStore(IRuntimeConfigurationStore inner, RuntimeRpcOrder order) : IRuntimeConfigurationStore
    {
        public async ValueTask<RuntimeConfigurationAuthority?> AuthorizeAsync(RuntimeAuthenticatedContext identity, ConfigurationDocumentRevisionId revision, CancellationToken token)
        {
            order.InAuthorization = true;
            var authority = await inner.AuthorizeAsync(identity, revision, token);
            order.Authorized = authority is not null;
            return authority;
        }
        public ValueTask<ConfigurationDeliveryArtifact?> ReadAsync(RuntimeConfigurationAuthority authority, CancellationToken token) => inner.ReadAsync(authority, token);
    }

    [Fact]
    public async Task Runtime_generated_rpc_uses_production_host_exact_publication_and_keeps_credential_purposes_separate()
    {
        var setup = await RuntimeDeliverySetup(publish: false);
        await using var services = InternalServices(setup.Scenario.Connection, setup.Scenario.Time);
        var publication = Publications(services, new IdentityFixture());
        var order = new RuntimeRpcOrder();
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + setup.Scenario.Connection,
            "--Haby:Port=0", "--Haby:Tls:Enabled=false"], collection =>
        {
            collection.RemoveAll<IRuntimeAuthenticator>(); collection.AddSingleton<IRuntimeAuthenticator, RuntimeRpcIdentity>();
            collection.AddDbContextFactory<ManifestCatalogDbContext>(options => options.AddInterceptors(order));
            collection.AddSingleton<IRuntimeConfigurationStore>(p => new RuntimeRpcStore(new PostgreSqlRuntimeConfigurationStore(
                p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>()), order));
        });
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = GrpcChannel.ForAddress(host.Urls.Single()); var client = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
        var request = new DeliverRuntimeConfigurationRequest { BindingId = setup.Request.Binding.ToString(), RevisionId = setup.Request.Revision!.Value.ToString() };
        var headers = new Metadata { { "authorization", "Bearer runtime-test" } };
        order.BeginRequest();
        var unpublished = await Assert.ThrowsAsync<RpcException>(async () => await client.DeliverRuntimeAsync(request, headers));
        Assert.Equal(StatusCode.PermissionDenied, unpublished.StatusCode);
        foreach (var invalid in new Metadata?[] { null, new() { { "authorization", "Bearer runtime-test" }, { "authorization", "Bearer runtime-test" } },
            new() { { "authorization", "Basic runtime-test" } }, new() { { "authorization", "Bearer " + OperatorTestCredentials.Credential(host.Urls.Single()) } },
            new() { { "authorization", "Bearer " + ConfigurationDeliveryCredential.Create(Guid.NewGuid()) } } })
        {
            order.BeginRequest();
            Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await client.DeliverRuntimeAsync(request, invalid))).StatusCode);
        }
        Assert.False(order.Violation); Assert.Equal(0, order.SizeProbes); Assert.Equal(0, order.BodyReads);
        await publication.ApplyAsync(setup.Request, default);
        order.BeginRequest();
        var reply = await client.DeliverRuntimeAsync(request, headers);
        Assert.Equal(request.RevisionId, reply.RevisionId); Assert.Equal("{\"Mode\":\"frozen\"}", Encoding.UTF8.GetString(reply.Json.Span));
        Assert.DoesNotContain("Mode", reply.ToString(), StringComparison.Ordinal);
        Assert.False(order.Violation); Assert.Equal(1, order.SizeProbes); Assert.Equal(1, order.BodyReads);
        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () => await client.DeliverAsync(new() { RevisionId = request.RevisionId }, headers))).StatusCode);
        using var unauthenticated = GrpcChannel.ForAddress(host.Urls.Single());
        Assert.ThrowsAny<Exception>(() => new Haby.Operator.Contracts.ManifestOperator.ManifestOperatorClient(unauthenticated)
            .GetEnvironment(new() { Name = "initial" }, headers));
        await host.StopAsync();
        await using var verify = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verify.ConfigurationAccess.AsNoTracking().ToArrayAsync());
        Assert.Null((await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
    }
}
