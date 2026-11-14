using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;
using Haby.Operator.Host;
using Haby.Application;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class OperatorHostConfigurationTests
{
    [Fact]
    public async Task Runtime_authentication_is_one_internal_host_composition_with_validated_nondefaults()
    {
        await using var app = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false",
            "--Haby:RuntimeAuthentication:GlobalConcurrency=3", "--Haby:RuntimeAuthentication:ProviderConcurrency=2",
            "--Haby:RuntimeAuthentication:GlobalRequestsPerSecond=7", "--Haby:RuntimeAuthentication:ProviderRequestsPerSecond=5",
            "--Haby:RuntimeAuthentication:QueueCapacity=4", "--Haby:RuntimeAuthentication:QueueWaitSeconds=2", "--Haby:RuntimeAuthentication:CallTimeoutSeconds=1"]);
        Assert.Equal(new RuntimeAuthenticationOptions(3, 2, 7, 5, 4, 2, 1), app.Services.GetRequiredService<RuntimeAuthenticationOptions>());
        Assert.Same(app.Services.GetRequiredService<RuntimeAuthenticationAdmission>(), app.Services.GetRequiredService<RuntimeAuthenticationAdmission>());
        Assert.Same(app.Services.GetRequiredService<RuntimeAuthentication>(), app.Services.GetRequiredService<RuntimeAuthentication>());
        Assert.IsType<Haby.Persistence.PostgreSql.PostgreSqlRuntimeIdentityStore>(app.Services.GetRequiredService<IRuntimeIdentityStore>());
    }

    [Theory]
    [InlineData("GlobalConcurrency", "0")]
    [InlineData("ProviderConcurrency", "33")]
    [InlineData("GlobalRequestsPerSecond", "not-an-integer")]
    [InlineData("ProviderRequestsPerSecond", "51")]
    [InlineData("QueueCapacity", "4097")]
    [InlineData("QueueWaitSeconds", "61")]
    [InlineData("CallTimeoutSeconds", "31")]
    public void Invalid_runtime_authentication_options_fail_host_construction(string key, string value) =>
        Assert.Throws<ArgumentException>(() => OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0",
            "--Haby:Tls:Enabled=false", "--Haby:RuntimeAuthentication:" + key + "=" + value]));

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
        var phase = HostFailurePhase.Build;
        Grpc.Core.StatusCode? observed = null;
        await HostFailureDiagnostics.RunAsync(async defer =>
        {
            var app = OperatorHostApplication.Build(
                ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false"]);
            defer(app.DisposeAsync);
            phase = HostFailurePhase.Start;
            await app.StartAsync();
            var address = new Uri(Assert.Single(app.Urls));
            phase = HostFailurePhase.AddressScheme;
            Assert.Equal("http", address.Scheme);
            phase = HostFailurePhase.AddressLoopback;
            Assert.True(IPAddress.IsLoopback(IPAddress.Parse(address.Host)));

            phase = HostFailurePhase.Channel;
            var channel = GrpcChannel.ForAddress(address);
            defer(() => { channel.Dispose(); return ValueTask.CompletedTask; });
            var client = new ServerReflection.ServerReflectionClient(channel);
            var call = client.ServerReflectionInfo();
            defer(() => { call.Dispose(); return ValueTask.CompletedTask; });
            await AnonymousReflectionAssertion.RequireUnauthenticatedAsync(
                () => call.RequestStream.WriteAsync(new ServerReflectionRequest { ListServices = "" }),
                async () => { await call.ResponseStream.MoveNext(CancellationToken.None); },
                value => phase = value, value => observed = value);
        }, () => phase, () => observed);
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
