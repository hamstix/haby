using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using k8s;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class ProviderAdministrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declaration_binds_exact_alternatives_and_roundtrips_with_the_same_codec(bool certificate)
    {
        var codec = new KubernetesProviderConfigurationCodec(); var adapter = new KubernetesProviderDeclarationAdapter(codec);
        using var json = JsonDocument.Parse("""{"endpoint":"https://API.example:6443"}""");
        var refs = certificate ? new Dictionary<string, SecretRef> { ["client-certificate"] = Ref(), ["client-private-key"] = Ref() }
            : new Dictionary<string, SecretRef> { ["bearer-token"] = Ref() };
        var value = (KubernetesProviderConfiguration)adapter.Bind(adapter.ParseProperties(json.RootElement), refs);
        var prepared = codec.Encode(value);
        Assert.Equal(value, codec.DecodeTyped(prepared.PayloadJson));
        Assert.Equal(refs.Count, prepared.SecretReferences.Count);
        Assert.All(prepared.SecretReferences, r => Assert.Equal(refs[r.Slot], r.Reference));
        Assert.True(adapter.SameEndpoint(value, value));
        Assert.False(adapter.SameEndpoint(value, ((KubernetesProviderConfiguration)value) with { Endpoint = new("https://other.example") }));
        Assert.Same(codec, adapter.Codec);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"endpoint\":null}")]
    [InlineData("{\"endpoint\":\"http://api.example\"}")]
    [InlineData("{\"endpoint\":\"https://api.example\",\"namespace\":\"default\"}")]
    [InlineData("{\"endpoint\":\"https://api.example\",\"token\":\"private\"}")]
    [InlineData("{\"endpoint\":\"https://api.example\",\"serverCaPem\":null}")]
    [InlineData("{\"endpoint\":\"https://api.example\",\"serverCaPem\":\"malformed\"}")]
    public void Malformed_properties_refuse_before_reference_resolution(string json)
    {
        using var document = JsonDocument.Parse(json);
        var adapter = new KubernetesProviderDeclarationAdapter(new());
        Assert.ThrowsAny<Exception>(() => adapter.ParseProperties(document.RootElement));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Empty_incomplete_mixed_or_unknown_credential_slots_refuse(int variant)
    {
        using var json = JsonDocument.Parse("""{"endpoint":"https://api.example"}""");
        var adapter = new KubernetesProviderDeclarationAdapter(new());
        var refs = new Dictionary<string, SecretRef>();
        if (variant == 1) refs.Add("client-certificate", Ref());
        if (variant == 2) { refs.Add("client-certificate", Ref()); refs.Add("client-private-key", Ref()); refs.Add("bearer-token", Ref()); }
        if (variant == 3) refs.Add("unknown", Ref());
        Assert.ThrowsAny<ArgumentException>(() => adapter.Bind(adapter.ParseProperties(json.RootElement), refs));
    }

    [Fact]
    public void Production_registration_has_exact_shared_codec_and_rejects_ambiguity_or_different_readers()
    {
        var codec = new KubernetesProviderConfigurationCodec();
        var adapter = new KubernetesProviderDeclarationAdapter(codec);
        var handler = new KubernetesPreflightHandler((KubernetesProviderConfigurationCodec)codec, new Secrets());
        _ = new ProviderDeclarationRegistry([adapter], [codec]);
        Assert.Same(handler, new ProviderPreflightRegistry([handler], [codec], [adapter]).Find(adapter.Key));
        Assert.Throws<InvalidOperationException>(() => new ProviderPreflightRegistry([handler, handler], [codec], [adapter]));
        Assert.Throws<InvalidOperationException>(() => new ProviderPreflightRegistry([handler], [new KubernetesProviderConfigurationCodec()], [adapter]));
    }

    [Theory]
    [InlineData("authenticated", 201, ProviderPreflightOutcome.Ready)]
    [InlineData("system:anonymous", 201, ProviderPreflightOutcome.AuthenticationFailed)]
    [InlineData("", 201, ProviderPreflightOutcome.AuthenticationFailed)]
    [InlineData("unauthenticated-group", 201, ProviderPreflightOutcome.AuthenticationFailed)]
    [InlineData("authenticated", 401, ProviderPreflightOutcome.AuthenticationFailed)]
    [InlineData("authenticated", 403, ProviderPreflightOutcome.InsufficientPrivilege)]
    [InlineData("authenticated", 503, ProviderPreflightOutcome.ServerUnavailable)]
    public async Task Review_requires_authenticated_identity_and_never_probes_a_namespace(string username, int status, ProviderPreflightOutcome expected)
    {
        var api = new ReviewApi(username, status);
        var handler = new KubernetesPreflightHandler(new(), (_, _, _) => ValueTask.FromResult(new KernelClient(
            new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api))));
        Assert.Equal(expected, (await handler.CheckTypedAsync(EnvironmentRecordId.New(), Configuration(), default)).Outcome);
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_missing_exact_secret_refuses_without_network()
    {
        var secrets = new Secrets(); var environment = EnvironmentRecordId.New();
        var handler = new KubernetesPreflightHandler(new(), secrets);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await handler.CheckTypedAsync(environment, Configuration(), cancellation.Token));
        Assert.Equal(0, secrets.Reads);
        Assert.Equal(ProviderPreflightOutcome.SecretUnavailable, (await handler.CheckTypedAsync(environment, Configuration(), default)).Outcome);
        Assert.Equal(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator), secrets.Definition);
    }

    [Fact]
    public void Explicit_custom_trust_accepts_certificates_and_refuses_private_key_or_trailing_data()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Provider Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var codec = new KubernetesProviderConfigurationCodec(); var configuration = Configuration() with { ServerCaPem = ca.ExportCertificatePem() };
        Assert.Equal(configuration, codec.DecodeTyped(codec.Encode(configuration).PayloadJson));
        Assert.ThrowsAny<ArgumentException>(() => codec.Encode(configuration with { ServerCaPem = configuration.ServerCaPem + key.ExportPkcs8PrivateKeyPem() }));
        Assert.ThrowsAny<ArgumentException>(() => codec.Encode(configuration with { ServerCaPem = configuration.ServerCaPem + "trailing-data" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task In_flight_work_observes_caller_cancellation_or_the_bounded_deadline(bool callerCancellation)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new KubernetesPreflightHandler(new(), async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable.");
        });
        using var caller = new CancellationTokenSource();
        var check = handler.CheckTypedAsync(EnvironmentRecordId.New(), Configuration(), caller.Token).AsTask();
        await entered.Task;
        if (callerCancellation)
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        }
        else Assert.Equal(ProviderPreflightOutcome.DeadlineExceeded, (await check.WaitAsync(TimeSpan.FromSeconds(20))).Outcome);
    }

    private static SecretRef Ref() => new(SecretId.New(), SecretVersionId.New());
    private static KubernetesProviderConfiguration Configuration() => new(new("https://api.example"), null, Ref(), null, null);
    private sealed class ReviewApi(string username, int status) : DelegatingHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/apis/authentication.k8s.io/v1/selfsubjectreviews", request.RequestUri!.AbsolutePath);
            var group = username == "unauthenticated-group" ? "system:unauthenticated" : "system:authenticated";
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                RequestMessage = request,
                Content = new StringContent($$$$"""{"apiVersion":"authentication.k8s.io/v1","kind":"SelfSubjectReview","status":{"userInfo":{"username":"{{{{username}}}}","groups":["{{{{group}}}}"]}}} """, Encoding.UTF8, "application/json")
            });
        }
    }
    private sealed class Secrets : ISecretStore
    {
        internal int Reads; internal SecretDefinition? Definition;
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes, CancellationToken token)
        { Reads++; Definition = definition; Assert.Equal(1_048_576, maximumBytes); return ValueTask.FromResult<SecretMaterial?>(null); }
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
    }
}
