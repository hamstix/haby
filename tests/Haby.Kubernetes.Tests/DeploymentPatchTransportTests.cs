using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haby.Configuration.Contracts;
using k8s;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class DeploymentPatchTransportTests
{
    [Fact]
    public void Transport_preserves_all_json_values_and_server_scalar_tokens_for_escaping_boundaries()
    {
        foreach (var (value, expectedToken) in new[]
        {
            ("quote \" slash \\ newline \n tab \t", "\"quote \\\" slash \\\\ newline \\n tab \\t\""),
            ("café Проверка <>&\u2028\u2029", "\"café Проверка \\u003c\\u003e\\u0026\\u2028\\u2029\""),
            ("emoji 😀", "\"emoji 😀\""),
            ("\u001b\b\f\r", "\"\\u001b\\b\\f\\r\"")
        })
        {
            var patch = new JsonArray(new JsonObject { ["op"] = "test", ["path"] = "/fixture", ["value"] = value },
                new JsonObject
                {
                    ["op"] = "add",
                    ["path"] = "/nested",
                    ["value"] = new JsonObject
                    { ["text<&>"] = value, ["items"] = new JsonArray(1, true, null, new JsonObject { ["text"] = value }) }
                });
            var encoded = DeploymentPatchTransport.Serialize(patch);
            Assert.True(JsonNode.DeepEquals(patch, JsonNode.Parse(encoded)));
            using var wire = JsonDocument.Parse(encoded);
            Assert.Equal(value, wire.RootElement[0].GetProperty("value"u8).GetString());
            Assert.Equal(expectedToken, wire.RootElement[0].GetProperty("value"u8).GetRawText());
        }
    }
    [Fact]
    public async Task Actual_outbound_configured_update_preserves_nested_bootstrap_tests_and_confirms_one_patch()
    {
        var (previous, next) = Intents();
        var api = new RawScalarPatchApi(previous, changeOldBootstrap: false);
        DeploymentIntents.Validate(next);
        var result = await Kernel(next, api).ExecuteAsync(next);
        Assert.True(result.Outcome == KernelOutcome.Confirmed, $"PATCH not confirmed: {result.Code}; rejected guard: {api.FailedPath}");
        Assert.Equal(1, result.WriteDispatches);
        Assert.NotNull(result.Receipt);
        Assert.Equal(1, api.Writes);
        Assert.Null(api.FailedPath);
        Assert.Contains("/metadata/uid", api.TestedPaths);
        Assert.Contains("/metadata/resourceVersion", api.TestedPaths);
        Assert.Contains("/spec/template/spec/containers/1/name", api.TestedPaths);
        Assert.Contains("/spec/template/spec/containers/1/env/1/name", api.TestedPaths);
        Assert.Contains("/spec/template/spec/containers/1/env/1/value", api.TestedPaths);
        Assert.Equal(previous.Target.Runtime!.Bootstrap, api.TestedBootstrap);
        Assert.Equal(next.Target.Runtime!.Bootstrap, api.Current["spec"]!["template"]!["spec"]!["containers"]![1]!["env"]![1]!["value"]!.GetValue<string>());
        Assert.Equal("foreign", api.Current["spec"]!["template"]!["spec"]!["containers"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("retained", api.Current["spec"]!["template"]!["spec"]!["containers"]![1]!["env"]![0]!["value"]!.GetValue<string>());
    }
    [Fact]
    public async Task Changed_old_bootstrap_between_read_and_dispatch_still_fails_the_original_test_without_retry()
    {
        var (previous, next) = Intents();
        var api = new RawScalarPatchApi(previous, changeOldBootstrap: true);
        DeploymentIntents.Validate(next);
        var result = await Kernel(next, api).ExecuteAsync(next);
        Assert.Equal(KernelOutcome.Conflict, result.Outcome);
        Assert.Null(result.Receipt);
        Assert.Equal(1, result.WriteDispatches);
        Assert.Equal(1, api.Writes);
        Assert.Equal("/spec/template/spec/containers/1/env/1/value", api.FailedPath);
        Assert.Equal(previous.Target.Image, api.Current["spec"]!["template"]!["spec"]!["containers"]![1]!["image"]!.GetValue<string>());
    }
    private static (DeploymentIntent Previous, DeploymentIntent Next) Intents()
    {
        var previous = ConfiguredDeploymentTests.Intent();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=transport-fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var bootstrap = RuntimeBootstrapCodec.Decode(previous.Target.Runtime!.Bootstrap);
        bootstrap = bootstrap with { Delivery = bootstrap.Delivery with { Tls = new("custom-ca", certificate.ExportCertificatePem()) } };
        previous = previous with { Target = previous.Target with { Runtime = previous.Target.Runtime with { Bootstrap = RuntimeBootstrapCodec.Encode(bootstrap) } } };
        var next = previous with
        {
            ExpectedUid = "saved-uid",
            LastConfirmed = previous.Target,
            TargetGeneration = 2,
            Operation = Guid.NewGuid(),
            Target = previous.Target with
            {
                Image = "registry.example/app:v2",
                Runtime = previous.Target.Runtime with
                { Bootstrap = RuntimeBootstrapCodec.Encode(bootstrap with { Documents = bootstrap.Documents.ToImmutableSortedDictionary(x => x.Key, _ => Guid.CreateVersion7().ToString("D"), StringComparer.Ordinal) }) }
            }
        };
        return (previous, next);
    }
    private static DeploymentKernel Kernel(DeploymentIntent intent, RawScalarPatchApi api) => new(intent.Binding,
        _ => ValueTask.FromResult(new KernelClient(new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api))));

    // Minimal server model for these test/add/replace operations. The scalar oracle
    // follows the audited Go JSON representation for canonical ASCII bootstrap text;
    // it never calls the production PATCH serializer. The SDK still sends the request.
    private sealed class RawScalarPatchApi : DelegatingHandler
    {
        private readonly DeploymentIntent _previous;
        private readonly bool _changeOldBootstrap;
        internal JsonObject Current { get; }
        internal int Writes;
        internal string? FailedPath;
        internal string? TestedBootstrap;
        internal List<string> TestedPaths { get; } = [];
        internal RawScalarPatchApi(DeploymentIntent previous, bool changeOldBootstrap)
        {
            _previous = previous; _changeOldBootstrap = changeOldBootstrap;
            Current = KernelTests.Object(previous);
            var containers = Current["spec"]!["template"]!["spec"]!["containers"]!.AsArray();
            containers.Insert(0, new JsonObject { ["name"] = "foreign", ["image"] = "foreign:v1" });
            containers[1]!["env"]!.AsArray().Insert(0, new JsonObject { ["name"] = "FOREIGN", ["value"] = "retained" });
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            HttpResponseMessage Reply(int status, JsonObject? body = null)
            {
                var response = KernelTests.Reply(status, body); response.RequestMessage = request; return response;
            }
            if (request.Method == HttpMethod.Get)
            {
                if (!request.RequestUri!.AbsolutePath.Contains("/serviceaccounts/", StringComparison.Ordinal)) return Reply(200, Current);
                var runtime = _previous.Target.Runtime!;
                return Reply(200, new JsonObject
                {
                    ["metadata"] = new JsonObject
                    {
                        ["name"] = runtime.ServiceAccount,
                        ["namespace"] = _previous.Namespace,
                        ["uid"] = runtime.ServiceAccountUid,
                        ["labels"] = new JsonObject { [DeploymentIntents.OwnerKey] = _previous.Owner.ToString("N"), [DeploymentIntents.ManagedKey] = _previous.ManagedWorkload.ToString("N") }
                    },
                    ["automountServiceAccountToken"] = false
                });
            }
            Assert.Equal(HttpMethod.Patch, request.Method); Writes++;
            if (_changeOldBootstrap) Current["spec"]!["template"]!["spec"]!["containers"]![1]!["env"]![1]!["value"] = _previous.Target.Runtime!.Bootstrap + " ";
            using var wire = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            foreach (var operation in wire.RootElement.EnumerateArray())
            {
                var path = operation.GetProperty("path"u8).GetString()!;
                var parts = path.Split('/').Skip(1).Select(x => x.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
                JsonNode parent = Current;
                foreach (var part in parts[..^1]) parent = (parent is JsonArray array ? array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)] : parent[part])!;
                var name = parts[^1];
                if (operation.GetProperty("op"u8).GetString() == "test")
                {
                    TestedPaths.Add(path);
                    var value = parent[name]!.GetValue<string>();
                    if (path.EndsWith("/env/1/value", StringComparison.Ordinal)) TestedBootstrap = operation.GetProperty("value"u8).GetString();
                    var serverToken = JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                        .Replace("<", "\\u003c", StringComparison.Ordinal).Replace(">", "\\u003e", StringComparison.Ordinal).Replace("&", "\\u0026", StringComparison.Ordinal);
                    if (serverToken != operation.GetProperty("value"u8).GetRawText())
                    { FailedPath = path; return Reply(422); }
                }
                else
                {
                    Assert.Contains(operation.GetProperty("op"u8).GetString(), new[] { "add", "replace" });
                    parent[name] = JsonNode.Parse(operation.GetProperty("value"u8).GetRawText());
                }
            }
            Current["metadata"]!["generation"] = 2;
            return Reply(200, Current);
        }
    }
}
