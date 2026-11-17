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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Frozen_array_projection_previews_whole_node_and_delivers_only_after_exact_authorization(bool firstJson)
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
                  {"path":"/Literal","format":"string","secretRef":{"name":"logging-password","version":1}},
                  {"path":"/Again","format":"json","secretRef":{"name":"logging-password","version":1}}]}}}}
            """;
        if (!firstJson)
        {
            var at = profileText.IndexOf("\"secretProjections\"", StringComparison.Ordinal);
            profileText = profileText[..at] + profileText[at..].Replace("\"format\":\"json\"", "\"format\":\"swap\"", StringComparison.Ordinal)
                .Replace("\"format\":\"string\"", "\"format\":\"json\"", StringComparison.Ordinal)
                .Replace("\"format\":\"swap\"", "\"format\":\"string\"", StringComparison.Ordinal);
        }
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
        Assert.Equal(["/Serilog/Again", "/Serilog/Literal", "/Serilog/WriteTo/0/Args/password"], preview.RedactedPaths);
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
        var middle = json.RootElement.GetProperty("Serilog"u8).GetProperty("Literal"u8);
        var again = json.RootElement.GetProperty("Serilog"u8).GetProperty("Again"u8);
        foreach (var value in new[] { node, middle, again }.Where((_, index) => (index != 1) == firstJson))
        {
            Assert.Equal("1e999", value.GetProperty("number"u8).GetRawText());
            Assert.True(value.GetProperty("enabled"u8).GetBoolean());
        }
        foreach (var value in new[] { node, middle, again }.Where((_, index) => (index != 1) != firstJson))
            Assert.Equal(Encoding.UTF8.GetString(ports.Material), value.GetString());
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
        foreach (var root in new[] { "null", "true", "123", "\"text\"", "[1,2]", "{}" })
        {
            ports.Material = Encoding.UTF8.GetBytes(root);
            using var deliveredRoot = await delivery.ExecuteAsync("credential", metadata.Id, default);
            Assert.Equal(ConfigurationDeliveryStatus.Delivered, deliveredRoot.Status);
            using var parsed = JsonDocument.Parse(deliveredRoot.Json);
            var serilog = parsed.RootElement.GetProperty("Serilog"u8);
            var jsonNode = firstJson ? serilog.GetProperty("WriteTo"u8)[0].GetProperty("Args"u8).GetProperty("password"u8)
                : serilog.GetProperty("Literal"u8);
            Assert.Equal(root switch { "123" => "123e0", "[1,2]" => "[1e0,2e0]", _ => root }, jsonNode.GetRawText());
        }
        ports.Material = Encoding.UTF8.GetBytes("\"" + new string('x', 65_534) + "\"");
        using (var exactMaximum = await delivery.ExecuteAsync("credential", metadata.Id, default))
            Assert.Equal(ConfigurationDeliveryStatus.Delivered, exactMaximum.Status);
        if (firstJson)
        {
            foreach (var (levels, status) in new[] { (59, ConfigurationDeliveryStatus.Delivered),
                (60, ConfigurationDeliveryStatus.LimitExceeded) })
            {
                ports.Material = Encoding.UTF8.GetBytes(new string('[', levels) + "0" + new string(']', levels));
                using var nested = await delivery.ExecuteAsync("credential", metadata.Id, default);
                Assert.Equal(status, nested.Status);
                if (status != ConfigurationDeliveryStatus.Delivered) Assert.True(nested.Json.IsEmpty);
            }
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

    [Fact]
    public void Secret_insertion_refuses_excessive_combined_depth_before_constructing_missing_parents()
    {
        var named = new ProfileNamedSecretRef("logging-password", 1);
        var deep = "/" + string.Join('/', Enumerable.Repeat("a", 70));
        var contribution = new ProfileStaticContribution(ManifestLocalId.Create("logging"),
            ManifestLocalId.Create("settings"), "/Serilog", new ConfigurationObject([]),
            [new ProfileSecretProjection(deep, ProfileSecretFormat.String, named)]);
        Assert.Throws<ConfigurationDocumentLimitException>(() => ProfileSecretInsertion.Apply(
            contribution.Value, contribution.SecretProjections,
            new Dictionary<ProfileNamedSecretRef, SecretRef> { [named] = new(SecretId.New(), SecretVersionId.New()) },
            contribution.Path));
        var huge = new ProfileSecretProjection("/" + string.Join('/', Enumerable.Repeat("a", 10_000)),
            ProfileSecretFormat.String, named);
        Assert.Throws<ConfigurationDocumentLimitException>(() => ProfileSecretInsertion.Apply(
            new ConfigurationObject([]), [huge],
            new Dictionary<ProfileNamedSecretRef, SecretRef> { [named] = new(SecretId.New(), SecretVersionId.New()) },
            contribution.Path));
        var exact = new Dictionary<ProfileNamedSecretRef, SecretRef> { [named] = new(SecretId.New(), SecretVersionId.New()) };
        var accepted = new ProfileSecretProjection("/" + string.Join('/', Enumerable.Repeat("a", 63)),
            ProfileSecretFormat.String, named);
        Assert.NotNull(ProfileSecretInsertion.Apply(new ConfigurationObject([]), [accepted], exact, contribution.Path));
        var rejected = new ProfileSecretProjection("/" + string.Join('/', Enumerable.Repeat("a", 64)),
            ProfileSecretFormat.String, named);
        Assert.Throws<ConfigurationDocumentLimitException>(() => ProfileSecretInsertion.Apply(
            new ConfigurationObject([]), [rejected], exact, contribution.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Projected_material_stops_reading_when_shared_escaped_budget_is_exhausted(bool distinct)
    {
        const int count = 70;
        var projections = string.Join(',', Enumerable.Range(0, count).Select(i =>
            $"{{\"path\":\"/Value{i}\",\"format\":\"string\",\"secretRef\":{{\"name\":\"secret-{(distinct ? i : 0)}\",\"version\":1}}}}"));
        var profileText = """
            {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"logging","revision":1},"spec":{
              "documents":{"settings":{"fileName":"settings.json","format":"json","template":{}}},
              "staticContributions":{"logging":{"documentRef":"settings","path":"/Serilog","value":{},
                "secretProjections":[]}}}}
            """.Replace("\"secretProjections\":[]", "\"secretProjections\":[" + projections + "]", StringComparison.Ordinal);
        var profile = ProfilePayloadParser.ParseProfile(Encoding.UTF8.GetBytes(profileText)).Declaration!;
        Assert.NotNull(profile);
        var manifest = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(
            """{"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},"spec":{"components":{"api":{}}}}}"""u8.ToArray(),
            new([], [], [], []))).Candidate.Declaration;
        var revision = ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1));
        var scopes = ConfigurationPlanningSnapshot.Targets(manifest).Select((target, i) => new ConfigurationPlanningScope(target,
            i == 0 ? AssignmentToken.New() : null, new(target,
                i == 0 ? [new ProfileDecision.Use(profile.Reference)] : []))).ToArray();
        var exact = Enumerable.Range(0, distinct ? count : 1).ToDictionary(i => new ProfileNamedSecretRef($"secret-{i}", 1),
            _ => new SecretRef(SecretId.New(), SecretVersionId.New()));
        var frozen = ConfigurationPlanningSnapshot.PrepareCore(revision, manifest, scopes, [profile], frozenSecrets: exact).Snapshot!;
        Assert.NotNull(frozen);
        var target = new ConfigurationDocumentTarget(ApplicationDeploymentId.New(), DeploymentGeneration.Create(1),
            ManifestLocalId.Create("api"), ManifestLocalId.Create("settings"));
        var composed = (await new ComposeConfigurationDocument(ConfigurationRenderingCatalog.Empty, new([], []), new NoEvidence())
            .ExecuteAsync(frozen, target, EnvironmentRecordId.New(), default))!;
        Assert.NotNull(composed);
        var metadata = new ConfigurationDocumentMetadata(ConfigurationDocumentRevisionId.New(), target,
            EnvironmentId.Create("pilot"), manifest.ApplicationId, revision, DeploymentPlanId.New(), "settings.json", "json",
            ConfigurationJson.Profile, "fingerprint", "inputs", "actor", DateTimeOffset.UtcNow);
        var ports = new Ports
        {
            Artifact = new(new(metadata, ConfigurationPlanningSnapshotCodec.Encode(frozen), "[]", composed.Tree), EnvironmentRecordId.New()),
            Material = Encoding.UTF8.GetBytes(new string(distinct ? '"' : 'x', 65_536))
        };
        ports.Grant = new(Guid.NewGuid(), metadata, "actor", DateTimeOffset.UtcNow, null, null, null);
        using var result = await new DeliverConfiguration(ports, ports, ports, ConfigurationRenderingCatalog.Empty, [])
            .ExecuteAsync("credential", metadata.Id, default);
        Assert.Equal(ConfigurationDeliveryStatus.LimitExceeded, result.Status);
        Assert.True(result.Json.IsEmpty);
        if (distinct) Assert.InRange(ports.Reads, 1, 40);
        else Assert.Equal(1, ports.Reads);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Projected_occurrences_and_ordinary_content_have_an_exact_escaped_four_MiB_boundary(int over, bool allowed)
    {
        const int occurrences = 10;
        const int sourceBytes = 65_536;
        var projectionNames = Enumerable.Range(0, occurrences).Select(i => $"Value{i}").ToArray();
        var emptyDocument = "{\"Padding\":\"\",\"Serilog\":{" +
            string.Join(',', projectionNames.Select(name => $"\"{name}\":\"\"")) + "}}";
        var padding = ConfigurationDocumentCodec.MaxBytes - Encoding.UTF8.GetByteCount(emptyDocument) -
            occurrences * sourceBytes * 6 + over; // '<' is encoded as six ASCII bytes: \\u003C.
        Assert.InRange(padding, 1, 1_000_000);
        var projections = string.Join(',', projectionNames.Select(name =>
            $"{{\"path\":\"/{name}\",\"format\":\"string\",\"secretRef\":{{\"name\":\"shared\",\"version\":1}}}}"));
        var profileText = """
            {"apiVersion":"haby.dev/v1alpha1","kind":"ConfigurationProfile","metadata":{"id":"logging","revision":1},"spec":{
              "documents":{"settings":{"fileName":"settings.json","format":"json","template":{}}},
              "staticContributions":{"logging":{"documentRef":"settings","path":"/Serilog","value":{},
                "secretProjections":[]}}}}
            """.Replace("\"template\":{}", "\"template\":{\"Padding\":\"" + new string('a', padding) + "\"}", StringComparison.Ordinal)
            .Replace("\"secretProjections\":[]", "\"secretProjections\":[" + projections + "]", StringComparison.Ordinal);
        var profile = ProfilePayloadParser.ParseProfile(Encoding.UTF8.GetBytes(profileText)).Declaration!;
        Assert.NotNull(profile);
        var manifest = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(
            """{"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},"spec":{"components":{"api":{}}}}}"""u8.ToArray(),
            new([], [], [], []))).Candidate.Declaration;
        var revision = ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1));
        var scopes = ConfigurationPlanningSnapshot.Targets(manifest).Select((target, i) => new ConfigurationPlanningScope(target,
            i == 0 ? AssignmentToken.New() : null, new(target,
                i == 0 ? [new ProfileDecision.Use(profile.Reference)] : []))).ToArray();
        var reference = new SecretRef(SecretId.New(), SecretVersionId.New());
        var frozen = ConfigurationPlanningSnapshot.PrepareCore(revision, manifest, scopes, [profile],
            frozenSecrets: new Dictionary<ProfileNamedSecretRef, SecretRef> { [new("shared", 1)] = reference }).Snapshot!;
        Assert.NotNull(frozen);
        var target = new ConfigurationDocumentTarget(ApplicationDeploymentId.New(), DeploymentGeneration.Create(1),
            ManifestLocalId.Create("api"), ManifestLocalId.Create("settings"));
        var composed = (await new ComposeConfigurationDocument(ConfigurationRenderingCatalog.Empty, new([], []), new NoEvidence())
            .ExecuteAsync(frozen, target, EnvironmentRecordId.New(), default))!;
        Assert.NotNull(composed);
        var metadata = new ConfigurationDocumentMetadata(ConfigurationDocumentRevisionId.New(), target,
            EnvironmentId.Create("pilot"), manifest.ApplicationId, revision, DeploymentPlanId.New(), "settings.json", "json",
            ConfigurationJson.Profile, "fingerprint", "inputs", "actor", DateTimeOffset.UtcNow);
        var ports = new Ports
        {
            Artifact = new(new(metadata, ConfigurationPlanningSnapshotCodec.Encode(frozen), "[]", composed.Tree), EnvironmentRecordId.New()),
            Material = Encoding.UTF8.GetBytes(new string('<', sourceBytes))
        };
        ports.Grant = new(Guid.NewGuid(), metadata, "actor", DateTimeOffset.UtcNow, null, null, null);
        using var result = await new DeliverConfiguration(ports, ports, ports, ConfigurationRenderingCatalog.Empty, [])
            .ExecuteAsync("credential", metadata.Id, default);
        Assert.Equal(allowed ? ConfigurationDeliveryStatus.Delivered : ConfigurationDeliveryStatus.LimitExceeded, result.Status);
        Assert.Equal(1, ports.Reads);
        if (allowed)
        {
            Assert.Equal(ConfigurationDocumentCodec.MaxBytes, result.Json.Length);
            using var json = JsonDocument.Parse(result.Json);
            Assert.Equal(new string('<', sourceBytes), json.RootElement.GetProperty("Serilog"u8).GetProperty("Value9"u8).GetString());
        }
        else Assert.True(result.Json.IsEmpty);
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
