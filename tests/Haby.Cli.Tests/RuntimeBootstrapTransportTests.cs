using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Configuration.Client;
using Haby.Configuration.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Haby.Cli.Tests;

public sealed class RuntimeBootstrapTransportTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("valid")]
    [InlineData("root")]
    [InlineData("name")]
    [InlineData("expired")]
    public async Task Generated_client_uses_peer_intermediate_without_promoting_it_to_a_trust_anchor(string fault)
    {
        // Bind a local interface: production bootstrap deliberately refuses loopback endpoints.
        var address = (await Dns.GetHostAddressesAsync(Dns.GetHostName())).First(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x));
        var now = DateTimeOffset.UtcNow;
        using var rootKey = RSA.Create(2048);
        using var root = Authority("Root", rootKey).CreateSelfSigned(now.AddDays(-2), now.AddDays(2));
        using var otherKey = RSA.Create(2048);
        using var other = Authority("Other", otherKey).CreateSelfSigned(now.AddDays(-2), now.AddDays(2));
        using var intermediateKey = RSA.Create(2048);
        using var publicIntermediate = Authority("Intermediate", intermediateKey).Create(root, now.AddDays(-1), now.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var intermediate = publicIntermediate.CopyWithPrivateKey(intermediateKey);
        using var leafKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=Delivery", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(fault == "name" ? IPAddress.Parse("192.0.2.1") : address);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var publicLeaf = request.Create(intermediate, now.AddHours(-2), fault == "expired" ? now.AddHours(-1) : now.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var keyedLeaf = publicLeaf.CopyWithPrivateKey(leafKey);
        // Use the repository's PFX-loaded server-key path; no certificate enters a trust store.
        using var leaf = ServerCertificate(keyedLeaf);
        var context = SslStreamCertificateContext.Create(leaf, new X509Certificate2Collection { intermediate, root }, offline: true);
        var observations = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Mark(string id) => SafeHandshakeLog.Add(observations, id);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace); builder.Logging.AddProvider(new SafeHandshakeLog(observations));
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(https =>
            {
                https.ServerCertificate = leaf;
                https.OnAuthenticate = (_, ssl) =>
                {
                    Mark("server-authenticate-start");
                    ssl.ServerCertificate = null; ssl.ServerCertificateContext = context;
                    Mark("server-authenticate-complete");
                };
            });
        }));
        var delivery = new Delivery();
        builder.Services.AddGrpc(); builder.Services.AddSingleton(delivery);
        await using var host = builder.Build(); host.MapGrpcService<Delivery>(); await host.StartAsync();
        try
        {
            using var channel = RuntimeBootstrapChannel.Create(new(host.Urls.Single(), new("custom-ca", (fault == "root" ? other : root).ExportCertificatePem()), new()));
            var client = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            async Task<DeliverConfigurationReply> Call()
            {
                using var call = client.DeliverRuntimeAsync(new() { BindingId = Guid.NewGuid().ToString("D"), RevisionId = Guid.CreateVersion7().ToString("D") }, cancellationToken: deadline.Token);
                return await call.ResponseAsync;
            }
            Mark("production-client-start");
            var failure = await Record.ExceptionAsync(Call);
            Mark(failure is null ? "production-client-complete" : "production-client-refused");
            if (failure is not null) SafeHandshakeLog.Types(failure, observations);
            // Prove this exact HTTPS/generated-RPC fixture independently with a local policy.
            // Run after the production call so the control cannot warm its intermediate cache.
            using var controlHandler = new SocketsHttpHandler { UseProxy = false };
            controlHandler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                CustomTrustStore = { root },
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true,
                ApplicationPolicy = { new Oid("1.3.6.1.5.5.7.3.1") }
            };
            controlHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, errors) =>
            {
                Mark(errors == SslPolicyErrors.None ? "control-validation-accepted" : "control-validation-refused");
                return errors == SslPolicyErrors.None;
            };
            if (fault == "valid")
            {
                using var controlChannel = GrpcChannel.ForAddress(host.Urls.Single(), new GrpcChannelOptions { HttpHandler = controlHandler });
                Mark("control-client-start");
                var controlFailure = await Record.ExceptionAsync(async () =>
                {
                    using var control = new ConfigurationDelivery.ConfigurationDeliveryClient(controlChannel).DeliverRuntimeAsync(new(), cancellationToken: deadline.Token);
                    Assert.Equal("{}", (await control.ResponseAsync).Json.ToStringUtf8());
                });
                Mark(controlFailure is null ? "control-client-complete" : "control-client-refused");
                if (controlFailure is not null) SafeHandshakeLog.Types(controlFailure, observations);
                Assert.True(controlFailure is null, "Owned positive transport control must succeed before a production refusal is diagnostic.");
                Assert.True(failure is null, "Production generated client must accept the valid exclusive-root peer-intermediate chain.");
                Assert.Equal(2, delivery.Calls);
            }
            else
            {
                Assert.IsType<RpcException>(failure);
                Assert.Equal(0, delivery.Calls);
            }
        }
        finally
        {
            await host.StopAsync();
            foreach (var observation in observations.Take(128)) output.WriteLine(observation);
        }
    }

    private static CertificateRequest Authority(string name, RSA key)
    {
        var request = new CertificateRequest("CN=" + name + "-" + Guid.NewGuid().ToString("N"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }

    private static X509Certificate2 ServerCertificate(X509Certificate2 certificate)
    {
        var bytes = certificate.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(bytes, null); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed class Delivery : ConfigurationDelivery.ConfigurationDeliveryBase
    {
        internal int Calls;
        public override Task<DeliverConfigurationReply> DeliverRuntime(DeliverRuntimeConfigurationRequest request, ServerCallContext context)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new DeliverConfigurationReply { RevisionId = request.RevisionId, Json = ByteString.CopyFromUtf8("{}") });
        }
    }

    private sealed class SafeHandshakeLog(System.Collections.Concurrent.ConcurrentQueue<string> observations) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) { Add(observations, "server-exception"); Types(exception, observations); }
        }
        internal static void Add(System.Collections.Concurrent.ConcurrentQueue<string> target, string id)
        {
            lock (target) { if (target.Count < 128) target.Enqueue(id); }
        }
        internal static void Types(Exception exception, System.Collections.Concurrent.ConcurrentQueue<string> target)
        {
            for (var depth = 0; depth < 4 && exception is not null; depth++, exception = exception.InnerException!)
                Add(target, exception switch
                {
                    AuthenticationException => "type-authentication",
                    CryptographicException => "type-cryptographic",
                    System.ComponentModel.Win32Exception => "type-win32",
                    HttpRequestException => "type-http-request",
                    IOException => "type-io",
                    ArgumentException => "type-argument",
                    InvalidOperationException => "type-invalid-operation",
                    NotSupportedException => "type-not-supported",
                    RpcException => "type-rpc",
                    _ => "type-other"
                });
        }
    }
}
