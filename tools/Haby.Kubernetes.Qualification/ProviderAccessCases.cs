using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using k8s;
using k8s.Models;

/// <summary>Extends the existing disposable API rehearsal; creates no application or probe objects.</summary>
internal static class ProviderAccessCases
{
    internal static async Task Run(Uri endpoint, string directory, string bearer, string expectedUsername, string expectedUid, CancellationToken token)
    {
        var environment = EnvironmentRecordId.New();
        using var secrets = new Secrets(environment);
        var cert = secrets.Add(File.ReadAllBytes(Path.Combine(directory, "admin.crt")));
        var key = secrets.Add(File.ReadAllBytes(Path.Combine(directory, "admin.key")));
        var accessToken = secrets.Add(Encoding.UTF8.GetBytes(bearer));
        var invalid = secrets.Add("invalid"u8.ToArray());
        var ca = File.ReadAllText(Path.Combine(directory, "ca.crt"));
        var configuration = new KubernetesProviderConfiguration(endpoint, ca, accessToken, null, null);
        var codec = new KubernetesProviderConfigurationCodec();
        var binding = new KubernetesBinding(environment.ToString(), ProviderInstanceId.New().ToString(), 1, KubernetesProviderConfigurationCodec.Implementation);
        ValueTask<KernelClient> Fixture(EnvironmentRecordId scope, KubernetesProviderConfiguration c, CancellationToken ct)
        {
            if (scope != environment) throw new InvalidOperationException("Qualification scope mismatch.");
            return KubernetesClientFactory.CreateFixture(new(binding, c.Endpoint, c.ServerCaPem, c.BearerToken, c.ClientCertificate, c.ClientPrivateKey), secrets, [], ct);
        }
        var handler = new KubernetesPreflightHandler(codec, Fixture);
        using (var client = await Fixture(environment, configuration, token))
        {
            var subject = await client.Client.AuthenticationV1.CreateSelfSubjectReviewAsync(new V1SelfSubjectReview(), cancellationToken: token);
            Check(subject.Status?.UserInfo?.Username == expectedUsername && subject.Status.UserInfo.Uid == expectedUid, "P01-exact-bearer-identity");
        }
        Check((await handler.CheckTypedAsync(environment, configuration, token)).IsReady, "P02-bearer-access-only-fixture-TLS");
        var mutual = configuration with { BearerToken = null, ClientCertificate = cert, ClientPrivateKey = key };
        using (var client = await Fixture(environment, mutual, token))
        {
            var subject = await client.Client.AuthenticationV1.CreateSelfSubjectReviewAsync(new V1SelfSubjectReview(), cancellationToken: token);
            Check(subject.Status?.UserInfo?.Username == "system:admin", "P03-exact-mtls-identity");
        }
        Check((await handler.CheckTypedAsync(environment, mutual, token)).IsReady, "P04-mtls-access-only-fixture-TLS");
        Check((await handler.CheckTypedAsync(environment, configuration with { BearerToken = invalid }, token)).Outcome == ProviderPreflightOutcome.AuthenticationFailed, "P05-invalid-bearer");
        Check((await handler.CheckTypedAsync(environment, configuration with { ServerCaPem = null }, token)).Outcome == ProviderPreflightOutcome.ServerUnavailable, "P06-untrusted-server");
        using var rsa = RSA.Create(2048);
        var wrongKey = secrets.Add(Encoding.UTF8.GetBytes(rsa.ExportPkcs8PrivateKeyPem()));
        Check((await handler.CheckTypedAsync(environment, mutual with { ClientPrivateKey = wrongKey }, token)).Outcome == ProviderPreflightOutcome.ConfigurationUnavailable, "P07-mismatched-client-key");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Check((await handler.CheckTypedAsync(environment, configuration with { Endpoint = new Uri($"https://127.0.0.1:{port}") }, token)).Outcome == ProviderPreflightOutcome.ServerUnavailable, "P08-unavailable-endpoint");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await handler.CheckTypedAsync(environment, configuration, canceled.Token); throw new InvalidOperationException("Cancellation ignored."); }
        catch (OperationCanceledException) { Check(true, "P09-caller-cancellation"); }
        // k3s fixture certificates have no revocation service. Production must refuse them;
        // fixture positive access does not establish production Online revocation success.
        var production = new KubernetesPreflightHandler(codec, secrets);
        Check((await production.CheckTypedAsync(environment, configuration, token)).Outcome == ProviderPreflightOutcome.ServerUnavailable, "P10-production-revocation-fails-closed");
    }
    private static void Check(bool value, string id)
    {
        Console.WriteLine((value ? "PASS " : "FAIL ") + "provider-" + id);
        if (!value) throw new InvalidOperationException("Provider access assertion failed.");
    }
    private sealed class Secrets(EnvironmentRecordId environment) : ISecretStore, IDisposable
    {
        private readonly Dictionary<SecretRef, byte[]> _values = [];
        internal SecretRef Add(byte[] bytes) { var reference = new SecretRef(SecretId.New(), SecretVersionId.New()); _values.Add(reference, bytes); return reference; }
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition != new SecretDefinition(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator)) return ValueTask.FromResult<SecretMaterial?>(null);
            return ValueTask.FromResult(_values.TryGetValue(reference, out var bytes) && bytes.Length <= maximumBytes ? new SecretMaterial(bytes) : null);
        }
        public void Dispose() { foreach (var bytes in _values.Values) CryptographicOperations.ZeroMemory(bytes); }
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
        public ValueTask AddVersionAsync(EnvironmentRecordId owner, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
    }
}
