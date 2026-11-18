using System.Text;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Net;
using k8s;
using Haby.Configuration.Contracts;
using Haby.Kubernetes.Contracts;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class ConfiguredDeploymentTests(Xunit.Abstractions.ITestOutputHelper output)
{
    internal static DeploymentIntent Intent()
    {
        var original = KernelTests.Intent();
        var bootstrap = new RuntimeBootstrap(1, Guid.CreateVersion7().ToString("D"),
            ImmutableSortedDictionary<string, string>.Empty.Add("settings", Guid.CreateVersion7().ToString("D")).Add("other", Guid.CreateVersion7().ToString("D")),
            RuntimeBootstrap.TokenPath, new("https://configuration.example/", new("system"), new()));
        return original with
        {
            Target = original.Target with
            {
                Container = "orders-service",
                Runtime = new("orders-api-main-runtime", "account-uid",
            $"urn:haby:{Guid.CreateVersion7():D}:{original.Binding.Provider}", RuntimeBootstrapCodec.Encode(bootstrap))
            }
        };
    }
    [Fact]
    public void Placement_freezes_explicit_runtime_endpoint_trust_and_client_settings()
    {
        const string json = """{"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"configured","revision":1},"spec":{"namespace":"test","runtimeConfiguration":{"endpoint":"https://configuration.example:7443/","tls":{"mode":"system"},"client":{"totalTimeoutSeconds":90}}}}""";
        var codec = new KubernetesPlacementCodec();
        var saved = codec.Decode(Encoding.UTF8.GetBytes(json));
        Assert.Equal(saved, codec.Read(saved.Contract, saved.FormatVersion, saved.Payload));
        Assert.Contains("https://configuration.example:7443/", saved.Payload);
        Assert.Contains("totalTimeoutSeconds", saved.Payload);
    }

    [Fact]
    public void Full_projection_and_receipts_preserve_exact_nonsecret_bootstrap_and_container()
    {
        var intent = Intent(); var json = DeploymentIntents.Encode(intent);
        Assert.Equal(json, DeploymentIntents.Encode(DeploymentIntents.Decode(json)));
        var obj = KernelTests.Object(intent);
        var receipt = DeploymentObjects.Receipt(obj, intent, ConfirmationSource.Response);
        Assert.Equal(json, DeploymentIntents.Encode(intent with { Target = receipt.Projection }));
        var spec = obj["spec"]!["template"]!["spec"]!;
        Assert.Equal("orders-service", spec["containers"]![0]!["name"]!.GetValue<string>());
        Assert.False(spec["automountServiceAccountToken"]!.GetValue<bool>());
        Assert.Equal(3600, spec["volumes"]![0]!["projected"]!["sources"]![0]!["serviceAccountToken"]!["expirationSeconds"]!.GetValue<int>());
        Assert.Null(spec["containers"]![0]!["volumeMounts"]![0]!["subPath"]);
        var bootstrap = RuntimeBootstrapCodec.Decode(intent.Target.Runtime!.Bootstrap);
        Assert.Equal(new[] { "other", "settings" }, bootstrap.Documents.Keys);
        Assert.Equal(intent.Target.Runtime.Bootstrap, RuntimeBootstrapCodec.Encode(bootstrap));
        Assert.DoesNotContain("bearer", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("serviceAccountName")]
    [InlineData("automount")]
    [InlineData("missing-automount")]
    [InlineData("bootstrap")]
    [InlineData("duplicate-env")]
    [InlineData("valueFrom")]
    [InlineData("audience")]
    [InlineData("expiration")]
    [InlineData("path")]
    [InlineData("mode")]
    [InlineData("extra-source")]
    [InlineData("readOnly")]
    [InlineData("subPath")]
    [InlineData("subPathExpr")]
    [InlineData("mountPropagation")]
    [InlineData("mountPath")]
    [InlineData("duplicate-volume")]
    [InlineData("injected-token-mount")]
    [InlineData("overlapping-mount")]
    [InlineData("envFrom")]
    public void Every_owned_runtime_field_is_required_for_response_and_observation(string fault)
    {
        var intent = Intent(); var obj = KernelTests.Object(intent);
        var spec = obj["spec"]!["template"]!["spec"]!;
        var main = spec["containers"]![0]!; var volume = spec["volumes"]![0]!;
        var projection = volume["projected"]!; var source = projection["sources"]![0]!["serviceAccountToken"]!;
        var mount = main["volumeMounts"]![0]!;
        switch (fault)
        {
            case "serviceAccountName": spec["serviceAccountName"] = "foreign"; break;
            case "automount": spec["automountServiceAccountToken"] = true; break;
            case "missing-automount": spec.AsObject().Remove("automountServiceAccountToken"); break;
            case "bootstrap": main["env"]![0]!["value"] = "{}"; break;
            case "duplicate-env": main["env"]!.AsArray().Add(main["env"]![0]!.DeepClone()); break;
            case "valueFrom": main["env"]![0]!["valueFrom"] = new JsonObject(); break;
            case "audience": source["audience"] = "foreign"; break;
            case "expiration": source["expirationSeconds"] = 600; break;
            case "path": source["path"] = "different"; break;
            case "mode": projection["defaultMode"] = 384; break;
            case "extra-source": projection["sources"]!.AsArray().Add(new JsonObject { ["downwardAPI"] = new JsonObject() }); break;
            case "readOnly": mount["readOnly"] = false; break;
            case "subPath": mount["subPath"] = "token"; break;
            case "subPathExpr": mount["subPathExpr"] = "token"; break;
            case "mountPropagation": mount["mountPropagation"] = "Bidirectional"; break;
            case "mountPath": mount["mountPath"] = "/elsewhere"; break;
            case "duplicate-volume": spec["volumes"]!.AsArray().Add(volume.DeepClone()); break;
            case "injected-token-mount": spec["containers"]!.AsArray().Add(new JsonObject { ["name"] = "injected", ["image"] = "example", ["volumeMounts"] = new JsonArray(mount.DeepClone()) }); break;
            case "overlapping-mount": main["volumeMounts"]!.AsArray().Add(new JsonObject { ["name"] = "foreign", ["mountPath"] = "/var/run" }); break;
            case "envFrom": main["envFrom"] = new JsonArray(new JsonObject()); break;
        }
        foreach (var confirmation in new[] { ConfirmationSource.Response, ConfirmationSource.Observation })
            Assert.Throws<WorkloadContractException>(() => DeploymentObjects.Receipt(obj, intent, confirmation));
    }

    [Theory]
    [InlineData("http://configuration.example/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://localhost/")]
    [InlineData("https://0.0.0.0/")]
    [InlineData("https://user:password@configuration.example/")]
    [InlineData("https://configuration.example/path")]
    [InlineData("https://configuration.example/?token=x")]
    [InlineData("https://configuration.example/#x")]
    public void Endpoint_cannot_supply_credentials_or_a_mutable_loopback_fallback(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => RuntimeBootstrapCodec.Validate(new RuntimeConfiguration(endpoint, new("system"), new())));
    }

    [Fact]
    public void Changed_runtime_identity_and_superseded_format_refuse_but_absent_configuration_has_no_runtime_fields()
    {
        var configured = Intent();
        Assert.Throws<WorkloadContractException>(() => DeploymentIntents.Encode(configured with
        { ExpectedUid = "saved-uid", LastConfirmed = configured.Target with { Runtime = configured.Target.Runtime! with { ServiceAccountUid = "replacement" } } }));
        Assert.Throws<WorkloadContractException>(() => DeploymentIntents.Encode(configured with { Format = 1 }));
        var plain = KernelTests.Object(KernelTests.Intent()); var spec = plain["spec"]!["template"]!["spec"]!;
        Assert.Null(spec["serviceAccountName"]); Assert.Null(spec["automountServiceAccountToken"]); Assert.Null(spec["volumes"]);
        Assert.Null(spec["containers"]![0]!["env"]); Assert.Null(spec["containers"]![0]!["volumeMounts"]);
    }

    [Theory]
    [InlineData("uid")]
    [InlineData("missing")]
    [InlineData("owner")]
    [InlineData("automount")]
    public async Task Observation_requires_the_original_live_account_UID_and_ownership_without_writes(string fault)
    {
        var intent = Intent(); var api = new RuntimeApi(intent, fault);
        var control = new RuntimeApi(intent, "valid");
        var positive = await new DeploymentKernel(intent.Binding, _ => ValueTask.FromResult(new KernelClient(
            new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, control)))).ObserveAsync(intent);
        Assert.True(positive.Outcome == KernelOutcome.Confirmed, $"Positive transport: {positive.Outcome}/{positive.Code}");
        var kernel = new DeploymentKernel(intent.Binding, _ => ValueTask.FromResult(new KernelClient(
            new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api))));
        var result = await kernel.ObserveAsync(intent);
        output.WriteLine($"Outcome={result.Outcome}; Code={result.Code}; API reads={api.Reads}");
        Assert.Equal(KernelOutcome.NotConfirmed, result.Outcome);
        Assert.Equal(0, result.WriteDispatches); Assert.Equal(0, api.Writes);
    }
    private sealed class RuntimeApi(DeploymentIntent intent, string fault) : DelegatingHandler
    {
        internal int Writes, Reads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get) Writes++;
            else Reads++;
            var account = request.RequestUri!.AbsolutePath.Contains("/serviceaccounts/", StringComparison.Ordinal);
            var body = account ? new JsonObject
            {
                ["metadata"] = new JsonObject
                {
                    ["name"] = intent.Target.Runtime!.ServiceAccount,
                    ["namespace"] = intent.Namespace,
                    ["uid"] = fault == "uid" ? "replacement" : intent.Target.Runtime.ServiceAccountUid,
                    ["labels"] = new JsonObject
                    {
                        [DeploymentIntents.OwnerKey] = fault == "owner" ? "foreign" : intent.Owner.ToString("N"),
                        [DeploymentIntents.ManagedKey] = intent.ManagedWorkload.ToString("N")
                    }
                },
                ["automountServiceAccountToken"] = fault == "automount"
            } : KernelTests.Object(intent);
            return Task.FromResult(new HttpResponseMessage(account && fault == "missing" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }
}
