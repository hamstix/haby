using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Haby.Application;
using Haby.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlPairImportTests
{
    private readonly List<string> tlsDirectories = [];
    private readonly List<string> remoteTlsFiles = [];

    [Fact]
    public async Task Explicit_disable_and_verify_full_use_frozen_provider_mode_without_trust_name_or_binding_fallback()
    {
        var pair = await ExistingPair();
        await using var passwordGuard = await PasswordGuard(pair);
        var before = await Snapshot(pair);
        var tls = await EnableTls();
        var disabled = await Prepare(pair, PostgreSqlTransportMode.Disable);
        await using (var services = Services(disabled.Connection))
        {
            Assert.Null((await services.GetRequiredService<DurableGenerationRunner>().RunAsync(disabled.Commit.DeploymentId, disabled.Commit.Generation, default)).StopOutcome);
            var observation = PostgreSqlImportObservation.Decode((await services.GetRequiredService<IResourceObservationAcceptance>().FindAsync(disabled.Key, default))!.Evidence)!;
            Assert.False(observation.AdministratorTls); Assert.False(observation.ApplicationTls);
        }
        var trusted = new PostgreSqlProviderTransport(tls.RootFile);
        var verified = await Prepare(pair, PostgreSqlTransportMode.VerifyFull, trusted);
        foreach (var untrusted in new[] { new PostgreSqlProviderTransport(tls.WrongRootFile), new PostgreSqlProviderTransport() })
        {
            await using var services = Services(verified.Connection, transport: untrusted);
            var result = await services.GetRequiredService<DurableGenerationRunner>().RunAsync(verified.Commit.DeploymentId, verified.Commit.Generation, default);
            Assert.Equal(DurableExecutionOutcome.RetryableFailure, result.StopOutcome);
            await AssertNoUsableCredential(verified);
        }
        await using (var services = Services(verified.Connection, transport: trusted))
        {
            Assert.Null((await services.GetRequiredService<DurableGenerationRunner>().RunAsync(verified.Commit.DeploymentId, verified.Commit.Generation, default)).StopOutcome);
            var observation = PostgreSqlImportObservation.Decode((await services.GetRequiredService<IResourceObservationAcceptance>().FindAsync(verified.Key, default))!.Evidence)!;
            Assert.True(observation.AdministratorTls); Assert.True(observation.ApplicationTls);
            // The Binding requests plaintext consumer formatting; it cannot override Provider verification transport.
            Assert.Equal(new Haby.Domain.ConfigurationString("disable"), Assert.Single(verified.Plan.Inputs.Configuration!.RenderPins).Options.Members["sslMode"]);
        }
        var badName = await Prepare(pair, PostgreSqlTransportMode.VerifyFull, trusted);
        await WriteCertificate(tls.RemoteCertificate, tls.RemoteKey, tls.WrongNameCertificate, tls.WrongNameKey);
        await Sql(Control(), "SELECT pg_reload_conf()");
        await AwaitTls(tls.RootFile, expected: false);
        await using (var services = Services(badName.Connection, transport: trusted))
        {
            Assert.Equal(DurableExecutionOutcome.RetryableFailure, (await services.GetRequiredService<DurableGenerationRunner>()
                .RunAsync(badName.Commit.DeploymentId, badName.Commit.Generation, default)).StopOutcome);
            await AssertNoUsableCredential(badName);
        }
        var explicitPlaintext = await Prepare(pair, PostgreSqlTransportMode.Disable);
        await using (var services = Services(explicitPlaintext.Connection))
        {
            Assert.Null((await services.GetRequiredService<DurableGenerationRunner>().RunAsync(explicitPlaintext.Commit.DeploymentId, explicitPlaintext.Commit.Generation, default)).StopOutcome);
            var observation = PostgreSqlImportObservation.Decode((await services.GetRequiredService<IResourceObservationAcceptance>().FindAsync(explicitPlaintext.Key, default))!.Evidence)!;
            Assert.False(observation.AdministratorTls); Assert.False(observation.ApplicationTls);
        }
        Assert.True(before == await Snapshot(pair), "Transport verification changed existing external state.");
        Assert.True(await PasswordUnchanged(passwordGuard, pair), "Transport verification changed the existing password verifier.");
    }

    private sealed class TlsFixture(string rootFile, string wrongRootFile, string remoteCertificate, string remoteKey,
        string wrongNameCertificate, string wrongNameKey)
    {
        public string RootFile => rootFile;
        public string WrongRootFile => wrongRootFile;
        public string RemoteCertificate => remoteCertificate;
        public string RemoteKey => remoteKey;
        public string WrongNameCertificate => wrongNameCertificate;
        public string WrongNameKey => wrongNameKey;
        public override string ToString() => "[Owned TLS fixture]";
    }

    private async Task<TlsFixture> EnableTls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "haby-import-tls-" + Guid.NewGuid().ToString("N"));
        tlsDirectories.Add(Path.GetFullPath(directory)); Directory.CreateDirectory(directory);
        using var caKey = RSA.Create(2048);
        using var ca = Root(caKey, "haby-import-fixture-ca");
        using var wrongKey = RSA.Create(2048);
        using var wrongCa = Root(wrongKey, "haby-import-other-fixture-ca");
        var rootFile = Path.Combine(directory, "root.pem"); var wrongRootFile = Path.Combine(directory, "other-root.pem");
        await File.WriteAllTextAsync(rootFile, ca.ExportCertificatePem());
        await File.WriteAllTextAsync(wrongRootFile, wrongCa.ExportCertificatePem());
        var (certificate, key) = Leaf(ca, true);
        var (wrongCertificate, wrongPrivateKey) = Leaf(ca, false);
        var remoteCertificate = "/tmp/haby-import-" + Guid.NewGuid().ToString("N") + ".crt";
        var remoteKey = "/tmp/haby-import-" + Guid.NewGuid().ToString("N") + ".key";
        remoteTlsFiles.Add(remoteCertificate); remoteTlsFiles.Add(remoteKey);
        await WriteCertificate(remoteCertificate, remoteKey, certificate, key);
        await Setting("ssl_cert_file", remoteCertificate); await Setting("ssl_key_file", remoteKey); await Setting("ssl", "on");
        await Sql(Control(), "SELECT pg_reload_conf()");
        await AwaitTls(rootFile, expected: true);
        return new(rootFile, wrongRootFile, remoteCertificate, remoteKey, wrongCertificate, wrongPrivateKey);
    }

    private async Task AwaitTls(string rootFile, bool expected)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(Control())
        {
            Host = "127.0.0.1",
            Pooling = false,
            SslMode = Npgsql.SslMode.VerifyFull,
            GssEncryptionMode = Npgsql.GssEncryptionMode.Disable,
            RootCertificate = rootFile
        };
        try
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var accepted = false;
                try { await using var connection = new Npgsql.NpgsqlConnection(builder.ConnectionString); await connection.OpenAsync(); accepted = true; }
                catch (Npgsql.NpgsqlException) { }
                if (accepted == expected) return;
                await Task.Delay(100);
            }
            throw new InvalidOperationException("Owned TLS fixture did not reach its selected validation state.");
        }
        finally { builder.Password = null; }
    }

    private static X509Certificate2 Root(RSA key, string name)
    {
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }
    private static (string Certificate, string Key) Leaf(X509Certificate2 issuer, bool correctName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var eku = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
        var names = new SubjectAlternativeNameBuilder();
        if (correctName) names.AddIpAddress(IPAddress.Loopback); else names.AddDnsName("wrong.fixture.invalid");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.Create(issuer, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(16));
        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }
    private Task WriteCertificate(string certificatePath, string keyPath, string certificate, string key) => fixture.RunClientUtilityAsync("sh", "-c",
        "set -eu; umask 077; printf %s '" + Convert.ToBase64String(Encoding.ASCII.GetBytes(certificate)) + "' | base64 -d > " + certificatePath +
        "; printf %s '" + Convert.ToBase64String(Encoding.ASCII.GetBytes(key)) + "' | base64 -d > " + keyPath +
        "; chown postgres:postgres " + certificatePath + " " + keyPath + "; chmod 600 " + keyPath);
}
