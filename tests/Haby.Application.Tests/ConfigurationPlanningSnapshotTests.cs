using System.Text;
using System.Text.Json.Nodes;
using Haby.Domain;
using Haby.Manifest;
using Xunit;
using static Haby.Tests.ProfilePersistenceSamples;

namespace Haby.Application.Tests;

public sealed class ConfigurationPlanningSnapshotTests
{
    private static readonly ManifestRevisionRef Revision = ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(7));
    private static NormalizedApplicationManifest Manifest(string options = "{}", string slot = "main")
    {
        var json = """
            {"applicationVersion":"2","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},"spec":{
            "resources":{"resource":{"type":"sample.resource/v1","providerRef":"available","properties":{}}},
            "components":{"api":{"bindings":{"consume":{"target":{"kind":"local-resource","resourceRef":"resource"},"configurationSlot":"SLOT","options":OPTIONS}}},"worker":{}}}}}
            """.Replace("SLOT", slot, StringComparison.Ordinal).Replace("OPTIONS", options, StringComparison.Ordinal);
        return Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(Encoding.UTF8.GetBytes(json),
            new([new(ResourceTypeId.Create("sample.resource/v1"), "{}", "{}")], [], [], []))).Candidate.Declaration;
    }
    private static JsonObject Profile(bool secrets = false)
    {
        var node = Node(secrets);
        node["spec"]!["bindingRules"]!["connection"]!["documentProjections"]!.AsArray().RemoveAt(1);
        return node;
    }
    private static ConfigurationPlanningScope[] Scopes(NormalizedApplicationManifest manifest, AssignmentToken token, params ProfileDecision[] decisions) =>
        ConfigurationPlanningSnapshot.Targets(manifest).Select((t, i) => new ConfigurationPlanningScope(t, i == 0 ? token : null, new(t, i == 0 ? decisions : []))).ToArray();
    private static ConfigurationPlanningSnapshot Snapshot(NormalizedApplicationManifest manifest, ConfigurationProfileRevision profile, AssignmentToken token) =>
        ConfigurationPlanningSnapshot.Prepare(Revision, manifest, Scopes(manifest, token, new ProfileDecision.Use(profile.Reference)), [profile], Haby.Tests.ConfigurationRenderingSamples.Catalog(), Haby.Tests.ConfigurationRenderingSamples.Producers(manifest.Spec)).Snapshot!;

    [Fact]
    public void Graph_refuses_an_operation_owned_by_an_undeclared_resource()
    {
        var snapshot = Snapshot(Manifest(), Parse(Profile()), AssignmentToken.New());
        var inputs = Haby.Tests.DurableInputSamples.ForManifest(snapshot.Manifest, EnvironmentRecordId.New(), EnvironmentId.Create("pilot"),
            snapshot.Revision, ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1), "sample/v1") with
        { Configuration = snapshot };
        var node = new DurableOperationNode(OperationId.Create("create"), ManifestLocalId.Create("undeclared"),
            new(new(ResourceTypeId.Create("sample.resource/v1"), ProviderKindId.Create("sample/v1"),
                ProviderConfigurationContractId.Create("sample.configuration/v1"), ProviderImplementationId.Create("sample/v1")), OperationContractId.Create("sample.create/v1")),
            new(inputs.Environment, inputs.Providers.Resource(ManifestLocalId.Create("resource")).Instance, inputs.Providers.Resource(ManifestLocalId.Create("resource")).Configuration, inputs.Providers.Resource(ManifestLocalId.Create("resource")).Implementation.Value), new("sample/v1", "{}"), []);
        Assert.Throws<InvalidDataException>(() => DurableOperationGraphCodec.Fingerprint(inputs, new([node])));
    }

    internal static string Fingerprint(ConfigurationPlanningSnapshot snapshot)
    {
        var inputs = Haby.Tests.DurableInputSamples.ForManifest(snapshot.Manifest, EnvironmentRecordId.Parse("019a2b70-1234-7123-8123-123456789abc"), EnvironmentId.Create("pilot"),
            snapshot.Revision, ProviderInstanceId.Parse("019a2b70-1234-7123-8123-123456789abd"), ProviderConfigurationRevisionNumber.Create(1), "sample/v1") with
        { Configuration = snapshot };
        var node = new DurableOperationNode(OperationId.Create("create"), ManifestLocalId.Create("resource"),
            new(new(ResourceTypeId.Create("sample.resource/v1"), ProviderKindId.Create("sample/v1"),
                ProviderConfigurationContractId.Create("sample.configuration/v1"), ProviderImplementationId.Create("sample/v1")), OperationContractId.Create("sample.create/v1")),
            new(inputs.Environment, inputs.Providers.Resource(ManifestLocalId.Create("resource")).Instance, inputs.Providers.Resource(ManifestLocalId.Create("resource")).Configuration, inputs.Providers.Resource(ManifestLocalId.Create("resource")).Implementation.Value), new("sample/v1", "{}"), []);
        return DurableOperationGraphCodec.Fingerprint(inputs, new([node]));
    }
    [Fact]
    public void Full_structure_exact_numbers_options_origins_and_inputs_roundtrip_detached_and_order_independent()
    {
        var source = Profile(); var token = AssignmentToken.New(); var manifest = Manifest("{\"timeout\":60,\"extra\":1e999}");
        var snapshot = Snapshot(manifest, Parse(source), token);
        var saved = ConfigurationPlanningSnapshotCodec.Encode(snapshot);
        var read = ConfigurationPlanningSnapshotCodec.Decode(saved, Revision);
        Assert.Equal(snapshot, read);
        Assert.Equal(snapshot.GetHashCode(), read.GetHashCode());
        Assert.Equal(saved, ConfigurationPlanningSnapshotCodec.Encode(Snapshot(manifest, Parse(Equivalent(source)), token)));
        Assert.Equal(Fingerprint(snapshot), Fingerprint(Snapshot(manifest, Parse(Equivalent(source)), token)));
        var api = read.Layouts.Single(x => x.Component.Value == "api");
        var request = Assert.Single(api.BindingRequests);
        Assert.Equal("main", request.Slot!.Value);
        Assert.Equal("resource", Assert.IsType<NormalizedLocalResourceTarget>(request.Binding.Target).ResourceRef.Value);
        Assert.Equal(ConfigurationNumber.Parse("60"), request.Options.Members["timeout"]);
        Assert.Equal(ConfigurationNumber.Parse("1e999"), request.Options.Members["extra"]);
        Assert.Equal(ProfileOptionScope.Binding, request.OptionOrigins["timeout"]);
        Assert.Equal(ProfileOptionScope.Profile, request.OptionOrigins["nested"]);
        Assert.Equal(Parse(source).Reference, request.Origin.Profile);
        Assert.Single(read.Layouts.Single(x => x.Component.Value == "worker").Documents);
        source["spec"]!["documents"]!["settings"]!["template"] = new JsonObject();
        Assert.Equal(saved, ConfigurationPlanningSnapshotCodec.Encode(snapshot));
        Assert.NotEqual(snapshot, Snapshot(manifest, Parse(source), token));
        Assert.NotEqual(Fingerprint(snapshot), Fingerprint(Snapshot(manifest, Parse(source), token)));
        Assert.NotEqual(Fingerprint(snapshot), Fingerprint(Snapshot(manifest, Parse(Profile()), AssignmentToken.New())));
        Assert.NotEqual(Fingerprint(snapshot), Fingerprint(Snapshot(Manifest("{\"timeout\":61}"), Parse(Profile()), token)));
        Assert.NotEqual(snapshot, Snapshot(manifest, Parse(Profile()), AssignmentToken.New()));
        Assert.NotEqual(snapshot, Snapshot(Manifest("{\"timeout\":61}"), Parse(Profile()), token));
    }

    [Fact]
    public void Absent_empty_exact_manifest_and_every_saved_layout_field_are_checked()
    {
        var manifest = Manifest(); var profile = Parse(Profile()); var token = AssignmentToken.New();
        var snapshot = Snapshot(manifest, profile, token);
        var scopes = snapshot.Scopes.ToArray();
        scopes[1] = scopes[1] with { Token = AssignmentToken.New() };
        var empty = ConfigurationPlanningSnapshot.Prepare(Revision, manifest, scopes, [profile], Haby.Tests.ConfigurationRenderingSamples.Catalog(), Haby.Tests.ConfigurationRenderingSamples.Producers(manifest.Spec)).Snapshot!;
        Assert.NotEqual(snapshot, empty);
        var text = ConfigurationPlanningSnapshotCodec.Encode(snapshot);
        Assert.Throws<InvalidDataException>(() => ConfigurationPlanningSnapshotCodec.Decode(text,
            ManifestRevisionRef.Create(Revision.ApplicationRecordId, ManifestRevisionNumber.Create(8))));
        foreach (var path in new[] { "format", "applicationRecord", "revision", "scopes/0/token", "layouts/0/component",
            "layouts/0/documents/0/file", "layouts/0/documents/0/source", "layouts/0/documents/0/profileRevision",
            "layouts/0/requests/0/slot", "layouts/0/requests/0/renderer", "layouts/0/requests/0/options/timeout",
            "layouts/0/requests/0/optionOrigins/timeout", "layouts/0/requests/0/projections/0/path" })
        {
            var node = JsonNode.Parse(text)!; var keys = path.Split('/');
            JsonNode parent = node;
            foreach (var key in keys[..^1]) parent = parent is JsonArray a ? a[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)]! : parent[key]!;
            if (parent is JsonArray array) array[int.Parse(keys[^1], System.Globalization.CultureInfo.InvariantCulture)] = "corrupt";
            else parent[keys[^1]] = "corrupt";
            Assert.Throws<InvalidDataException>(() => ConfigurationPlanningSnapshotCodec.Decode(node.ToJsonString(), Revision));
        }
        Assert.Throws<InvalidDataException>(() => ConfigurationPlanningSnapshotCodec.Decode(text.Replace("\"format\":2", "\"format\":2,\"format\":2", StringComparison.Ordinal), Revision));
    }

    [Fact]
    public void Effective_secrets_refuse_with_original_code_but_disabled_history_is_harmless_and_has_no_partial_snapshot()
    {
        var manifest = Manifest(); var profile = Parse(Profile(true)); var scopes = Scopes(manifest, AssignmentToken.New(), new ProfileDecision.Use(profile.Reference));
        var refusal = ConfigurationPlanningSnapshot.Prepare(Revision, manifest, scopes, [profile]);
        Assert.Null(refusal.Snapshot); Assert.Null(refusal.Expansion.Layouts);
        Assert.Contains(refusal.Expansion.Diagnostics, x => x.Code == "profile.secret-projections.unsupported");
        // Suppress only the secret-bearing Profile; an independent ordinary Profile supplies the slot.
        var ordinaryNode = Profile(); ordinaryNode["metadata"]!["id"] = "ordinary";
        var ordinary = Parse(ordinaryNode);
        scopes[1] = scopes[1] with
        {
            Token = AssignmentToken.New(),
            Declaration = new(scopes[1].Target,
            [new ProfileDecision.Disable(profile.Reference.ProfileId), new ProfileDecision.Use(ordinary.Reference)])
        };
        Assert.NotNull(ConfigurationPlanningSnapshot.Prepare(Revision, manifest, scopes, [profile, ordinary], Haby.Tests.ConfigurationRenderingSamples.Catalog(), Haby.Tests.ConfigurationRenderingSamples.Producers(manifest.Spec)).Snapshot);
    }
}
