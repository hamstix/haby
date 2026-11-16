using System.Text;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ConfigurationSecretProjectionTests
{
    private sealed class Ports : IConfigurationAccessStore, IConfigurationDeliveryDocumentStore, ISecretStore
    {
        internal ConfigurationDeliveryArtifact Artifact = null!;
        internal ConfigurationAccessGrant Grant = null!;
        internal bool Allowed = true;
        internal int Reads;
        internal byte[] Material = Encoding.UTF8.GetBytes("{\"number\":1e999,\"enabled\":true}");
        public ValueTask<ConfigurationAccessGrant?> AuthorizeAsync(string credential, ConfigurationDocumentRevisionId revision, CancellationToken token) =>
            ValueTask.FromResult<ConfigurationAccessGrant?>(Allowed && revision == Grant.Document.Id ? Grant : null);
        public ValueTask<ConfigurationDeliveryArtifact?> ReadAsync(ConfigurationAccessGrant grant, CancellationToken token) =>
            ValueTask.FromResult<ConfigurationDeliveryArtifact?>(Artifact);
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes, CancellationToken token)
        {
            Reads++;
            Assert.Equal(SecretPurpose.Configuration, definition.Purpose);
            Assert.Equal(new SecretOwner.Environment(Artifact.Environment), definition.Owner);
            Assert.Equal(65536, maximumBytes);
            return ValueTask.FromResult<SecretMaterial?>(new(Material));
        }
        public ValueTask<ConfigurationAccessCreated?> CreateAsync(ConfigurationDocumentSelection selection, string actor, DateTimeOffset? expiresAt, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ConfigurationAccessGrant?> GetAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ConfigurationAccessGrant?> RevokeAsync(Guid id, string actor, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken token) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Frozen_array_projection_previews_whole_node_and_delivers_only_after_exact_authorization()
    {
        var manifestText = """
            {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},"spec":{
            "components":{"api":{}}}}}
            """;
        var manifest = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(
            Encoding.UTF8.GetBytes(manifestText), new([], [], [], []))).Candidate.Declaration;
        var profileText = """
            {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"logging","revision":1},"spec":{
              "documents":{"settings":{"fileName":"settings.json","format":"json","template":{}}},
              "staticContributions":{"logging":{"documentRef":"settings","path":"/Serilog","value":{"WriteTo":[{"Args":{}}]},
                "secretProjections":[{"path":"/WriteTo/0/Args/password","format":"json","secretRef":{"name":"logging-password","version":1}},
                  {"path":"/Literal","format":"string","secretRef":{"name":"logging-password","version":1}}]}}}}
            """;
        var profile = ProfilePayloadParser.ParseProfile(Encoding.UTF8.GetBytes(profileText)).Declaration!;
        Assert.NotNull(profile);
        var revision = ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1));
        var scopes = ConfigurationPlanningSnapshot.Targets(manifest).Select((target, index) => new ConfigurationPlanningScope(target,
            index == 0 ? AssignmentToken.New() : null, new(target,
                index == 0 ? [new ProfileDecision.Use(profile.Reference)] : []))).ToArray();
        var exact = new SecretRef(SecretId.New(), SecretVersionId.New());
        var named = Assert.Single(profile.Contributions).SecretProjections[0].SecretRef;
        var frozen = ConfigurationPlanningSnapshot.PrepareCore(revision, manifest, scopes, [profile],
            frozenSecrets: new Dictionary<ProfileNamedSecretRef, SecretRef> { [named] = exact }).Snapshot!;
        Assert.NotNull(frozen);
        var encoded = ConfigurationPlanningSnapshotCodec.Encode(frozen);
        var restarted = ConfigurationPlanningSnapshotCodec.Decode(encoded, revision);
        Assert.Equal(exact, restarted.FrozenSecrets[named]);
        var target = new ConfigurationDocumentTarget(ApplicationDeploymentId.New(), DeploymentGeneration.Create(1),
            ManifestLocalId.Create("api"), ManifestLocalId.Create("settings"));
        var composer = new ComposeConfigurationDocument(ConfigurationRenderingCatalog.Empty, new([], []), new NoEvidence());
        var composed = await composer.ExecuteAsync(restarted, target, EnvironmentRecordId.New(), default);
        Assert.NotNull(composed);
        var preview = ConfigurationJson.Preview(composed.Tree);
        Assert.Equal(["/Serilog/Literal", "/Serilog/WriteTo/0/Args/password"], preview.RedactedPaths);
        Assert.DoesNotContain("1e999", preview.Json);
        var metadata = new ConfigurationDocumentMetadata(ConfigurationDocumentRevisionId.New(), target,
            EnvironmentId.Create("pilot"), manifest.ApplicationId, revision, DeploymentPlanId.New(), "settings.json", "json",
            ConfigurationJson.Profile, "fingerprint", "inputs", "actor", DateTimeOffset.UtcNow);
        var ports = new Ports { Artifact = new(new(metadata, encoded, "[]", composed.Tree), EnvironmentRecordId.New()) };
        ports.Grant = new(Guid.NewGuid(), metadata, "actor", DateTimeOffset.UtcNow, null, null, null);
        var delivery = new DeliverConfiguration(ports, ports, ports, ConfigurationRenderingCatalog.Empty, []);
        ports.Allowed = false;
        using (var denied = await delivery.ExecuteAsync("credential", metadata.Id, default)) Assert.Equal(ConfigurationDeliveryStatus.Denied, denied.Status);
        Assert.Equal(0, ports.Reads);
        ports.Allowed = true;
        using var accepted = await delivery.ExecuteAsync("credential", metadata.Id, default);
        Assert.Equal(ConfigurationDeliveryStatus.Delivered, accepted.Status);
        Assert.Equal(1, ports.Reads);
        using var json = JsonDocument.Parse(accepted.Json);
        var node = json.RootElement.GetProperty("Serilog"u8).GetProperty("WriteTo"u8)[0].GetProperty("Args"u8).GetProperty("password"u8);
        Assert.Equal("1e999", node.GetProperty("number"u8).GetRawText());
        Assert.True(node.GetProperty("enabled"u8).GetBoolean());
        Assert.Equal(Encoding.UTF8.GetString(ports.Material), json.RootElement.GetProperty("Serilog"u8).GetProperty("Literal"u8).GetString());
        foreach (var invalid in new[]
        {
            new byte[] { 0xff, 0xfe }, Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}"),
            Encoding.UTF8.GetBytes("/* comment */ true"), Encoding.UTF8.GetBytes("true false"),
            Encoding.UTF8.GetBytes("{\"a\":1,}")
        })
        {
            ports.Material = invalid;
            using var failed = await delivery.ExecuteAsync("credential", metadata.Id, default);
            Assert.Equal(ConfigurationDeliveryStatus.InputUnavailable, failed.Status);
            Assert.True(failed.Json.IsEmpty);
            Assert.DoesNotContain("private", failed.ToString());
        }
        ports.Material = new byte[65_537];
        using var oversized = await delivery.ExecuteAsync("credential", metadata.Id, default);
        Assert.Equal(ConfigurationDeliveryStatus.LimitExceeded, oversized.Status);
        Assert.True(oversized.Json.IsEmpty);
    }

    private sealed class NoEvidence : IConfigurationOutputEvidence
    {
        public ValueTask<ExternalOperationOutput?> ReadAsync(DurableOperationKey producer, ManagedResourceKey owner,
            DurableActionExecutionKey action, string contract, string key, CancellationToken token) => throw new Xunit.Sdk.XunitException("No binding output expected.");
    }
    [Fact]
    public void Secret_projection_can_traverse_an_existing_array_to_insert_a_missing_object_leaf()
    {
        var value = new ConfigurationObject(new Dictionary<string, ConfigurationValue>
        {
            ["WriteTo"] = new ConfigurationArray([new ConfigurationObject(new Dictionary<string, ConfigurationValue>
            {
                ["Args"] = new ConfigurationObject([])
            })])
        });
        var contribution = new ProfileStaticContribution(ManifestLocalId.Create("logging"), ManifestLocalId.Create("settings"),
            "/Serilog", value, [new ProfileSecretProjection("/WriteTo/0/Args/lokiPassword", ProfileSecretFormat.String,
                new ProfileNamedSecretRef("logging-password", 1))]);

        Assert.Single(contribution.SecretProjections);
    }

    [Fact]
    public void Secret_projection_creates_missing_object_parents_and_rejects_overlapping_targets()
    {
        var named = new ProfileNamedSecretRef("telemetry-headers", 1);
        var projection = new ProfileSecretProjection("/Otlp/Headers", ProfileSecretFormat.String, named);
        var contribution = new ProfileStaticContribution(ManifestLocalId.Create("telemetry"),
            ManifestLocalId.Create("settings"), "/OpenTelemetry", new ConfigurationObject([]), [projection]);
        var value = ProfileSecretInsertion.Apply(contribution.Value, contribution.SecretProjections,
            new Dictionary<ProfileNamedSecretRef, SecretRef> { [named] = new(SecretId.New(), SecretVersionId.New()) });
        Assert.IsType<ConfigurationProjectedSecret>(Assert.IsType<ConfigurationObject>(
            Assert.IsType<ConfigurationObject>(value).Members["Otlp"]).Members["Headers"]);
        Assert.Throws<ArgumentException>(() => new ProfileStaticContribution(ManifestLocalId.Create("telemetry"),
            ManifestLocalId.Create("settings"), "/OpenTelemetry", new ConfigurationObject([]),
            [projection, new ProfileSecretProjection("/Otlp", ProfileSecretFormat.Json, named)]));
    }

    [Theory]
    [InlineData("/WriteTo/00/Args/password")]
    [InlineData("/WriteTo/1/Args/password")]
    [InlineData("/WriteTo/-/Args/password")]
    [InlineData("/WriteTo/0")]
    [InlineData("/WriteTo/0/Args")]
    public void Array_projection_rejects_noncanonical_out_of_range_and_occupied_paths(string path)
    {
        var value = new ConfigurationObject(new Dictionary<string, ConfigurationValue>
        {
            ["WriteTo"] = new ConfigurationArray([new ConfigurationObject(new Dictionary<string, ConfigurationValue>
            {
                ["Args"] = new ConfigurationObject([])
            })])
        });
        Assert.Throws<ArgumentException>(() => new ProfileStaticContribution(ManifestLocalId.Create("logging"),
            ManifestLocalId.Create("settings"), "/Serilog", value,
            [new ProfileSecretProjection(path, ProfileSecretFormat.String, new("logging-password", 1))]));
    }
}
