using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

[assembly: InternalsVisibleTo("Haby.Cli.Tests")]

namespace Haby.RuntimeStartup.Consumer;

/// <summary>Fixed qualification-only endpoints, available only after complete configured startup.</summary>
internal sealed class ConsumerHealthEndpoint : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime;
    private readonly Task[] workers;
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    private ConsumerHealthEndpoint(int port, CancellationToken token)
    {
        listener = new TcpListener(IPAddress.Any, port);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        try { listener.Start(4); }
        catch { listener.Dispose(); lifetime.Dispose(); throw; }
        // Four fixed accept/connection workers; no unbounded per-request task queue.
        workers = Enumerable.Range(0, 4).Select(_ => ServeAsync()).ToArray();
    }

    internal static async Task<ConsumerHealthEndpoint> StartAfterAsync(Func<Task> startup, int port, CancellationToken token)
    {
        await startup().WaitAsync(token);
        token.ThrowIfCancellationRequested();
        return new(port, token);
    }

    private async Task ServeAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var connection = await listener.AcceptTcpClientAsync(lifetime.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(1));
                var buffer = new byte[4096];
                try
                {
                    var stream = connection.GetStream(); var length = 0;
                    while (length < buffer.Length)
                    {
                        var count = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token);
                        if (count == 0) break;
                        length += count;
                        if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0) continue;
                        var lineEnd = buffer.AsSpan(0, length).IndexOf("\r\n"u8);
                        var status = Status(buffer.AsSpan(0, lineEnd));
                        ReadOnlyMemory<byte> response = status switch
                        {
                            200 => "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
                            503 => "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
                            _ => "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray()
                        };
                        await stream.WriteAsync(response, deadline.Token);
                        break;
                    }
                }
                finally { CryptographicOperations.ZeroMemory(buffer); }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private static int Status(ReadOnlySpan<byte> line) =>
        line.SequenceEqual("GET /health HTTP/1.1"u8) || line.SequenceEqual("GET /health HTTP/1.0"u8) ? 200 :
        line.SequenceEqual("GET /unready HTTP/1.1"u8) || line.SequenceEqual("GET /unready HTTP/1.0"u8) ? 503 : 404;

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync(); listener.Stop();
        try { await Task.WhenAll(workers); }
        finally { listener.Dispose(); lifetime.Dispose(); }
    }
}
