using System.Net;
using System.Net.Sockets;
using Haby.Application;
using Haby.Operator.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostCompositionTests
{
    [Fact]
    public async Task Additive_contract_conflict_is_refused_before_listening()
    {
        var port = AvailablePort();
        Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.BuildComposed(
            ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=" + port, "--Haby:Tls:Enabled=false"],
            (services, _) => services.AddSingleton(new Haby.Manifest.ResourceTypeDefinition(
                Haby.Manifest.PostgreSqlDatabaseContract.Id, "{\"type\":\"object\",\"additionalProperties\":false}"))));
        await RequireNotListeningAsync(port);
    }

    [Fact]
    public async Task Missing_contributed_capability_disposes_owned_dependencies_before_listening()
    {
        var port = AvailablePort();
        var owned = new AsyncResource(false);
        Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.BuildComposed(
            ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=" + port, "--Haby:Tls:Enabled=false"],
            (services, _) =>
            {
                services.AddSingleton(_ => owned);
                services.AddSingleton(p =>
                {
                    p.GetRequiredService<AsyncResource>();
                    return new Haby.Manifest.ResourceTypeDefinition(Haby.Domain.ResourceTypeId.Create("sample.missing/v1"), "{\"type\":\"object\"}");
                });
            }));
        Assert.True(owned.Disposed);
        await RequireNotListeningAsync(port);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eager_validation_failure_disposes_resolved_async_dependencies_and_preserves_primary(bool cleanupThrows)
    {
        var port = AvailablePort();
        var primary = new InvalidOperationException("registration sentinel");
        var resource = new AsyncResource(cleanupThrows);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new NeverPumpedContext());
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.Build(
                ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=" + port, "--Haby:Tls:Enabled=false"], services =>
                {
                    services.AddSingleton(_ => resource);
                    services.AddSingleton<ProviderPreflightRegistry>(provider =>
                    {
                        _ = provider.GetRequiredService<AsyncResource>();
                        throw primary;
                    });
                }));
            Assert.Same(primary, error);
            Assert.True(resource.Disposed);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await RequireNotListeningAsync(port);
    }

    [Fact]
    public async Task Public_callback_runs_once_after_stock_registration_with_actual_configuration_before_resolution()
    {
        IConfiguration? observed = null;
        var calls = 0;
        var created = 0;
        await using var app = OperatorHostApplication.BuildComposed(
            ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false", "--Sample:Value=from-host"],
            (services, configuration) =>
            {
                calls++;
                observed = configuration;
                Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IProviderIdentityQuery));
                services.AddSingleton(provider =>
                {
                    created++;
                    return new ConfiguredService(provider.GetRequiredService<TimeProvider>(), configuration["Sample:Value"]!);
                });
                Assert.Equal(0, created);
            });
        var catalog = app.Services.GetRequiredService<Haby.Manifest.ManifestContractCatalog>();
        Assert.True(catalog.TryGet(Haby.Manifest.PostgreSqlDatabaseContract.Id, out _));
        Assert.True(catalog.TryGet(Haby.PostgreSql.PostgreSqlApplicationLoginContract.Id, out _));
        Assert.True(catalog.TryGet(Haby.Kubernetes.Contracts.KubernetesContract.Definition.Id, out _));
        Assert.Equal(1, calls);
        Assert.Same(app.Configuration, observed);
        Assert.Equal(0, created);
        var service = app.Services.GetRequiredService<ConfiguredService>();
        Assert.Same(app.Services.GetRequiredService<TimeProvider>(), service.Clock);
        Assert.Equal("from-host", service.Value);
        Assert.Equal(1, created);
        Assert.Empty(app.Urls);
    }

    [Fact]
    public async Task Successful_app_keeps_resolved_dependencies_until_caller_disposes_and_does_not_own_external_instances()
    {
        var owned = new AsyncResource(false);
        var external = new AsyncResource(false);
        var app = OperatorHostApplication.BuildComposed(
            ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false"],
            (services, _) =>
            {
                services.AddSingleton(_ => owned);
                services.AddSingleton<IAsyncDisposable>(external);
            });
        try
        {
            Assert.Same(owned, app.Services.GetRequiredService<AsyncResource>());
            Assert.Same(external, app.Services.GetRequiredService<IAsyncDisposable>());
            Assert.False(owned.Disposed);
            Assert.False(external.Disposed);
            Assert.Empty(app.Urls);
        }
        finally { await app.DisposeAsync(); }
        Assert.True(owned.Disposed);
        Assert.False(external.Disposed);
        await external.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Callback_or_provider_validation_failure_never_listens(bool providerFailure)
    {
        var port = AvailablePort();
        var primary = new InvalidOperationException("callback sentinel");
        var args = new[] { "--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=" + port,
            "--Haby:Tls:Enabled=false", "--environment=Development" };
        if (providerFailure)
            Assert.Throws<AggregateException>(() => OperatorHostApplication.BuildComposed(args,
                (services, _) => services.AddSingleton<Unresolvable>()));
        else
            Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.BuildComposed(args, (_, _) => throw primary)));
        await RequireNotListeningAsync(port);
    }

    [Fact]
    public async Task Existing_entrypoints_and_null_callback_contract_remain_source_compatible()
    {
        string[] args = ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false"];
        await using var stock = OperatorHostApplication.Build(args);
        await using var internalNull = OperatorHostApplication.Build(args, null);
        Assert.Throws<ArgumentNullException>(() => OperatorHostApplication.BuildComposed(args, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => OperatorHostApplication.BuildComposedMigratedAsync(args, null!));
        // Compile the old default-token and named-token forms without opening a database.
        if (args.Length == 0)
        {
            await using var first = await OperatorHostApplication.BuildMigratedAsync(args, default);
            await using var second = await OperatorHostApplication.BuildMigratedAsync(args, cancellationToken: default);
        }
    }

    [Fact]
    public Task Migration_cancellation_disposes_configuration_once_even_when_dependency_disposal_throws() =>
        RequireMigrationConfigurationDisposalAsync(cleanupThrows: true);

    [Fact]
    public Task Migration_cancellation_with_normal_disposal_releases_configuration_provider_once() =>
        RequireMigrationConfigurationDisposalAsync(cleanupThrows: false);

    private static async Task RequireMigrationConfigurationDisposalAsync(bool cleanupThrows)
    {
        var configuration = new ObservableConfigurationProvider();
        var dependency = new AsyncResource(cleanupThrows);
        var port = AvailablePort();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OperatorHostApplication.BuildComposedMigratedAsync(
            ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=" + port, "--Haby:Tls:Enabled=false"],
            (services, hostConfiguration) =>
            {
                ((IConfigurationBuilder)hostConfiguration).Add(new ObservableConfigurationSource(configuration));
                services.AddSingleton(_ => dependency);
                services.AddSingleton(provider =>
                {
                    provider.GetRequiredService<AsyncResource>();
                    return new ProviderPreflightRegistry(provider.GetServices<IProviderPreflightHandler>(),
                        provider.GetServices<IProviderConfigurationCodec>(), provider.GetServices<IProviderDeclarationAdapter>());
                });
            }, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(dependency.Disposed);
        Assert.Equal(1, configuration.Disposals);
        await RequireNotListeningAsync(port);
    }

    [Fact]
    public async Task Successful_factory_leaves_configuration_owned_by_caller_lifecycle_and_disposes_provider_once()
    {
        var configuration = new ObservableConfigurationProvider();
        var app = OperatorHostApplication.BuildComposed(
            ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false"],
            (_, hostConfiguration) => ((IConfigurationBuilder)hostConfiguration).Add(new ObservableConfigurationSource(configuration)));
        try { Assert.Equal(0, configuration.Disposals); }
        finally { await app.DisposeAsync(); }
        Assert.Equal(1, configuration.Disposals);
    }

    private sealed class ObservableConfigurationSource(ObservableConfigurationProvider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }

    private sealed class ObservableConfigurationProvider : ConfigurationProvider, IDisposable
    {
        internal int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    internal static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal static async Task RequireNotListeningAsync(int port)
    {
        using var client = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(async () => await client.ConnectAsync(IPAddress.Loopback, port));
    }

    private sealed record ConfiguredService(TimeProvider Clock, string Value);
    private sealed class MissingDependency;
    private sealed class Unresolvable(MissingDependency missing)
    {
        public MissingDependency Missing { get; } = missing;
    }

    private sealed class NeverPumpedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            throw new InvalidOperationException("Disposal captured the caller synchronization context.");
    }

    private sealed class AsyncResource(bool cleanupThrows) : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            Disposed = true;
            if (cleanupThrows) throw new InvalidOperationException("cleanup sentinel");
        }
    }
}
