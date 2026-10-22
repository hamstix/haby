using Grpc.Core;
using Haby.Cli;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliGuardTests
{
    [Theory]
    [InlineData("http://localhost:7443")]
    [InlineData("https://example.com:7443")]
    [InlineData("https://0.0.0.0:7443")]
    public void Endpoint_must_use_loopback_https(string address) => Assert.Null(Program.ParseEndpoint(address));

    [Theory]
    [InlineData("https://localhost:7443")]
    [InlineData("https://127.0.0.1:7443")]
    public void Endpoint_accepts_loopback_https(string address) => Assert.NotNull(Program.ParseEndpoint(address));

    [Fact]
    public async Task File_read_accepts_boundary_and_rejects_first_oversized_byte()
    {
        var path = Path.Combine(Path.GetTempPath(), "haby-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(path, new byte[Program.MaxEnvelopeBytes]);
            Assert.Equal(Program.MaxEnvelopeBytes, (await Program.ReadEnvelopeAsync(path)).Length);
            await using (var stream = File.OpenWrite(path))
            {
                stream.Seek(0, SeekOrigin.End);
                stream.WriteByte(1);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => Program.ReadEnvelopeAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Error_output_excludes_exception_details()
    {
        Assert.DoesNotContain("secret-path", Program.SafeError(new IOException("secret-path")));
        var rpc = new RpcException(new Status(StatusCode.Unavailable, "secret-connection"));
        Assert.Equal("RPC failed: Unavailable", Program.SafeError(rpc));
    }
}
