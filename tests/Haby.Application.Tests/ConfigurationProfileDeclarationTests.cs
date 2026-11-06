using System.Text;
using System.Text.Json;
using Haby.Application;
using Haby.Manifest;
using Xunit;
using static Haby.Tests.ProfilePersistenceSamples;

namespace Haby.Application.Tests;

public sealed class ConfigurationProfileDeclarationTests
{
    [Fact]
    public void Complete_portable_declaration_round_trips_with_exact_numbers_and_deterministic_sets()
    {
        var source = Node();
        source["spec"]!["documents"]!["settings"]!["template"]!["ordinary"] = System.Text.Json.Nodes.JsonNode.Parse(
            """{"n":1e999,"tiny":1e-999,"fraction":0.123456789012345678901234567890123456789,"values":[null,true,false,"1e999",{},[],{"kind":"SecretReference","$type":"DeferredValue","value":"ordinary"}]}"""u8);
        var profile = Parse(source);
        var bytes = ConfigurationProfileDeclaration.Serialize(profile);
        var parsed = ProfilePayloadParser.ParseProfile(bytes);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(profile, parsed.Declaration);
        Assert.Equal(bytes, ConfigurationProfileDeclaration.Serialize(Parse(Equivalent(source))));
        Assert.Contains("1e999", Encoding.UTF8.GetString(bytes));
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("spec"u8).GetProperty("documents"u8)
            .GetProperty("settings"u8).GetProperty("template"u8).GetProperty("ordinary"u8).GetProperty("n"u8).ValueKind);
        var equivalent = Encoding.UTF8.GetString(bytes).Replace(":1e999,", ":10e998,", StringComparison.Ordinal);
        Assert.Equal(profile, ProfilePayloadParser.ParseProfile(Encoding.UTF8.GetBytes(equivalent)).Declaration);
        Assert.Equal(profile, ProfilePayloadParser.ParseProfile(JsonSerializer.SerializeToUtf8Bytes(ConfigurationProfileDeclaration.Map(profile),
            new ManifestJsonContext(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }).ConfigurationProfileDocument)).Declaration);
    }

    [Theory]
    [InlineData("-0.0e999", "0")]
    [InlineData("1e0", "1")]
    [InlineData("10", "10")]
    [InlineData("100", "1e2")]
    [InlineData("0.1", "0.1")]
    [InlineData("-0.1", "-0.1")]
    [InlineData("1.2345", "1.2345")]
    [InlineData("1e999", "1e999")]
    [InlineData("1e-999", "1e-999")]
    [InlineData("1e999999999999999999999999999999", "1e999999999999999999999999999999")]
    [InlineData("1e-999999999999999999999999999999", "1e-999999999999999999999999999999")]
    public void Portable_numeric_spelling_is_compact_without_changing_domain_canonical_contract(string input, string expected)
    {
        var number = Haby.Domain.ConfigurationNumber.Parse(input);
        var canonical = number.CanonicalText; var hash = number.GetHashCode();
        var profile = new Haby.Domain.ConfigurationProfileRevision(new(Haby.Domain.ProfileId.Create("layout"), 1),
            [new Haby.Domain.ProfileDocument(Haby.Domain.ManifestLocalId.Create("settings"), "app.json", Haby.Domain.StructuredDocumentFormat.Json, number)], [], []);
        using var json = JsonDocument.Parse(ConfigurationProfileDeclaration.Serialize(profile));
        var value = json.RootElement.GetProperty("spec"u8).GetProperty("documents"u8).GetProperty("settings"u8).GetProperty("template"u8);
        Assert.Equal(expected, value.GetRawText());
        Assert.Equal(number, Haby.Domain.ConfigurationNumber.Parse(value.GetRawText()));
        Assert.Equal(canonical, number.CanonicalText); Assert.Equal(hash, number.GetHashCode());
        Assert.Equal(canonical, ConfigurationJson.Serialize(number).Json);
    }

    [Fact]
    public void Portable_strings_keys_controls_and_unicode_round_trip_without_loss()
    {
        var source = Node();
        var text = new string(Enumerable.Range(0, 32).Select(x => (char)x).ToArray()) + "\"\\/　\uE000😀";
        source["spec"]!["documents"]!["settings"]!["template"] = new System.Text.Json.Nodes.JsonObject { [text] = text };
        source["spec"]!["staticContributions"]!["logging"]!["path"] = "/　😀";
        var profile = Parse(source);
        var normalized = ConfigurationProfileDeclaration.Serialize(profile);
        Assert.Equal(profile, ProfilePayloadParser.ParseProfile(normalized).Declaration);
        Assert.True(normalized.Length < Bytes(source).Length);
    }

    [Fact]
    public void Near_limit_normalized_output_reimports_within_existing_byte_budget()
    {
        foreach (var source in Haby.Tests.ProfileDeclarationBoundarySamples.Sources())
        {
            var input = Encoding.UTF8.GetBytes(source);
            Assert.True(input.Length < 1024 * 1024 - 1024);
            var accepted = ProfilePayloadParser.ParseProfile(input);
            Assert.True(accepted.IsSuccess);
            var normalized = ConfigurationProfileDeclaration.Serialize(accepted.Declaration!);
            Assert.True(normalized.Length <= input.Length, $"Input {input.Length}; normalized {normalized.Length}.");
            Assert.Equal(accepted.Declaration, ProfilePayloadParser.ParseProfile(normalized).Declaration);
        }
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"documents":{},"staticContributions":{},"bindingRules":{}}""")]
    [InlineData("""{"documents":{"settings":{"fileName":"app.json","format":"json","template":null}},"bindingRules":{"main":{"slot":"main","rendererRef":"sample/v1","documentProjections":[{"documentRef":"settings","path":"/a~1b"}]}}}""")]
    public void Empty_and_default_forms_round_trip(string spec)
    {
        var profile = ProfilePayloadParser.ParseProfile(Encoding.UTF8.GetBytes(
            "{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"ConfigurationProfile\",\"metadata\":{\"id\":\"empty\",\"revision\":1},\"spec\":" + spec + "}")).Declaration;
        Assert.NotNull(profile);
        Assert.Equal(profile, ProfilePayloadParser.ParseProfile(ConfigurationProfileDeclaration.Serialize(profile)).Declaration);
    }
}
