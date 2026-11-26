using System.Text;
using System.Collections.Immutable;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ConfigurationCanonicalIdentityTests
{
    [Fact]
    public void Pre_fix_canonical_bytes_and_fingerprints_survive_persisted_round_trips()
    {
        var number = ConfigurationNumber.Parse("-1");
        var values = new ConfigurationObject(new Dictionary<string, ConfigurationValue> { ["Count"] = number, ["Large"] = ConfigurationNumber.Parse("1e9") });
        var profile = new ConfigurationProfileRevision(new(ProfileId.Create("layout"), 1),
            [new ProfileDocument(ManifestLocalId.Create("settings"), "app.json", StructuredDocumentFormat.Json, values)], [], []);
        var environment = EnvironmentRecordId.Parse("019a2b70-1234-7123-8123-123456789abc");
        var revision = ManifestRevisionRef.Create(ApplicationRecordId.Parse("019a2b70-1234-7123-8123-123456789abe"), ManifestRevisionNumber.Create(1));
        var inputs = Haby.Tests.DurableInputSamples.Create(environment, EnvironmentId.Create("pilot"), revision, ProviderAlias.Create("available"),
            ProviderInstanceId.Parse("019a2b70-1234-7123-8123-123456789abd"), ProviderConfigurationRevisionNumber.Create(1), "sample/v1", "resource");
        var entry = inputs.Providers.Entries.Values.Single() with { Naming = new(AdapterInputContractId.Create("sample.naming/v1"), values), Placement = new(AdapterInputContractId.Create("sample.placement/v1"), values) };
        var providers = new ExactProviderResolutionSet(inputs.Providers.Declaration, environment, [entry]);
        var scopes = ConfigurationPlanningSnapshot.Targets(inputs.Providers.Declaration).Select((target, index) => new ConfigurationPlanningScope(target,
            index == 0 ? AssignmentToken.Parse("019a2b70-1234-7123-8123-123456789abf") : null,
            new(target, index == 0 ? [new ProfileDecision.Use(profile.Reference)] : [])));
        var snapshot = ConfigurationPlanningSnapshot.Prepare(revision, inputs.Providers.Declaration, scopes, [profile]).Snapshot!;
        var pin = new ConfigurationRenderPin(ManifestLocalId.Create("api"), 0, ConfigurationRendererId.Create("sample.connection/v1"), ManifestLocalId.Create("sample-v1"),
            ConfigurationFormatterId.Create("sample.string/v1"), ManifestLocalId.Create("sample-v1"), ResourceTypeId.Create("sample.resource/v1"), ManifestLocalId.Create("resource"),
            "sample.output/v1", "value", OperationId.Create("create"), new(new(ResourceTypeId.Create("sample.resource/v1"), ProviderKindId.Create("sample/v1"),
            ProviderConfigurationContractId.Create("sample.configuration/v1"), ProviderImplementationId.Create("sample/v1")), OperationContractId.Create("sample.create/v1")), values, values,
            new Dictionary<string, ProfileOptionScope> { ["Count"] = ProfileOptionScope.Profile, ["Large"] = ProfileOptionScope.Binding }.ToImmutableDictionary());
        var actual = new Dictionary<string, string>
        {
            ["profile"] = ProfileSnapshotCodec.Encode(profile),
            ["declaration"] = Encoding.UTF8.GetString(ConfigurationProfileDeclaration.Serialize(profile)),
            ["providers"] = ExactProviderResolutionSetCodec.Encode(providers),
            ["planning"] = ConfigurationPlanningSnapshotCodec.Encode(snapshot),
            ["pins"] = ConfigurationDocumentCodec.EncodeBounded(w => ConfigurationRenderPinCodec.Write(w, [pin]))
        };
        foreach (var (name, text) in actual)
        {
            var original = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "int32-json", name + ".json"));
            Assert.Equal(original, text);
            Assert.Equal(ExpectedHashes[name], Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        }
        using (var storedPins = System.Text.Json.JsonDocument.Parse(Original("pins")))
        {
            var decoded = ConfigurationRenderPinCodec.Read(storedPins.RootElement);
            Assert.Equal(actual["pins"], ConfigurationDocumentCodec.EncodeBounded(w => ConfigurationRenderPinCodec.Write(w, decoded)));
        }
        Assert.Equal(profile, ProfilePayloadParser.ParseProfile(Encoding.UTF8.GetBytes(Original("declaration"))).Declaration);
        Assert.Equal(profile, ProfileSnapshotCodec.Decode(Original("profile"), "layout", 1, 1, 1));
        Assert.Equal(providers, ExactProviderResolutionSetCodec.Decode(Original("providers"), inputs.Providers.Declaration, environment));
        Assert.Equal(snapshot, ConfigurationPlanningSnapshotCodec.Decode(Original("planning"), revision));
    }
    private static string Original(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "int32-json", name + ".json"));
    private static readonly IReadOnlyDictionary<string, string> ExpectedHashes = new Dictionary<string, string>
    {
        ["profile"] = "f4d81e7a319e49f6543dbb53f06a37f8e1adb1b25bfe1f4c4e30ceeb0124a690",
        ["declaration"] = "67fa158af1d23f25ff38fdb593c6f75a7256d481b09b2bdb57be7f41dab034ab",
        ["providers"] = "e0f63ba8287a6e5362dd87ac127a4f8969b75c12ac22e51df2e629c8bab8dfa9",
        ["planning"] = "645882ae33a694539d1fb4878d9f9bc3c2189f91e4c464826265ebbebf31393d",
        ["pins"] = "9c2af1b59c5190890ee60c51c5c4474df9f4504c3a848f630447a6d24c29b065",
    };
}
