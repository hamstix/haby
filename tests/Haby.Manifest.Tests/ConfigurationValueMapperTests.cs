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

    [Fact]
    public void Mapping_rejects_unvalidated_duplicate_members()
    {
        using var document = JsonDocument.Parse("""{"a":1,"a":2}""");
        Assert.Throws<ArgumentException>(() => ConfigurationValueMapper.FromValidatedJson(document.RootElement));
    }
}
