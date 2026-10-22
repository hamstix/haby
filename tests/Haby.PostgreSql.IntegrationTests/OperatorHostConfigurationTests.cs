using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;
using Haby.Operator.Host;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class OperatorHostConfigurationTests
{
    [Fact]
    public async Task Wildcard_url_configuration_cannot_override_loopback_and_tls_defaults()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Combine(Path.GetTempPath(), "haby-loopback-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, "test-only"));
        try
        {
            await using var app = OperatorHostApplication.Build(
                ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0",
                    "--Haby:Tls:CertificatePath=" + path, "--Haby:Tls:CertificatePassword=test-only",
                    "--urls=http://0.0.0.0:7443"]);
            await app.StartAsync();
            var address = new Uri(Assert.Single(app.Urls));
            Assert.Equal("https", address.Scheme);
            Assert.Equal(IPAddress.Loopback, IPAddress.Parse(address.Host));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Disabled_tls_serves_grpc_on_loopback_without_certificate_settings()
    {
        await using var app = OperatorHostApplication.Build(
            ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false"]);
        await app.StartAsync();
        var address = new Uri(Assert.Single(app.Urls));
        Assert.Equal("http", address.Scheme);
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(address.Host)));

        using var channel = GrpcChannel.ForAddress(address);
        var client = new ServerReflection.ServerReflectionClient(channel);
        using var call = client.ServerReflectionInfo();
        await call.RequestStream.WriteAsync(new ServerReflectionRequest { ListServices = "" });
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Contains(call.ResponseStream.Current.ListServicesResponse.Service,
            service => service.Name == "haby.operator.v1alpha1.ManifestOperator");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void Enabled_tls_requires_certificate_path(string? setting)
    {
        var arguments = new List<string> { "--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0" };
        if (setting is not null) arguments.Add("--Haby:Tls:Enabled=" + setting);
        var error = Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.Build(
            [.. arguments]));
        Assert.Contains("Haby:Tls:CertificatePath", error.Message);
    }

    [Fact]
    public void Invalid_tls_setting_prevents_host_configuration()
    {
        var error = Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.Build(
            ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=invalid"]));
        Assert.Contains("Haby:Tls:Enabled", error.Message);
    }
}
