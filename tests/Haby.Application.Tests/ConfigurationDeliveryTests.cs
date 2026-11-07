using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ConfigurationDeliveryTests
{
    private static readonly ConfigurationRendererId Renderer = ConfigurationRendererId.Create("sample.delivery/v1");
    private static readonly ConfigurationFormatterId Formatter = ConfigurationFormatterId.Create("sample.delivery-string/v1");
    private static readonly ManifestLocalId Implementation = ManifestLocalId.Create("sample-delivery-v1");
    private static readonly ResourceTypeId Resource = ResourceTypeId.Create("sample.resource/v1");
    private static readonly DurableActionExecutionKey Action = new(new(Resource, ProviderKindId.Create("sample/v1"),
        ProviderConfigurationContractId.Create("sample.configuration/v1"), ProviderImplementationId.Create("sample/v1")), OperationContractId.Create("sample.create/v1"));
    private sealed record Definition() : ConfigurationRendererContract(Renderer, ConfigurationDeliveryTests.Resource, "sample.output/v1", "value", OperationId.Create("create"), Action, ConfigurationDeliveryTests.Formatter)
    {
        public override ConfigurationObject Defaults => new([]);
        public override bool ValidateOptions(ConfigurationObject options, bool complete) => options.Members.Count == 0;
        public override ConfigurationObject NormalizeOptions(ConfigurationObject options) => options;
    }
    private sealed record FormatDefinition() : ConfigurationFormatterContract(Formatter)
    {
        public override bool ValidateArguments(ConfigurationObject a) => a.Members.Count == 1 && a.Members.GetValueOrDefault("credential") is ConfigurationSecretReference;
    }
    private sealed class Adapter : IConfigurationFormatter, IConfigurationResolutionBinding
    {
        public ConfigurationFormatterId Contract => Formatter;
        public ConfigurationFormatterId Formatter => ConfigurationDeliveryTests.Formatter;
        public ManifestLocalId Identity => Implementation;
        public ManifestLocalId Implementation => ConfigurationDeliveryTests.Implementation;
        public SecretRef Reference = new(SecretId.New(), SecretVersionId.New());
        public int Calls;
        public string? Text;
        public bool Throw;
        public bool Cancel;
        public int? FailureOnCall;
        public SecretPurpose Purpose = SecretPurpose.ApplicationLogin;
        public bool ValidateMaterial(ReadOnlySpan<char> material) => !material.IsEmpty;
        public SecretResolutionReference? Bind(ConfigurationRenderPin pin, ExternalOperationOutput output, ConfigurationDeferredValue value) =>
            output.Payload == Reference.SecretId + ":" + Reference.SecretVersionId && value.Formatter == Formatter &&
            new FormatDefinition().ValidateArguments(value.Arguments) && value.Arguments.Members["credential"] is ConfigurationSecretReference r && r.Reference == Reference
                ? new(Reference, Purpose) : null;
        public ConfidentialConfigurationString? Format(ConfigurationObject arguments, ReadOnlySpan<char> material)
        {
            Calls++;
            if (Cancel) throw new OperationCanceledException();
            if (Throw || Calls == FailureOnCall) throw new InvalidOperationException("private-formatter-diagnostic");
            return new(Text ?? material.ToString());
        }
    }
    private sealed class Evidence(Adapter adapter) : IConfigurationOutputEvidence
    {
        public ValueTask<ExternalOperationOutput?> ReadAsync(DurableOperationKey producer, ManagedResourceKey owner, DurableActionExecutionKey action, string contract, string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ExternalOperationOutput?>(new(contract, key, adapter.Reference.SecretId + ":" + adapter.Reference.SecretVersionId));
    }
    private sealed class SyntheticRenderer(Adapter adapter) : IConfigurationRenderer
    {
        public ConfigurationRendererId Contract => Renderer;
        public ManifestLocalId Identity => Implementation;
        public ConfigurationFragment? Render(ExternalOperationOutput output, ConfigurationObject settings) =>
            new(new ConfigurationDeferredValue(Formatter, new(new Dictionary<string, ConfigurationValue> { ["credential"] = new ConfigurationSecretReference(adapter.Reference) })));
    }
    private sealed class Store : IConfigurationAccessStore, IConfigurationDeliveryDocumentStore, ISecretStore
    {
        public ConfigurationDeliveryArtifact Artifact = null!;
        public ConfigurationAccessGrant Grant = null!;
        public bool Allowed = true;
        public int Reads;
        public int Resolutions;
        public byte[] Bytes = Encoding.UTF8.GetBytes("synthetic-private-material");
        public SecretMaterial? LastMaterial;
        public bool Missing;
        public bool Throw;
        public bool Cancel;
        public CancellationTokenSource? CancelAfterRead;
        public ValueTask<ConfigurationAccessGrant?> AuthorizeAsync(string credential, ConfigurationDocumentRevisionId revision, CancellationToken token) => ValueTask.FromResult(Allowed ? Grant : null);
        public ValueTask<ConfigurationDeliveryArtifact?> ReadAsync(ConfigurationAccessGrant grant, CancellationToken token) { Reads++; return ValueTask.FromResult<ConfigurationDeliveryArtifact?>(Artifact); }
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes, CancellationToken cancellationToken)
        {
            Resolutions++; Assert.Equal(new SecretOwner.Resource(new(Artifact.Environment, Artifact.Revision.Metadata.Revision.ApplicationRecordId, ManifestLocalId.Create("resource"))), definition.Owner);
            Assert.Equal(SecretPurpose.ApplicationLogin, definition.Purpose); Assert.Equal(65536, maximumBytes);
            if (Cancel) throw new OperationCanceledException();
            if (Throw) throw new IOException("private-secret-diagnostic");
            LastMaterial = Missing ? null : new(Bytes);
            CancelAfterRead?.Cancel();
            return ValueTask.FromResult(LastMaterial);
        }
        public ValueTask<ConfigurationAccessCreated?> CreateAsync(ConfigurationDocumentSelection selection, string actor, DateTimeOffset? expiresAt, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ConfigurationAccessGrant?> GetAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ConfigurationAccessGrant?> RevokeAsync(Guid id, string actor, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken cancellationToken) => throw new InvalidOperationException("Unbounded resolution is forbidden.");
    }
    private sealed record Harness(Store Store, Adapter Adapter, ConfigurationRenderingCatalog Catalog)
    {
        public DeliverConfiguration Service(params IConfigurationResolutionBinding[] registrations) => new(Store, Store, Store, Catalog, registrations.Length == 0 ? [Adapter] : registrations);
        public ValueTask<ConfigurationDeliveryResult> Run() => Service().ExecuteAsync("opaque", Store.Grant.Document.Id, default);
    }
    private static async Task<Harness> Prepare(int projections = 1)
    {
        var manifest = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Encoding.UTF8.GetBytes("""
            {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},"spec":{
            "resources":{"resource":{"type":"sample.resource/v1","providerRef":"available","properties":{}}},
            "components":{"api":{"bindings":{"consume":{"target":{"kind":"local-resource","resourceRef":"resource"},"configurationSlot":"main"}}}}}}}
            """), new([new(Resource, "{}", "{}")], [], [], []))).Candidate.Declaration;
        var profileNode = JsonNode.Parse("""
            {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"layout","revision":1},"spec":{
            "documents":{"settings":{"fileName":"appsettings.json","format":"json","template":{}}},
            "bindingRules":{"connection":{"slot":"main","rendererRef":"sample.delivery/v1","documentProjections":[{"documentRef":"settings","path":"/value"}]}}}}
            """u8)!;
        if (projections == 2) profileNode["spec"]!["bindingRules"]!["connection"]!["documentProjections"]!.AsArray().Add(new JsonObject { ["documentRef"] = "settings", ["path"] = "/second" });
        var profile = Haby.Tests.ProfilePersistenceSamples.Parse(profileNode);
        var reference = ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1));
        var scopes = ConfigurationPlanningSnapshot.Targets(manifest).Select((t, i) => new ConfigurationPlanningScope(t, i == 0 ? AssignmentToken.New() : null, new(t, i == 0 ? [new ProfileDecision.Use(profile.Reference)] : [])));
        var adapter = new Adapter();
        var formatters = new ConfigurationFormatterCatalog([new FormatDefinition()], [new(Implementation, Formatter)]);
        var catalog = new ConfigurationRenderingCatalog([new Definition()], [new SyntheticRenderer(adapter)], formatters, [adapter]);
        var frozen = ConfigurationPlanningSnapshot.Prepare(reference, manifest, scopes, [profile], catalog).Snapshot!;
        var environment = EnvironmentRecordId.New();
        var target = new ConfigurationDocumentTarget(ApplicationDeploymentId.New(), DeploymentGeneration.Create(1), ManifestLocalId.Create("api"), ManifestLocalId.Create("settings"));
        var composed = (await new ComposeConfigurationDocument(catalog, formatters, new Evidence(adapter)).ExecuteAsync(frozen, target, environment, default))!;
        var associations = ConfigurationDocumentCodec.EncodeBounded(w =>
        {
            w.WriteStartArray(); foreach (var output in composed.Outputs) { w.WriteStartArray(); w.WriteStringValue(output.Pin.Producer.Value); w.WriteStringValue(Guid.CreateVersion7().ToString()); w.WriteStringValue(output.Output.Contract); w.WriteStringValue(output.Output.Key); w.WriteStringValue(output.Output.Payload); w.WriteEndArray(); }
            w.WriteEndArray();
        });
        var revision = new ConfigurationDocumentRevision(new(ConfigurationDocumentRevisionId.New(), target, EnvironmentId.Create("pilot"), manifest.ApplicationId, reference, DeploymentPlanId.New(),
            "appsettings.json", "json", ConfigurationJson.Profile, "safe-source", "frozen", "tester", DateTimeOffset.UnixEpoch), ConfigurationPlanningSnapshotCodec.Encode(frozen), associations, composed.Tree);
        var store = new Store { Artifact = new(revision, environment), Grant = new(Guid.CreateVersion7(), revision.Metadata, "tester", DateTimeOffset.UnixEpoch, null, null, null) };
        // Delivery has no renderer registration at all, proving that it cannot rerender.
        return new(store, adapter, new([new Definition()], [], formatters, [adapter]));
    }

    [Fact]
    public async Task Second_typed_family_authorizes_before_reads_and_deduplicates_only_within_each_request()
    {
        var h = await Prepare(2); h.Store.Allowed = false;
        using (var denied = await h.Run()) { Assert.Equal(ConfigurationDeliveryStatus.Denied, denied.Status); Assert.True(denied.Json.IsEmpty); }
        Assert.Equal(0, h.Store.Reads); Assert.Equal(0, h.Store.Resolutions); Assert.Equal(0, h.Adapter.Calls);
        h.Store.Allowed = true;
        for (var i = 0; i < 2; i++)
        {
            using var delivered = await h.Run(); Assert.Equal(ConfigurationDeliveryStatus.Delivered, delivered.Status);
            Assert.Equal(i + 1, h.Store.Resolutions); Assert.Equal(2 * (i + 1), h.Adapter.Calls);
            Assert.Throws<ObjectDisposedException>(() => h.Store.LastMaterial!.CopyBytes());
            Assert.False(delivered.ToString().Contains("synthetic-private-material", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("formatter")]
    [InlineData("extra-reference")]
    [InlineData("standalone")]
    [InlineData("projection")]
    [InlineData("output")]
    [InlineData("duplicate-output")]
    [InlineData("ambiguous-registration")]
    [InlineData("purpose")]
    [InlineData("implementation")]
    public async Task Untrusted_reference_arguments_output_projection_and_capability_refuse_before_material(string mode)
    {
        var h = await Prepare(); var r = h.Store.Artifact.Revision;
        if (mode == "purpose") h.Adapter.Purpose = SecretPurpose.ProviderAdministrator;
        var deferred = Assert.IsType<ConfigurationDeferredValue>(Assert.IsType<ConfigurationObject>(r.Tree).Members["value"]);
        ConfigurationValue replacement = mode switch
        {
            "formatter" => new ConfigurationDeferredValue(ConfigurationFormatterId.Create("other/v1"), deferred.Arguments),
            "standalone" => new ConfigurationSecretReference(h.Adapter.Reference),
            "extra-reference" => new ConfigurationDeferredValue(Formatter, new(deferred.Arguments.Members.Append(KeyValuePair.Create<string, ConfigurationValue>("another", new ConfigurationSecretReference(h.Adapter.Reference))))),
            "reference" => new ConfigurationDeferredValue(Formatter, new(new Dictionary<string, ConfigurationValue> { ["credential"] = new ConfigurationSecretReference(new(SecretId.New(), SecretVersionId.New())) })),
            _ => deferred
        };
        var tree = new ConfigurationObject(new Dictionary<string, ConfigurationValue> { [mode == "projection" ? "foreign" : "value"] = replacement });
        var associations = mode == "output" ? "[]" : mode == "duplicate-output" ? r.OutputAssociations[..^1] + "," + r.OutputAssociations[1..] : r.OutputAssociations;
        h.Store.Artifact = h.Store.Artifact with
        {
            Revision = r with
            {
                Tree = tree,
                OutputAssociations = associations,
                FrozenInputs = mode == "implementation" ? r.FrozenInputs.Replace("sample-delivery-v1", "foreign-v1", StringComparison.Ordinal) : r.FrozenInputs
            }
        };
        using var result = await (mode == "ambiguous-registration" ? h.Service(h.Adapter, h.Adapter).ExecuteAsync("opaque", r.Metadata.Id, default) : h.Run());
        Assert.Equal(ConfigurationDeliveryStatus.InputUnavailable, result.Status); Assert.True(result.Json.IsEmpty);
        Assert.Equal(0, h.Store.Resolutions); Assert.Equal(0, h.Adapter.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unavailable")]
    [InlineData("utf8")]
    [InlineData("nul")]
    [InlineData("formatter")]
    public async Task Material_and_formatter_failures_publish_nothing_and_dispose_material(string mode)
    {
        var h = await Prepare();
        h.Store.Missing = mode == "missing"; h.Store.Throw = mode == "unavailable"; h.Adapter.Throw = mode == "formatter";
        if (mode == "utf8") h.Store.Bytes = [0xc3, 0x28];
        if (mode == "nul") h.Store.Bytes = [65, 0, 66];
        using var result = await h.Run(); Assert.Equal(ConfigurationDeliveryStatus.InputUnavailable, result.Status); Assert.True(result.Json.IsEmpty);
        if (h.Store.LastMaterial is not null) Assert.Throws<ObjectDisposedException>(() => h.Store.LastMaterial.CopyBytes());
    }

    [Theory]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    public async Task Secret_source_byte_boundary_is_exact(int length, bool allowed)
    {
        var h = await Prepare(); h.Store.Bytes = Enumerable.Repeat((byte)'a', length).ToArray();
        using var result = await h.Run(); Assert.Equal(allowed ? ConfigurationDeliveryStatus.Delivered : ConfigurationDeliveryStatus.LimitExceeded, result.Status);
        if (!allowed) Assert.True(result.Json.IsEmpty);
        Assert.Throws<ObjectDisposedException>(() => h.Store.LastMaterial!.CopyBytes());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Final_json_exact_four_MiB_limit_uses_independent_ASCII_wire_length(int over, bool allowed)
    {
        var h = await Prepare(); h.Adapter.Text = new string('x', 4 * 1024 * 1024 - "{\"value\":\"\"}"u8.Length + over);
        using var result = await h.Run(); Assert.Equal(allowed ? ConfigurationDeliveryStatus.Delivered : ConfigurationDeliveryStatus.LimitExceeded, result.Status);
        if (allowed) { Assert.Equal(4 * 1024 * 1024, result.Json.Length); Assert.Equal((byte)'{', result.Json.Span[0]); }
        else Assert.True(result.Json.IsEmpty);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Escaped_expansion_limit_is_counted_before_encoding_with_an_independent_escape_oracle(int over, bool allowed)
    {
        var h = await Prepare();
        var budget = 4 * 1024 * 1024 - "{\"value\":\"\"}"u8.Length;
        // Each '<' is six UTF-8 bytes (\u003C); ordinary ASCII padding fills the remainder.
        h.Adapter.Text = new string('<', budget / 6) + new string('a', budget % 6 + over);
        using var result = await h.Run(); Assert.Equal(allowed ? ConfigurationDeliveryStatus.Delivered : ConfigurationDeliveryStatus.LimitExceeded, result.Status);
        if (allowed) Assert.Equal(4 * 1024 * 1024, result.Json.Length); else Assert.True(result.Json.IsEmpty);
    }

    [Fact]
    public async Task A_later_formatter_failure_refuses_the_complete_response_and_disposes_deduplicated_material()
    {
        var h = await Prepare(2); h.Adapter.FailureOnCall = 2;
        using var refused = await h.Run(); Assert.Equal(ConfigurationDeliveryStatus.InputUnavailable, refused.Status); Assert.True(refused.Json.IsEmpty);
        Assert.Equal(1, h.Store.Resolutions); Assert.Equal(2, h.Adapter.Calls);
        Assert.Throws<ObjectDisposedException>(() => h.Store.LastMaterial!.CopyBytes());
    }

    [Fact]
    public async Task Cancellation_after_scoped_read_disposes_returned_material_before_formatting()
    {
        var h = await Prepare(); using var canceled = new CancellationTokenSource(); h.Store.CancelAfterRead = canceled;
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await h.Service().ExecuteAsync("opaque", h.Store.Grant.Document.Id, canceled.Token));
        Assert.Equal(0, h.Adapter.Calls); Assert.Throws<ObjectDisposedException>(() => h.Store.LastMaterial!.CopyBytes());
    }

    [Fact]
    public async Task Unicode_quoting_exact_numbers_and_ordinary_magic_JSON_are_preserved_without_interpretation()
    {
        var h = await Prepare(); h.Adapter.Text = "Ж🔑\n\"\\;=";
        var r = h.Store.Artifact.Revision;
        var tree = new ConfigurationObject(Assert.IsType<ConfigurationObject>(r.Tree).Members
            .Append(KeyValuePair.Create<string, ConfigurationValue>("number", ConfigurationNumber.Parse("123456789012345678901234567890.001e999")))
            .Append(KeyValuePair.Create<string, ConfigurationValue>("magic", new ConfigurationObject(new Dictionary<string, ConfigurationValue> { ["kind"] = new ConfigurationString("SecretReference"), ["secret"] = new ConfigurationString("ordinary") }))));
        h.Store.Artifact = h.Store.Artifact with { Revision = r with { Tree = tree } };
        using var result = await h.Run(); Assert.Equal(ConfigurationDeliveryStatus.Delivered, result.Status);
        using var json = JsonDocument.Parse(result.Json);
        Assert.True(json.RootElement.GetProperty("value"u8).GetString() == h.Adapter.Text);
        Assert.Equal("123456789012345678901234567890001e996", json.RootElement.GetProperty("number"u8).GetRawText());
        Assert.Equal("ordinary", json.RootElement.GetProperty("magic"u8).GetProperty("secret"u8).GetString());
        result.Dispose(); Assert.True(result.Json.Span.IndexOfAnyExcept((byte)0) < 0);
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Delivery_serializer_semantic_depth_boundary_is_independent_from_storage_wrappers(int depth, bool allowed)
    {
        ConfigurationValue tree = new ConfigurationString("x"); for (var i = 0; i < depth; i++) tree = new ConfigurationArray([tree]);
        if (allowed) { var bytes = ConfigurationJson.SerializeDelivery(tree); Assert.Equal(depth * 2 + 3, bytes.Length); }
        else Assert.Throws<ConfigurationDocumentLimitException>(() => ConfigurationJson.SerializeDelivery(tree));
    }

    [Fact]
    public async Task Cancellation_propagates_and_never_leaves_owned_material_undisposed()
    {
        var h = await Prepare(); h.Store.Cancel = true;
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await h.Run());
        h.Store.Cancel = false; h.Adapter.Cancel = true;
        // Pure formatter exceptions are deliberately bounded refusals, including adapter cancellation exceptions.
        using var refused = await h.Run(); Assert.Equal(ConfigurationDeliveryStatus.InputUnavailable, refused.Status);
        Assert.Throws<ObjectDisposedException>(() => h.Store.LastMaterial!.CopyBytes());
    }
}
