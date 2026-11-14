using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Kubernetes;
using k8s;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class RuntimeAuthenticationTests
{
    private static RuntimeTrust Trust()
    {
        var context = InternalDeploymentPlacementTests.Contexts(false, configured: true)[0] with { InstallationId = Guid.NewGuid() };
        var handler = new InternalRuntimeIdentityAction(null!);
        var receipt = new RuntimeAccountReceipt(RuntimeIdentityCodec.Decode(handler.Prepare(context)), "expected-uid", false, ConfirmationSource.Response);
        return new(Guid.NewGuid(), context.InstallationId, Guid.Parse(context.Provider.Environment.ToString()), context.ManagedIdentity,
            Guid.Parse(context.Provider.Instance.ToString()), context.Provider.Configuration.Value, context.Provider.Implementation.Value,
            KubernetesProviderConfigurationCodec.Kind, KubernetesProviderConfigurationCodec.Shape, 1, context.Configuration.PayloadJson,
            "haby.kubernetes.token-review/v1", RuntimeIdentityCodec.Audience(receipt.Intent), RuntimeIdentityCodec.ValidationContract, 1,
            RuntimeIdentityCodec.Validation(receipt), handler.Contract.OutputContract, 1, RuntimeIdentityCodec.Encode(receipt), false);
    }
    private static KubernetesRuntimeAuthenticator Adapter(Api api) => new(null!, (_, handlers, _) =>
    {
        // Use the real bounded response handler before the pinned SDK parses either success or error.
        return ValueTask.FromResult(new KernelClient(new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, [.. handlers, api])));
    });

    [Fact]
    public async Task One_call_uses_persisted_audience_and_authenticated_fields_without_returning_token()
    {
        var trust = Trust(); var api = new Api(trust); var adapter = Adapter(api);
        Assert.True(adapter.Validate(trust));
        Assert.Equal(RuntimeAuthenticationOutcome.Authenticated, await adapter.AuthenticateAsync(trust, "transient"u8.ToArray(), default));
        Assert.Equal(1, api.Calls); Assert.Equal(trust.Audience, api.Audience); Assert.Equal("transient", api.Token);
    }

    [Theory]
    [InlineData("audience")]
    [InlineData("namespace")]
    [InlineData("uid")]
    [InlineData("account")]
    [InlineData("pod-name")]
    [InlineData("pod-uid")]
    [InlineData("multiple-pods")]
    [InlineData("malformed-pod")]
    [InlineData("authenticated")]
    public async Task Malformed_or_foreign_authenticated_response_cannot_succeed(string fault)
    {
        var trust = Trust(); var api = new Api(trust) { Fault = fault };
        Assert.Equal(RuntimeAuthenticationOutcome.Unauthenticated, await Adapter(api).AuthenticateAsync(trust, "transient"u8.ToArray(), default));
        Assert.Equal(1, api.Calls);
    }

    [Theory]
    [InlineData("http-denied")]
    [InlineData("http-error")]
    [InlineData("error")]
    [InlineData("negative-error")]
    [InlineData("missing-user")]
    [InlineData("api-version")]
    [InlineData("loss")]
    [InlineData("oversized")]
    public async Task Api_failure_is_safe_unavailable_and_never_retried(string fault)
    {
        var trust = Trust(); var api = new Api(trust) { Fault = fault };
        Assert.Equal(RuntimeAuthenticationOutcome.Unavailable, await Adapter(api).AuthenticateAsync(trust, "transient"u8.ToArray(), default));
        Assert.Equal(1, api.Calls);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("installation")]
    [InlineData("provider")]
    [InlineData("configuration")]
    [InlineData("implementation")]
    [InlineData("audience")]
    [InlineData("receipt")]
    [InlineData("format")]
    public void Corrupt_trust_is_refused_without_network_or_secret_resolution(string fault)
    {
        var trust = Trust(); var api = new Api(trust);
        var changed = fault switch
        {
            "binding" => trust with { Binding = Guid.Empty },
            "installation" => trust with { Installation = Guid.NewGuid() },
            "provider" => trust with { Provider = Guid.NewGuid() },
            "configuration" => trust with { Configuration = trust.Configuration + 1 },
            "implementation" => trust with { Implementation = "foreign" },
            "audience" => trust with { Audience = "foreign" },
            "receipt" => trust with { InventoryPayload = "{}" },
            _ => trust with { InventoryFormat = 2 }
        };
        Assert.False(Adapter(api).Validate(changed)); Assert.Equal(0, api.Calls);
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 200)]
    [InlineData(false, 403)]
    [InlineData(true, 503)]
    public async Task Response_cap_applies_before_body_parsing_even_without_content_length(bool hasLength, int status)
    {
        using var handler = new BoundedTokenReviewResponse { InnerHandler = new Large(hasLength, status) };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<IOException>(() => client.GetAsync("https://fixture.invalid"));
    }

    private sealed class Large(bool hasLength, int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent content = new UnknownLength();
            if (hasLength) content.Headers.ContentLength = BoundedTokenReviewResponse.MaximumBytes + 1;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { RequestMessage = request, Content = content });
        }
    }
    private sealed class UnknownLength : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(new byte[BoundedTokenReviewResponse.MaximumBytes + 1]));
    }
    private sealed class Api(RuntimeTrust trust) : DelegatingHandler
    {
        internal string Fault = "";
        internal int Calls;
        internal string? Audience, Token;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Audience = body["spec"]!["audiences"]![0]!.GetValue<string>(); Token = body["spec"]!["token"]!.GetValue<string>();
            if (Fault == "loss") throw new HttpRequestException("transient private exception");
            var receipt = RuntimeIdentityCodec.Receipt(trust.InventoryPayload);
            var response = new JsonObject
            {
                ["apiVersion"] = Fault == "api-version" ? "other" : "authentication.k8s.io/v1",
                ["kind"] = "TokenReview",
                ["status"] = new JsonObject
                {
                    ["authenticated"] = Fault is not ("authenticated" or "negative-error"),
                    ["audiences"] = new JsonArray(Fault == "audience" ? "foreign" : trust.Audience),
                    ["user"] = new JsonObject
                    {
                        ["uid"] = Fault == "uid" ? "foreign" : receipt.Uid,
                        ["username"] = $"system:serviceaccount:{(Fault == "namespace" ? "foreign" : receipt.Intent.Namespace)}:{(Fault == "account" ? "foreign" : receipt.Intent.Name)}",
                        ["extra"] = new JsonObject { ["authentication.kubernetes.io/pod-name"] = new JsonArray("bound"), ["authentication.kubernetes.io/pod-uid"] = new JsonArray("pod-uid") }
                    }
                }
            };
            var user = response["status"]!["user"]!;
            if (Fault is "pod-name" or "pod-uid") user["extra"]!.AsObject().Remove("authentication.kubernetes.io/" + Fault);
            if (Fault == "multiple-pods") user["extra"]!["authentication.kubernetes.io/pod-uid"]!.AsArray().Add("foreign");
            if (Fault == "malformed-pod") user["extra"]!["authentication.kubernetes.io/pod-name"]![0] = "invalid/name";
            if (Fault == "missing-user") response["status"]!.AsObject().Remove("user");
            if (Fault is "error" or "negative-error") response["status"]!["error"] = "unstructured private authenticator error";
            return new HttpResponseMessage(Fault == "http-denied" ? HttpStatusCode.Forbidden : Fault == "http-error" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent(Fault == "oversized" ? new string('x', 65537) : response.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
