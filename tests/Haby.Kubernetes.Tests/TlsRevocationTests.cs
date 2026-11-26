using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class TlsRevocationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_CRL_proves_online_result_and_offline_never_fetches_or_falls_back(bool revoked)
    {
        await using var crl = await LocalCrlEndpoint.Start();
        using var ca = TlsPolicyTests.Authority();
        using var leaf = TlsPolicyTests.Leaf(ca, distributionPoint: crl.Address.AbsoluteUri);
        var builder = new CertificateRevocationListBuilder();
        if (revoked) builder.AddEntry(leaf);
        crl.Content = builder.Build(ca, 1, DateTimeOffset.UtcNow.AddMinutes(10), HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1, DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var endpoint = await LocalTlsEndpoint.Start(leaf);
        var handler = new KubernetesPreflightHandler(new(), new Secrets());
        var configuration = new KubernetesProviderConfiguration(endpoint.Address, ca.ExportCertificatePem(),
            new(SecretId.New(), SecretVersionId.New()), null, null, new(KubernetesTlsMode.CustomCa, KubernetesRevocationMode.Online));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var elapsed = Stopwatch.StartNew();
        var online = await handler.CheckTypedAsync(new(EnvironmentRecordId.New(), ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1)), configuration, timeout.Token);
        Snapshot(DiagnosticPhase.Online, online);
        if (revoked)
        {
            Assert.False(online.IsReady);
            Assert.Equal(ProviderConnectionDiagnostic.TlsCertificateRevoked, online.ConnectionDiagnostic);
            Assert.Equal(0, endpoint.Requests);
        }
        else Assert.True(online.IsReady);
        Assert.True(crl.Requests > 0);
        var requests = crl.Requests;
        crl.Available = false;
        elapsed.Restart();
        var offline = await handler.CheckTypedAsync(new(EnvironmentRecordId.New(), ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1)), configuration with
        { Tls = new(KubernetesTlsMode.CustomCa, KubernetesRevocationMode.Offline) }, timeout.Token);
        Snapshot(DiagnosticPhase.Offline, offline);
        Assert.Equal(requests, crl.Requests);
        if (revoked)
        {
            Assert.False(offline.IsReady);
            Assert.Contains(offline.ConnectionDiagnostic, new ProviderConnectionDiagnostic?[]
            { ProviderConnectionDiagnostic.TlsCertificateRevoked, ProviderConnectionDiagnostic.TlsRevocationUnavailable });
        }
        else if (OperatingSystem.IsWindows()) Assert.True(offline.IsReady);
        else if (!offline.IsReady) Assert.Equal(ProviderConnectionDiagnostic.TlsRevocationUnavailable, offline.ConnectionDiagnostic);
        // Linux/OpenSSL need not retain the Windows URL cache across separate chain instances.
        // In either case a missing cached answer is not converted to a successful NoCheck result.
        elapsed.Restart();
        var uncheckedResult = await handler.CheckTypedAsync(new(EnvironmentRecordId.New(), ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1)), configuration with
        { Tls = new(KubernetesTlsMode.CustomCa, KubernetesRevocationMode.NoCheck) }, timeout.Token);
        Snapshot(DiagnosticPhase.NoCheck, uncheckedResult);
        Assert.True(uncheckedResult.IsReady); Assert.Equal(requests, crl.Requests);

        void Snapshot(DiagnosticPhase phase, ProviderPreflightResult result) => output.WriteLine(
            $"TLS_REVOCATION Phase={phase}; ElapsedMilliseconds={elapsed.ElapsedMilliseconds}; Outcome={result.Outcome}; Diagnostic={result.ConnectionDiagnostic}; CallerCancelled={timeout.IsCancellationRequested}; " +
            $"CrlStarted={Volatile.Read(ref crl.Requests)}; CrlCompleted={Volatile.Read(ref crl.Completed)}; CrlHttpStatus={Volatile.Read(ref crl.ResponseStatus)}; CrlContentLength={Volatile.Read(ref crl.ResponseLength)}; " +
            $"CrlAvailable={Volatile.Read(ref crl.Available)}; TlsApplicationRequests={Volatile.Read(ref endpoint.Requests)}");
    }

    private enum DiagnosticPhase { Online, Offline, NoCheck }

    private sealed class Secrets : ISecretStore
    {
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes, CancellationToken token) =>
            ValueTask.FromResult<SecretMaterial?>(new(Encoding.ASCII.GetBytes("test-only")));
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
    }
}

internal sealed class LocalCrlEndpoint(WebApplication application) : IAsyncDisposable
{
    internal Uri Address { get; private set; } = null!;
    internal byte[] Content = [];
    internal int Requests;
    internal int Completed;
    internal int ResponseStatus;
    internal int ResponseLength;
    internal bool Available = true;
    internal static async Task<LocalCrlEndpoint> Start()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build(); var endpoint = new LocalCrlEndpoint(app);
        app.Run(async context =>
        {
            Interlocked.Increment(ref endpoint.Requests);
            context.Response.StatusCode = endpoint.Available ? 200 : 503;
            context.Response.ContentType = "application/pkix-crl";
            Interlocked.Exchange(ref endpoint.ResponseStatus, context.Response.StatusCode);
            Interlocked.Exchange(ref endpoint.ResponseLength, endpoint.Available ? endpoint.Content.Length : 0);
            context.Response.OnCompleted(() => { Interlocked.Increment(ref endpoint.Completed); return Task.CompletedTask; });
            if (endpoint.Available) await context.Response.Body.WriteAsync(endpoint.Content, context.RequestAborted);
        });
        try
        {
            await app.StartAsync();
            endpoint.Address = new Uri(new Uri(app.Urls.Single()), Guid.NewGuid().ToString("N") + ".crl");
            return endpoint;
        }
        catch { await app.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await application.StopAsync(timeout.Token); }
        finally { await application.DisposeAsync(); }
    }
}
