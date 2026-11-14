using Haby.Application;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Configuration.Contracts;
using Haby.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class RuntimeDeliveryHostTests
{
    [Fact]
    public async Task Runtime_delivery_composition_has_distinct_store_and_finite_lifetime_through_reply_copy()
    {
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false",
            "--Haby:RuntimeDelivery:RequestTimeoutSeconds=7", "--Haby:RuntimeDelivery:MaxConcurrentRequests=1"]);
        Assert.IsType<PostgreSqlRuntimeConfigurationStore>(host.Services.GetRequiredService<IRuntimeConfigurationStore>());
        Assert.NotNull(host.Services.GetRequiredService<DeliverRuntimeConfiguration>());
        var lifetime = host.Services.GetRequiredService<RuntimeDeliveryLifetime>();
        Assert.Equal(new(7, 1), lifetime.Options);
        using var first = lifetime.Enter(); Assert.NotNull(first); Assert.Null(lifetime.Enter());
        first.Dispose(); using var next = lifetime.Enter(); Assert.NotNull(next);
    }

    [Theory]
    [InlineData("RequestTimeoutSeconds", "0")]
    [InlineData("RequestTimeoutSeconds", "31")]
    [InlineData("MaxConcurrentRequests", "0")]
    [InlineData("MaxConcurrentRequests", "257")]
    [InlineData("MaxConcurrentRequests", "invalid")]
    public void Runtime_delivery_invalid_limits_fail_host_construction(string key, string value) =>
        Assert.Throws<ArgumentException>(() => OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false",
            "--Haby:RuntimeDelivery:" + key + "=" + value]));

    [Theory]
    [InlineData(16384, true)]
    [InlineData(16385, false)]
    [InlineData(0, false)]
    public void Runtime_header_credential_byte_bound_precedes_encoding(int length, bool accepted) =>
        Assert.Equal(accepted, ConfigurationDeliveryService.ValidRuntimeHeader("Bearer " + new string('a', length)));

    [Theory]
    [InlineData("Bearer a b")]
    [InlineData("bearer abc")]
    [InlineData("Bearer ü")]
    [InlineData("Bearer abc\n")]
    public void Runtime_header_rejects_ambiguous_or_non_ASCII_material(string header) => Assert.False(ConfigurationDeliveryService.ValidRuntimeHeader(header));

    private sealed class WaitingRuntimeTrust : IRuntimeIdentityStore
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<RuntimeTrust?> ReadAsync(Guid binding, CancellationToken token)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; }
            finally { Exited.TrySetResult(); }
        }
        public ValueTask<bool> IsCurrentAsync(RuntimeTrust expected, CancellationToken token) => throw new InvalidOperationException("Unexpected identity success.");
    }

    private sealed class InvalidRevisionAccess : IRuntimeIdentityStore, IRuntimeConfigurationStore, ISecretStore
    {
        internal int IdentityReads, ArtifactReads, MaterialReads;
        public ValueTask<RuntimeTrust?> ReadAsync(Guid binding, CancellationToken token)
        { IdentityReads++; return ValueTask.FromResult<RuntimeTrust?>(null); }
        public ValueTask<bool> IsCurrentAsync(RuntimeTrust expected, CancellationToken token) => throw new InvalidOperationException("Unexpected identity success.");
        public ValueTask<RuntimeConfigurationAuthority?> AuthorizeAsync(RuntimeAuthenticatedContext identity, ConfigurationDocumentRevisionId revision, CancellationToken token)
        { ArtifactReads++; return ValueTask.FromResult<RuntimeConfigurationAuthority?>(null); }
        public ValueTask<ConfigurationDeliveryArtifact?> ReadAsync(RuntimeConfigurationAuthority authority, CancellationToken token)
        { ArtifactReads++; return ValueTask.FromResult<ConfigurationDeliveryArtifact?>(null); }
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes, CancellationToken cancellationToken)
        { MaterialReads++; return ValueTask.FromResult<SecretMaterial?>(null); }
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken cancellationToken)
        { MaterialReads++; return ValueTask.FromResult<SecretMaterial?>(null); }
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("version4")]
    [InlineData("noncanonical")]
    [InlineData("uppercase")]
    public async Task Runtime_invalid_revision_is_Unauthenticated_before_identity_artifact_or_material_access(string shape)
    {
        var access = new InvalidRevisionAccess();
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false"], services =>
        {
            services.AddSingleton<IRuntimeIdentityStore>(access);
            services.AddSingleton<IRuntimeConfigurationStore>(access);
            services.AddSingleton<ISecretStore>(access);
        });
        await host.StartAsync();
        using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var client = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
        var revision = shape switch
        {
            "version4" => Guid.NewGuid().ToString(),
            "uppercase" => ConfigurationDocumentRevisionId.New().ToString().ToUpperInvariant(),
            _ => Guid.CreateVersion7().ToString("N")
        };
        var request = new DeliverRuntimeConfigurationRequest { BindingId = Guid.CreateVersion7().ToString(), RevisionId = revision };
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await client.DeliverRuntimeAsync(request,
            new Metadata { { "authorization", "Bearer bounded-transient-credential" } }));
        Assert.Equal(0, access.IdentityReads); Assert.Equal(0, access.ArtifactReads); Assert.Equal(0, access.MaterialReads);
        Assert.Equal(StatusCode.Unauthenticated, refused.StatusCode);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("server")]
    [InlineData("caller")]
    [InlineData("deadline")]
    public async Task Runtime_request_cap_and_caller_deadline_cancel_the_same_authentication_lifetime(string mode)
    {
        var waiting = new WaitingRuntimeTrust();
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false",
            "--Haby:RuntimeDelivery:RequestTimeoutSeconds=1", "--Haby:RuntimeDelivery:MaxConcurrentRequests=1"], services => services.AddSingleton<IRuntimeIdentityStore>(waiting));
        Assert.Same(waiting, host.Services.GetRequiredService<IRuntimeIdentityStore>());
        Assert.NotNull(host.Services.GetRequiredService<RuntimeAuthentication>());
        Assert.NotNull(host.Services.GetRequiredService<DeliverRuntimeConfiguration>());
        try { Assert.NotNull(ActivatorUtilities.CreateInstance<ConfigurationDeliveryService>(host.Services)); }
        catch (Exception) { Assert.Fail("Runtime delivery service activation failed."); }
        await host.StartAsync();
        using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var client = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
        var request = new DeliverRuntimeConfigurationRequest { BindingId = Guid.CreateVersion7().ToString(), RevisionId = ConfigurationDocumentRevisionId.New().ToString() };
        var headers = new Metadata { { "authorization", "Bearer bounded-transient-credential" } };
        using var cancellation = new CancellationTokenSource();
        using var call = client.DeliverRuntimeAsync(request, headers, deadline: mode == "deadline" ? DateTime.UtcNow.AddMilliseconds(500) : DateTime.UtcNow.AddSeconds(10),
            cancellationToken: cancellation.Token);
        await Task.WhenAny(waiting.Entered.Task, call.ResponseAsync).WaitAsync(TimeSpan.FromSeconds(5));
        if (!waiting.Entered.Task.IsCompleted)
        {
            try { await call.ResponseAsync; }
            catch (RpcException error) { Assert.Fail("Runtime RPC completed before authentication probe: " + error.StatusCode); }
            Assert.Fail("Runtime RPC succeeded before authentication probe.");
        }
        await waiting.Entered.Task;
        Assert.Equal(StatusCode.ResourceExhausted, (await Assert.ThrowsAsync<RpcException>(async () => await client.DeliverRuntimeAsync(request, headers))).StatusCode);
        if (mode == "caller") cancellation.Cancel();
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
        Assert.Equal(mode == "caller" ? StatusCode.Cancelled : mode == "deadline" ? StatusCode.DeadlineExceeded : StatusCode.Unavailable, refused.StatusCode);
        await waiting.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();
    }
}
