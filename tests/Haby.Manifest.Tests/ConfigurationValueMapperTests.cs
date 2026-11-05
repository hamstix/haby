using System.Text.Json;
using Haby.Domain;
using Haby.Manifest;
using Xunit;

namespace Haby.Manifest.Tests;

public sealed class ConfigurationValueMapperTests
{
    [Fact]
    public void Mapping_copies_validated_json_before_document_disposal()
    {
        ConfigurationValue mapped;
        using (var document = JsonDocument.Parse("""{"nested":[null,true,1e999999999999999999999,"\u00e9"]}"""))
            mapped = ConfigurationValueMapper.FromValidatedJson(document.RootElement);
        var expected = new ConfigurationObject(new Dictionary<string, ConfigurationValue>
        {
            ["nested"] = new ConfigurationArray([
                ConfigurationNull.Instance, new ConfigurationBoolean(true),
                ConfigurationNumber.Parse("1e999999999999999999999"), new ConfigurationString("é")])
        });
        Assert.Equal(expected, mapped);
        Assert.Equal(expected.GetHashCode(), mapped.GetHashCode());
    }

    [Fact]
    public void Json_shaped_fake_reference_remains_an_ordinary_object_at_any_depth()
    {
        using var document = JsonDocument.Parse("""{"$secretRef":{"secretId":"value","secretVersionId":"version"},"nested":{"$secretRef":{"secretId":"x","secretVersionId":"y"}}}""");
        var mapped = Assert.IsType<ConfigurationObject>(ConfigurationValueMapper.FromValidatedJson(document.RootElement));
        Assert.IsType<ConfigurationObject>(mapped.Members["$secretRef"]);
        var nested = Assert.IsType<ConfigurationObject>(mapped.Members["nested"]);
        Assert.IsType<ConfigurationObject>(nested.Members["$secretRef"]);
        Assert.IsNotType<ConfigurationSecretReference>(nested.Members["$secretRef"]);
    }

    [Theory]
    [InlineData("{\"kind\":\"deferred\",\"formatter\":\"sample/v1\",\"arguments\":{\"kind\":\"secret\",\"id\":\"x\"}}")]
    [InlineData("{\"$deferredValue\":{\"formatterId\":\"sample/v1\",\"Arguments\":{\"$secretRef\":{\"secretId\":\"x\"}}}}")]
    public void Magic_looking_json_remains_ordinary_through_schema_pipeline_and_normalized_mapping(string template)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("{\"applicationVersion\":\"v1\",\"manifest\":{\"apiVersion\":\"haby.dev/v1alpha1\",\"kind\":\"Application\",\"metadata\":{\"id\":\"sample\"},\"spec\":{\"components\":{\"api\":{\"documents\":{\"settings\":{\"format\":\"json\",\"fileName\":\"settings.json\",\"template\":" + template + "}}}}}}}");
        var candidate = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare(payload, new ManifestContractCatalog([], [], [], [])));
        var document = Assert.IsType<NormalizedStructuredDocument>(candidate.Candidate.Declaration.Spec.Components[ManifestLocalId.Create("api")].Documents[ManifestLocalId.Create("settings")]);
        AssertOrdinary(document.Template);
    }

    private static void AssertOrdinary(ConfigurationValue value)
    {
        Assert.IsNotType<ConfigurationDeferredValue>(value);
        Assert.IsNotType<ConfigurationSecretReference>(value);
        if (value is ConfigurationObject obj) foreach (var child in obj.Members.Values) AssertOrdinary(child);
        if (value is ConfigurationArray array) foreach (var child in array.Items) AssertOrdinary(child);
    }
    [Fact]
    public void Mapping_rejects_unvalidated_duplicate_members()
    {
        using var document = JsonDocument.Parse("""{"a":1,"a":2}""");
        Assert.Throws<ArgumentException>(() => ConfigurationValueMapper.FromValidatedJson(document.RootElement));
    }
}
