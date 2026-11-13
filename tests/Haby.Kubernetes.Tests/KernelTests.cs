using System.Collections.Immutable;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes.Contracts;
using k8s;
using Xunit;
using static Haby.Kubernetes.DeploymentIntents;

namespace Haby.Kubernetes.Tests;

public sealed class KernelTests
{
    internal static DeploymentIntent Intent() => new(1,
        new(EnvironmentRecordId.New().ToString(), ProviderInstanceId.New().ToString(), 3, "haby.kubernetes/v1"),
        "api", "main", "standard", 1, "test", "orders-api-main", Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
        new("app", "registry.example/app:v1", 1, "RollingUpdate", "25%", "25%", 0, 600,
            ImmutableSortedDictionary<string, string>.Empty.Add("disk", "ssd"), ["registry"]), null, null);
    internal static JsonObject Object(DeploymentIntent i, string uid = "saved-uid", string rv = "19")
    {
        var obj = DeploymentObjects.Read(DeploymentObjects.Create(i));
        obj["metadata"]!["uid"] = uid; obj["metadata"]!["resourceVersion"] = rv; obj["metadata"]!["generation"] = 1;
        return obj;
    }
    private static DeploymentIntent Update(DeploymentIntent i) => i with
    {
        ExpectedUid = "saved-uid",
        LastConfirmed = i.Target,
        Operation = Guid.NewGuid(),
        TargetGeneration = 2,
        Target = i.Target with { Image = "registry.example/app:v2" }
    };
    private static DeploymentKernel Kernel(DeploymentIntent i, FakeApi api) => new(i.Binding,
        _ => ValueTask.FromResult(new KernelClient(new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api))));

    [Fact]
    public void Closed_intent_and_receipt_roundtrip_preserves_exact_authority_without_credential_material()
    {
        var i = Intent(); var json = Encode(i); var decoded = Decode(json);
        Assert.Equal(json, Encode(decoded)); Assert.Equal(i.Binding, decoded.Binding); Assert.Equal(i.Operation, decoded.Operation);
        var r = DeploymentObjects.Receipt(Object(i), i, ConfirmationSource.Response);
        Assert.Equal(Encode(r), Encode(DecodeReceipt(Encode(r))));
        Assert.DoesNotContain("certificate", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("endpoint", json, StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData("unknown", "true")]
    [InlineData("format", "2")]
    [InlineData("operation", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("lastConfirmed", "{}")]
    [InlineData("target", "null")]
    public void Malformed_or_open_intents_refuse(string property, string value)
    {
        var obj = JsonNode.Parse(Encode(Intent()))!; obj[property] = JsonNode.Parse(value);
        Assert.Throws<WorkloadContractException>(() => Decode(obj.ToJsonString()));
    }
    [Theory]
    [InlineData("name")]
    [InlineData("bound")]
    [InlineData("duplicate-json")]
    public void Frozen_codec_rejects_noncanonical_suffixes_and_duplicate_properties(string fault)
    {
        var i = Intent();
        if (fault == "name") i = i with { Name = "orders-api-main\n" };
        if (fault == "bound") i = i with { Target = i.Target with { MaxSurge = "25\n" } };
        if (fault == "duplicate-json")
        {
            var json = Encode(i); Assert.Throws<WorkloadContractException>(() => Decode("{\"format\":1," + json[1..]));
        }
        else Assert.Throws<WorkloadContractException>(() => Encode(i));
    }
    [Fact]
    public async Task Create_dispatches_once_and_returns_response_evidence()
    {
        var i = Intent(); var api = new FakeApi((request, _) => request.Method == HttpMethod.Get ? Reply(404) : Reply(201, Object(i)));
        var result = await Kernel(i, api).ExecuteAsync(i);
        Assert.Equal(KernelOutcome.Confirmed, result.Outcome); Assert.Equal(1, result.WriteDispatches);
        Assert.Equal(ConfirmationSource.Response, result.Receipt!.Source); Assert.Equal("saved-uid", result.Receipt.Uid);
        Assert.Equal(new[] { "GET", "POST" }, api.Methods);
        var body = JsonNode.Parse(api.Bodies.Single())!;
        Assert.Null(body["spec"]!["template"]!["metadata"]!["annotations"]);
    }
    [Fact]
    public async Task Occupied_name_never_adopts_even_with_matching_markers()
    {
        var i = Intent(); var api = new FakeApi((_, _) => Reply(200, Object(i)));
        var result = await Kernel(i, api).ExecuteAsync(i);
        Assert.Equal(KernelOutcome.Conflict, result.Outcome); Assert.Equal(0, result.WriteDispatches); Assert.Equal(new[] { "GET" }, api.Methods);
    }
    [Theory]
    [InlineData("transport")]
    [InlineData("cancellation")]
    [InlineData("server")]
    [InlineData("response-projection")]
    public async Task Postdispatch_ambiguity_never_retries_and_stateless_reload_observes_readonly(string fault)
    {
        var i = Intent(); var obj = Object(i); var accepted = false;
        var api = new FakeApi((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return accepted ? Reply(200, obj) : Reply(404);
            accepted = true;
            return fault switch
            {
                "transport" => throw new HttpRequestException("private marker"),
                "cancellation" => throw new OperationCanceledException("private marker"),
                "server" => Reply(500),
                _ => Reply(201, new JsonObject())
            };
        });
        var original = await Kernel(i, api).ExecuteAsync(i);
        Assert.Equal(KernelOutcome.Uncertain, original.Outcome); Assert.Null(original.Receipt); Assert.Equal(1, original.WriteDispatches);
        var reloaded = Decode(Encode(i));
        var observeApi = new FakeApi((_, _) => Reply(200, obj));
        var observed = await Kernel(reloaded, observeApi).ObserveAsync(reloaded);
        Assert.Equal(KernelOutcome.Confirmed, observed.Outcome); Assert.Equal(0, observed.WriteDispatches);
        Assert.Equal(ConfirmationSource.Observation, observed.Receipt!.Source); Assert.Equal(i.Operation, observed.Receipt.Operation);
        Assert.Equal(new[] { "GET" }, observeApi.Methods); Assert.Equal(KernelOutcome.Uncertain, original.Outcome);
        Assert.Single(api.Methods, x => x != "GET");
    }
    [Theory]
    [InlineData("absent")]
    [InlineData("missing-marker")]
    [InlineData("wrong-marker")]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("projection")]
    [InlineData("replacement")]
    [InlineData("unavailable")]
    public async Task Observation_cannot_confirm_missing_foreign_or_unavailable_evidence(string fault)
    {
        var i = Update(Intent()); var obj = Object(i);
        switch (fault)
        {
            case "missing-marker": obj["metadata"]!["annotations"]!.AsObject().Remove(OperationKey); break;
            case "wrong-marker": obj["metadata"]!["annotations"]![OperationKey] = Guid.NewGuid().ToString("N"); break;
            case "owner": obj["metadata"]!["labels"]![OwnerKey] = Guid.NewGuid().ToString("N"); break;
            case "generation": obj["metadata"]!["annotations"]![GenerationKey] = "1"; break;
            case "projection": obj["spec"]!["replicas"] = 9; break;
            case "replacement": obj["metadata"]!["uid"] = "foreign-uid"; break;
        }
        var api = new FakeApi((_, _) => Reply(fault == "absent" ? 404 : fault == "unavailable" ? 503 : 200, obj));
        var result = await Kernel(i, api).ObserveAsync(i);
        Assert.Equal(fault == "unavailable" ? KernelOutcome.ObservationUnavailable : KernelOutcome.NotConfirmed, result.Outcome);
        Assert.Null(result.Receipt); Assert.Equal(0, result.WriteDispatches); Assert.Equal(new[] { "GET" }, api.Methods);
    }
    [Fact]
    public async Task Known_predispatch_cancellation_does_not_resolve_secrets_or_contact_API()
    {
        var i = Intent(); var connections = 0;
        var kernel = new DeploymentKernel(i.Binding, _ => { connections++; throw new Exception(); });
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await kernel.ExecuteAsync(i, cancel.Token);
        Assert.Equal(KernelOutcome.CanceledBeforeDispatch, result.Outcome); Assert.Equal(0, connections); Assert.Equal(0, result.WriteDispatches);
    }
    [Fact]
    public async Task Wrong_exact_provider_refuses_before_resolution()
    {
        var i = Intent(); var connections = 0;
        var kernel = new DeploymentKernel(i.Binding with { Configuration = 4 }, _ => { connections++; throw new Exception(); });
        Assert.Equal(KernelOutcome.Refused, (await kernel.ExecuteAsync(i)).Outcome); Assert.Equal(0, connections);
    }
    [Theory]
    [InlineData("absent")]
    [InlineData("uid")]
    [InlineData("owner")]
    [InlineData("image")]
    [InlineData("selector")]
    [InlineData("container")]
    [InlineData("duplicate-container")]
    [InlineData("node")]
    [InlineData("pull")]
    [InlineData("reserved-template")]
    public async Task Update_refuses_missing_or_drifted_state_without_writes(string fault)
    {
        var old = Intent(); var i = Update(old); var obj = Object(old);
        switch (fault)
        {
            case "uid": obj["metadata"]!["uid"] = "foreign"; break;
            case "owner": obj["metadata"]!["labels"]![OwnerKey] = "foreign"; break;
            case "image": obj["spec"]!["template"]!["spec"]!["containers"]![0]!["image"] = "manual:v3"; break;
            case "selector": obj["spec"]!["selector"]!["matchLabels"]!["extra"] = "value"; break;
            case "container": obj["spec"]!["template"]!["spec"]!["containers"]![0]!["name"] = "renamed"; break;
            case "duplicate-container": obj["spec"]!["template"]!["spec"]!["containers"]!.AsArray().Add(obj["spec"]!["template"]!["spec"]!["containers"]![0]!.DeepClone()); break;
            case "node": obj["spec"]!["template"]!["spec"]!["nodeSelector"]!["disk"] = "hdd"; break;
            case "pull": obj["spec"]!["template"]!["spec"]!["imagePullSecrets"] = new JsonArray(); break;
            case "reserved-template": obj["spec"]!["template"]!["metadata"]!["labels"]![OperationKey] = "invalid"; break;
        }
        var api = new FakeApi((_, _) => Reply(fault == "absent" ? 404 : 200, obj));
        var result = await Kernel(i, api).ExecuteAsync(i);
        Assert.Equal(KernelOutcome.Conflict, result.Outcome); Assert.Equal(0, result.WriteDispatches);
        Assert.Equal(new[] { "GET" }, api.Methods);
    }
    [Fact]
    public async Task Image_update_guards_keyed_container_and_UID_RV_without_replacing_other_fields()
    {
        var old = Intent(); var i = Update(old); var obj = Object(old);
        obj["spec"]!["template"]!["spec"]!["containers"]!.AsArray().Insert(0, new JsonObject { ["name"] = "injected", ["image"] = "sidecar:v1" });
        obj["metadata"]!["annotations"]!["foreign"] = "preserve";
        var api = new FakeApi((request, _) => Reply(200, request.Method == HttpMethod.Get ? obj : Object(i)));
        Assert.Equal(KernelOutcome.Confirmed, (await Kernel(i, api).ExecuteAsync(i)).Outcome);
        var patch = JsonNode.Parse(api.Bodies.Single())!.AsArray();
        Assert.Contains(patch, n => n!["op"]!.GetValue<string>() == "test" && n["path"]!.GetValue<string>() == "/metadata/uid" && n["value"]!.GetValue<string>() == "saved-uid");
        Assert.Contains(patch, n => n!["op"]!.GetValue<string>() == "test" && n["path"]!.GetValue<string>() == "/metadata/resourceVersion" && n["value"]!.GetValue<string>() == "19");
        Assert.Contains(patch, n => n!["op"]!.GetValue<string>() == "test" && n["path"]!.GetValue<string>() == "/spec/template/spec/containers/1/name" && n["value"]!.GetValue<string>() == "app");
        Assert.Contains(patch, n => n!["path"]!.GetValue<string>() == "/spec/template/spec/containers/1/image");
        Assert.DoesNotContain(patch, n => new[] { "/metadata", "/metadata/annotations", "/spec/template/spec/containers", "/spec/selector" }.Contains(n!["path"]!.GetValue<string>()));
    }
    [Theory]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(401)]
    public async Task Generic_rejections_never_retry(int status)
    {
        var old = Intent(); var i = Update(old); var api = new FakeApi((request, _) => request.Method == HttpMethod.Get ? Reply(200, Object(old)) : Reply(status));
        var result = await Kernel(i, api).ExecuteAsync(i);
        Assert.Equal(KernelOutcome.Conflict, result.Outcome); Assert.Equal(1, result.WriteDispatches); Assert.Equal(new[] { "GET", "PATCH" }, api.Methods);
    }
    [Theory]
    [InlineData("transport")]
    [InlineData("cancellation")]
    [InlineData("server")]
    public async Task Uncertain_update_never_replays_the_conditional_write(string fault)
    {
        var old = Intent(); var i = Update(old);
        var api = new FakeApi((request, _) => request.Method == HttpMethod.Get ? Reply(200, Object(old)) : fault switch
        { "transport" => throw new HttpRequestException("private marker"), "cancellation" => throw new OperationCanceledException("private marker"), _ => Reply(500) });
        var result = await Kernel(i, api).ExecuteAsync(i);
        Assert.Equal(KernelOutcome.Uncertain, result.Outcome); Assert.Equal(1, result.WriteDispatches); Assert.Null(result.Receipt);
        Assert.Equal(new[] { "GET", "PATCH" }, api.Methods);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Positive_RV_test_rejections_revalidate_fresh_state_and_stop_at_three_dispatches(bool eventuallyAccepted)
    {
        var old = Intent(); var i = Update(old); var reads = 0; var writes = 0;
        var api = new FakeApi((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Reply(200, Object(old, rv: (++reads).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            writes++;
            return eventuallyAccepted && writes == 3 ? Reply(200, Object(i)) : PositiveTestRejection();
        });
        var result = await Kernel(i, api).ExecuteAsync(i);
        Assert.Equal(eventuallyAccepted ? KernelOutcome.Confirmed : KernelOutcome.Conflict, result.Outcome);
        Assert.Equal(3, result.WriteDispatches); Assert.Equal(3, writes); Assert.Equal(3, reads);
        Assert.Equal(new[] { "1", "2", "3" }, api.Bodies.Select(body => JsonNode.Parse(body)!.AsArray().Single(n => n!["path"]!.GetValue<string>() == "/metadata/resourceVersion")!["value"]!.GetValue<string>()));
    }
    [Theory]
    [InlineData("owner")]
    [InlineData("projection")]
    [InlineData("uid")]
    public async Task Positive_precondition_rejection_does_not_skip_next_ownership_or_drift_validation(string drift)
    {
        var old = Intent(); var i = Update(old); var reads = 0;
        var api = new FakeApi((request, _) =>
        {
            if (request.Method != HttpMethod.Get) return PositiveTestRejection();
            var obj = Object(old);
            if (++reads == 2)
            {
                if (drift == "uid") obj["metadata"]!["uid"] = "foreign";
                if (drift == "owner") obj["metadata"]!["labels"]![OwnerKey] = "foreign";
                if (drift == "projection") obj["spec"]!["replicas"] = 7;
            }
            return Reply(200, obj);
        });
        var result = await Kernel(i, api).ExecuteAsync(i);
        Assert.Equal(KernelOutcome.Conflict, result.Outcome); Assert.Equal(1, result.WriteDispatches);
        Assert.Equal(new[] { "GET", "PATCH", "GET" }, api.Methods);
    }
    [Theory]
    [InlineData("wrong-reason")]
    [InlineData("uid-test")]
    [InlineData("suffix")]
    public async Task Similar_but_nonpositive_precondition_evidence_never_retries(string fault)
    {
        var old = Intent(); var i = Update(old);
        var api = new FakeApi((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Reply(200, Object(old));
            var response = PositiveTestRejection(); var body = JsonNode.Parse(response.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            var cause = body["details"]!["causes"]![0]!;
            if (fault == "wrong-reason") cause["reason"] = "FieldValueRequired";
            if (fault == "uid-test") cause["message"] = "Invalid value: patch: testing value /metadata/uid failed: test failed";
            if (fault == "suffix") cause["message"] = "Invalid value: patch: testing value /metadata/resourceVersion failed: test failed suffix";
            response.Dispose(); return Reply(422, body.AsObject());
        });
        var result = await Kernel(i, api).ExecuteAsync(i);
        Assert.Equal(KernelOutcome.Conflict, result.Outcome); Assert.Equal(1, result.WriteDispatches); Assert.Equal(new[] { "GET", "PATCH" }, api.Methods);
    }
    private static HttpResponseMessage PositiveTestRejection() => Reply(422, JsonNode.Parse("""
        {"apiVersion":"v1","kind":"Status","status":"Failure","reason":"Invalid","code":422,
         "details":{"causes":[{"field":"patch","reason":"FieldValueInvalid","message":"Invalid value: patch: testing value /metadata/resourceVersion failed: test failed"}]}}
        """u8)!.AsObject());
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Production_chain_policy_is_Online_failclosed_in_both_trust_modes(bool custom)
    {
        using var ca = MakeCa(); var roots = new X509Certificate2Collection(); if (custom) roots.Add(ca);
        using var chain = new X509Chain(); KubernetesClientFactory.ConfigureChain(chain.ChainPolicy, roots);
        Assert.Equal(X509RevocationMode.Online, chain.ChainPolicy.RevocationMode);
        Assert.Equal(X509VerificationFlags.NoFlag, chain.ChainPolicy.VerificationFlags);
        Assert.Equal(custom ? X509ChainTrustMode.CustomRootTrust : X509ChainTrustMode.System, chain.ChainPolicy.TrustMode);
        Assert.Contains(chain.ChainPolicy.ApplicationPolicy.Cast<Oid>(), p => p.Value == "1.3.6.1.5.5.7.3.1");
    }
    [Fact]
    public void Disposable_CA_has_no_production_revocation_fallback_and_name_checks_still_apply_in_fixture()
    {
        using var ca = MakeCa(); using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var leaf = request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        var roots = new X509Certificate2Collection(ca);
        Assert.True(KubernetesClientFactory.ValidateServer(leaf, SslPolicyErrors.RemoteCertificateChainErrors, roots, true));
        Assert.False(KubernetesClientFactory.ValidateServer(leaf, SslPolicyErrors.RemoteCertificateChainErrors, roots));
        Assert.False(KubernetesClientFactory.ValidateServer(leaf, SslPolicyErrors.RemoteCertificateNameMismatch, roots, true));
        Assert.False(KubernetesClientFactory.ValidateServer(leaf, SslPolicyErrors.None, new X509Certificate2Collection()));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Strict_factory_rejects_wrong_server_usage_and_expired_certificates_even_with_fixture_revocation_seam(bool expired)
    {
        using var ca = MakeCa(); using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(expired ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
        using var leaf = request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-4), expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        Assert.False(KubernetesClientFactory.ValidateServer(leaf, SslPolicyErrors.RemoteCertificateChainErrors, new X509Certificate2Collection(ca), true));
    }
    [Fact]
    public async Task Arbitrary_secret_store_failure_is_redacted_at_public_boundary()
    {
        var i = Intent(); var kernel = new DeploymentKernel(i.Binding, _ => throw new SecretBackendFailure("SECRET-MUST-NOT-ESCAPE"));
        var result = await kernel.ExecuteAsync(i);
        Assert.Equal(KernelOutcome.Refused, result.Outcome); Assert.Equal("connection-unavailable", result.Code);
        Assert.DoesNotContain("SECRET", result.ToString());
    }
    private sealed class SecretBackendFailure(string message) : Exception(message);
    private static X509Certificate2 MakeCa()
    {
        using var key = RSA.Create(2048); var request = new CertificateRequest("CN=disposable-ca", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
    }
    private static HttpResponseMessage Reply(int status, JsonObject? body = null) => new((HttpStatusCode)status) { Content = new StringContent(body?.ToJsonString() ?? $$"""{"apiVersion":"v1","kind":"Status","status":"Failure","reason":"Unknown","code":{{status}}}""", Encoding.UTF8, "application/json") };
    private sealed class FakeApi(Func<HttpRequestMessage, int, HttpResponseMessage> reply) : DelegatingHandler
    {
        internal List<string> Methods { get; } = []; internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method.Method); if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            var result = reply(request, Methods.Count); result.RequestMessage = request; return result;
        }
    }
}
