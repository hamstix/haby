using System.Net;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;
using Haby.Operator.Host;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class OperatorHostConfigurationTests
{
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
