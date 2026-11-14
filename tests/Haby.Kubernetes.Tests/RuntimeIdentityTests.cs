using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Haby.Application;
using Haby.Kubernetes;
using Haby.Kubernetes.Contracts;
using k8s;
using Xunit;

namespace Haby.Kubernetes.Tests;

public sealed class RuntimeIdentityTests
{
    private static InternalActionContext Context() => InternalDeploymentPlacementTests.Contexts(false, configured: true)[0] with { InstallationId = Guid.NewGuid() };
    private static InternalRuntimeIdentityAction Handler(Api api) => new(null!, (_, _) => ValueTask.FromResult(new KernelClient(
        new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, api))));
    [Theory]
    [InlineData("inventory", "haby.kubernetes.runtime-account-receipt/v1")]
    [InlineData("binding", "haby.kubernetes.runtime-validation/v1")]
    public void Receipt_and_validation_envelopes_identify_their_actual_payload_contract(string envelope, string expected)
    {
        var context = Context(); var handler = new InternalRuntimeIdentityAction(null!); var payload = handler.Prepare(context);
        var receipt = RuntimeIdentityCodec.Encode(new RuntimeAccountReceipt(RuntimeIdentityCodec.Decode(payload), "confirmed-uid", false, ConfirmationSource.Response));
        var confirmation = handler.IdentityConfirmation(context, payload, receipt);
        Assert.Equal(expected, envelope == "inventory" ? confirmation.InventoryContract : confirmation.ValidationContract);
        if (envelope == "inventory")
        {
            var saved = RuntimeIdentityCodec.Receipt(confirmation.InventoryPayload);
            Assert.Equal("confirmed-uid", saved.Uid); Assert.Equal(payload, RuntimeIdentityCodec.Encode(saved.Intent));
            Assert.Null(JsonNode.Parse(confirmation.InventoryPayload)!["mode"]);
        }
        else
        {
            var saved = System.Text.Json.JsonSerializer.Deserialize(confirmation.ValidationPayload, RuntimeIdentityJson.Default.RuntimeValidationContext)!;
            Assert.Equal("confirmed-uid", saved.ServiceAccountUid); Assert.Equal("service-account-bound", saved.Mode);
            Assert.Equal(context.Provider.Instance.ToString(), saved.Binding.Provider);
            Assert.Null(JsonNode.Parse(confirmation.ValidationPayload)!["intent"]);
        }
    }
    [Fact]
    public async Task Confirmed_identity_has_disabled_automount_exact_audience_and_no_incidental_privileges()
    {
        var context = Context(); var api = new Api(); var handler = Handler(api); var payload = handler.Prepare(context);
        var result = await handler.ExecuteAsync(context, payload, default);
        Assert.Equal(InternalActionOutcome.Confirmed, result.Outcome);
        Assert.True(handler.ValidateReceipt(context, payload, result.Receipt!, false));
        var receipt = RuntimeIdentityCodec.Receipt(result.Receipt!);
        Assert.False(receipt.Automount); Assert.Equal("fixture-uid", receipt.Uid);
        var projection = handler.IdentityConfirmation(context, payload, result.Receipt!);
        Assert.Equal($"urn:haby:{context.InstallationId:D}:{Guid.Parse(context.Provider.Instance.ToString()):D}", projection.Audience);
        Assert.Contains("service-account-bound", projection.ValidationPayload);
        Assert.Equal(handler.Contract.OutputContract, projection.InventoryContract);
        Assert.Equal(RuntimeIdentityCodec.ValidationContract, projection.ValidationContract);
        Assert.Single(api.Writes); Assert.EndsWith("/serviceaccounts", api.Writes[0]);
        Assert.False(api.Account!["automountServiceAccountToken"]!.GetValue<bool>());
        Assert.Null(api.Account["secrets"]); Assert.Null(api.Account["imagePullSecrets"]);
        Assert.Equal(new[] { "get:namespaces", "get:serviceaccounts", "create:serviceaccounts" }, api.Reviews);
    }
    [Theory]
    [InlineData("permission")]
    [InlineData("namespace")]
    [InlineData("occupied")]
    [InlineData("authentication")]
    [InlineData("cancellation")]
    public async Task Known_preconditions_refuse_before_account_create(string fault)
    {
        var context = Context(); var api = new Api { Fault = fault }; var handler = Handler(api); var payload = handler.Prepare(context);
        using var cancel = new CancellationTokenSource(); if (fault == "cancellation") cancel.Cancel();
        var result = await handler.ExecuteAsync(context, payload, cancel.Token);
        Assert.NotEqual(InternalActionOutcome.Confirmed, result.Outcome); Assert.Null(result.Receipt); Assert.Empty(api.Writes);
    }
    [Theory]
    [InlineData("owner")]
    [InlineData("marker")]
    [InlineData("automount")]
    [InlineData("missing")]
    [InlineData("unavailable")]
    public async Task Observation_requires_exact_owned_projection_and_never_writes(string fault)
    {
        var context = Context(); var api = new Api(); var handler = Handler(api); var payload = handler.Prepare(context);
        Assert.Equal(InternalActionOutcome.Confirmed, (await handler.ExecuteAsync(context, payload, default)).Outcome);
        api.Writes.Clear(); api.Fault = fault;
        var result = await handler.ObserveAsync(context, payload, default);
        Assert.NotEqual(InternalActionOutcome.Confirmed, result.Outcome); Assert.Null(result.Receipt); Assert.Empty(api.Writes);
    }
    [Fact]
    public async Task Lost_create_response_is_observed_with_original_markers_and_one_write()
    {
        var context = Context(); var api = new Api { Fault = "lost" }; var handler = Handler(api); var payload = handler.Prepare(context);
        Assert.Equal(InternalActionOutcome.Uncertain, (await handler.ExecuteAsync(context, payload, default)).Outcome);
        api.Fault = "";
        var result = await handler.ObserveAsync(context, payload, default);
        Assert.Equal(InternalActionOutcome.Confirmed, result.Outcome); Assert.Single(api.Writes);
        Assert.True(handler.ValidateReceipt(context, payload, result.Receipt!, true));
        Assert.Equal(payload, RuntimeIdentityCodec.Encode(RuntimeIdentityCodec.Receipt(result.Receipt!).Intent));
    }
    [Fact]
    public void No_configuration_and_changed_trust_refuse_without_network()
    {
        var api = new Api(); var handler = Handler(api); var context = Context(); var payload = handler.Prepare(context);
        Assert.Throws<WorkloadContractException>(() => handler.Prepare(InternalDeploymentPlacementTests.Contexts(false)[0]));
        Assert.False(handler.Validate(context with { InstallationId = Guid.NewGuid() }, payload));
        Assert.False(handler.Validate(context with { ManagedIdentity = Guid.NewGuid() }, payload));
        Assert.Throws<WorkloadContractException>(() => RuntimeIdentityCodec.Decode("{\"format\":1," + payload[1..]));
        Assert.Empty(api.Writes);
    }
    private sealed class Api : DelegatingHandler
    {
        internal string Fault = ""; internal JsonObject? Account; internal List<string> Writes = []; internal List<string> Reviews = [];
        // Each restarted adapter owns a new client; this fixture models one independent API server.
        protected override void Dispose(bool disposing) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var input = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            JsonNode? body = null; var status = 200;
            if (path.EndsWith("selfsubjectreviews", StringComparison.Ordinal))
                body = JsonNode.Parse(Fault == "authentication" ? """{"status":{"userInfo":{"username":"system:anonymous"}}}""" : """{"status":{"userInfo":{"username":"fixture"}}}""");
            else if (path.EndsWith("selfsubjectaccessreviews", StringComparison.Ordinal))
            {
                var attributes = input!["spec"]!["resourceAttributes"]!;
                Reviews.Add(attributes["verb"]!.GetValue<string>() + ":" + attributes["resource"]!.GetValue<string>());
                body = new JsonObject { ["status"] = new JsonObject { ["allowed"] = Fault != "permission" } };
            }
            else if (path.StartsWith("/api/v1/namespaces/", StringComparison.Ordinal) && !path.Contains("/serviceaccounts", StringComparison.Ordinal))
            {
                if (Fault == "namespace") status = 404;
                else body = new JsonObject { ["metadata"] = new JsonObject { ["name"] = path.Split('/').Last() }, ["status"] = new JsonObject { ["phase"] = "Active" } };
            }
            else if (request.Method == HttpMethod.Post)
            {
                Writes.Add(path); Account = input!.AsObject(); Account["metadata"]!["uid"] = "fixture-uid";
                if (Fault == "lost") throw new HttpRequestException("private credential detail");
                body = Account; status = 201;
            }
            else
            {
                if (Fault == "unavailable") throw new HttpRequestException("private credential detail");
                if (Fault == "occupied" && Account is null) Account = new JsonObject();
                body = Account?.DeepClone();
                if (body is null || Fault == "missing") status = 404;
                else if (Fault == "owner") body["metadata"]!["labels"]![DeploymentIntents.OwnerKey] = Guid.NewGuid().ToString("N");
                else if (Fault == "marker") body["metadata"]!["annotations"]![DeploymentIntents.OperationKey] = Guid.NewGuid().ToString("N");
                else if (Fault == "automount") body["automountServiceAccountToken"] = true;
            }
            var response = new HttpResponseMessage((HttpStatusCode)status)
            { RequestMessage = request, Content = new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json") };
            return response;
        }
    }
}
