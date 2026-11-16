using System.Net;
using System.Net.Sockets;
using Haby.Application;
using Haby.Operator.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class OperatorHostCompositionTests
{
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
