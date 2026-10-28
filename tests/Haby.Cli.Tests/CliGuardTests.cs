using Grpc.Core;
using Haby.Cli;
using Haby.Operator.Contracts;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliGuardTests
{
    [Theory]
    [InlineData("http://example.com:7443")]
    [InlineData("https://example.com:7443")]
    [InlineData("https://0.0.0.0:7443")]
    [InlineData("http://user:secret@localhost:7443")]
    [InlineData("http://localhost:7443/?token=secret")]
    public void Endpoint_must_use_loopback(string address) => Assert.Null(CliApplication.ParseEndpoint(address));

    [Theory]
    [InlineData("https://localhost:7443")]
    [InlineData("https://127.0.0.1:7443")]
    [InlineData("http://localhost:7443")]
    [InlineData("http://127.0.0.1:7443")]
    public void Endpoint_accepts_loopback(string address) => Assert.NotNull(CliApplication.ParseEndpoint(address));

    [Fact]
    public async Task File_read_accepts_boundary_and_rejects_first_oversized_byte()
    {
        var path = Path.Combine(Path.GetTempPath(), "haby-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(path, new byte[CliApplication.MaxEnvelopeBytes]);
            Assert.Equal(CliApplication.MaxEnvelopeBytes, (await CliApplication.ReadEnvelopeAsync(path)).Length);
            await using (var stream = File.OpenWrite(path))
            {
                stream.Seek(0, SeekOrigin.End);
                stream.WriteByte(1);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => CliApplication.ReadEnvelopeAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Error_output_excludes_exception_details()
    {
        Assert.DoesNotContain("secret-path", CliApplication.SafeError(new IOException("secret-path")));
        var rpc = new RpcException(new Status(StatusCode.Unavailable, "secret-connection"));
        Assert.Equal("RPC failed: Unavailable", CliApplication.SafeError(rpc));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("invalid", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    public void Certificate_validation_bypass_requires_explicit_true(string? setting, bool expected) =>
        Assert.Equal(expected, CliApplication.SkipTlsCertificateValidation(setting));

    [Fact]
    public void Certificate_validation_is_enabled_by_default()
    {
        using var handler = CliApplication.CreateHttpHandler(false);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void Certificate_validation_can_be_disabled_for_loopback_debugging()
    {
        using var handler = CliApplication.CreateHttpHandler(true);
        Assert.Same(System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public async Task Preview_context_rejects_secret_fields_duplicates_and_oversize()
    {
        var path = Path.Combine(Path.GetTempPath(), "haby-preview-" + Guid.NewGuid().ToString("N"));
        var reference = new RevisionReference { ApplicationRecordId = Guid.NewGuid().ToString(), RevisionNumber = 1 };
        const string valid = """{"preview":true,"environmentId":"test","providers":[{"alias":"main","instanceId":"local-postgresql","configurationRevision":1,"implementationId":"haby.postgresql.database/v1alpha1"}]}""";
        try
        {
            await File.WriteAllTextAsync(path, valid);
            var request = await CliApplication.ReadPreviewContextAsync(path, reference);
            Assert.Equal("test", request.EnvironmentId);
            Assert.Single(request.Providers);
            await File.WriteAllTextAsync(path, valid, new System.Text.UTF8Encoding(true));
            var withBom = await CliApplication.ReadPreviewContextAsync(path, reference);
            Assert.Equal(request, withBom);
            await File.WriteAllTextAsync(path, valid.Replace("\"preview\":true", "\"preview\":true,\"connectionString\":\"secret-value\"", StringComparison.Ordinal));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => CliApplication.ReadPreviewContextAsync(path, reference));
            Assert.DoesNotContain("secret-value", error.Message);
            await File.WriteAllTextAsync(path, valid.Replace("\"preview\":true", "\"preview\":true,\"preview\":true", StringComparison.Ordinal));
            await Assert.ThrowsAsync<InvalidDataException>(() => CliApplication.ReadPreviewContextAsync(path, reference));
            await File.WriteAllBytesAsync(path, new byte[CliApplication.MaxPreviewContextBytes + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => CliApplication.ReadPreviewContextAsync(path, reference));
        }
        finally { File.Delete(path); }
    }
}
