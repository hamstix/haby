using System.Net;
using System.Net.Sockets;
using Haby.RuntimeStartup.Consumer;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class ConsumerHealthEndpointTests
{
    [Theory]
    [InlineData("/health", 200)]
    [InlineData("/unready", 503)]
    [InlineData("/other", 404)]
    public async Task Configured_consumer_endpoint_returns_only_fixed_empty_responses(string path, int status)
    {
        await using var endpoint = await ConsumerHealthEndpoint.StartAfterAsync(() => Task.CompletedTask, 0, CancellationToken.None);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{endpoint.Port}{path}"));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_incomplete_startup_never_binds_a_listener(bool fail)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = ConsumerHealthEndpoint.StartAfterAsync(() => startup.Task, port, CancellationToken.None);
        using var client = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
        if (fail)
        {
            startup.SetException(new InvalidOperationException("private startup sentinel"));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await pending);
            using var retry = new TcpClient();
            await Assert.ThrowsAsync<SocketException>(() => retry.ConnectAsync(IPAddress.Loopback, port));
        }
        else
        {
            startup.SetResult(); await using var endpoint = await pending;
            Assert.Equal(port, endpoint.Port);
        }
    }

    [Fact]
    public async Task Incomplete_headers_are_bounded_and_do_not_block_another_probe()
    {
        await using var endpoint = await ConsumerHealthEndpoint.StartAfterAsync(() => Task.CompletedTask, 0, CancellationToken.None);
        using var incomplete = new TcpClient(); await incomplete.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        await incomplete.GetStream().WriteAsync("GET /health HTTP/1.1\r\n"u8.ToArray());
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{endpoint.Port}/health"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await incomplete.GetStream().ReadAsync(new byte[1], deadline.Token));
    }

    [Fact]
    public async Task Oversized_headers_are_refused_and_disposal_releases_owned_listener()
    {
        var endpoint = await ConsumerHealthEndpoint.StartAfterAsync(() => Task.CompletedTask, 0, CancellationToken.None);
        var port = endpoint.Port;
        using var oversized = new TcpClient(); await oversized.ConnectAsync(IPAddress.Loopback, port);
        await oversized.GetStream().WriteAsync(new byte[4097]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buffer = new byte[1];
        try { Assert.Equal(0, await oversized.GetStream().ReadAsync(buffer, deadline.Token)); }
        catch (IOException) { /* A bounded connection reset also refuses the oversized request. */ }
        await endpoint.DisposeAsync();
        using var probe = new TcpClient(); await Assert.ThrowsAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port));
    }
}
