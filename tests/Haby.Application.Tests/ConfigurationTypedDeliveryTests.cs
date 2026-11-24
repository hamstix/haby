using System.Text.Json;
using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed partial class ConfigurationDeliveryTests
{
    [Fact]
    public void Resolved_value_measurement_rejects_the_first_container_beyond_the_subtree_depth_limit()
    {
        ConfigurationValue value = ConfigurationNull.Instance;
        for (var i = 0; i < ConfigurationDocumentCodec.MaxContainers; i++) value = new ConfigurationArray([value]);
        // Each array adds two ASCII delimiters; the null leaf contributes four bytes.
        Assert.Equal(2L * ConfigurationDocumentCodec.MaxContainers + 4, ConfigurationJson.MeasureResolvedValue(value, default));
        value = new ConfigurationArray([value]);
        Assert.Throws<ConfigurationDocumentLimitException>(() => ConfigurationJson.MeasureResolvedValue(value, default));
    }

    [Fact]
    public async Task Typed_deferred_formatter_delivers_complete_JSON_without_changing_unresolved_artifact()
    {
        var h = await Prepare(2);
        var before = ConfigurationDocumentCodec.Encode(h.Store.Artifact.Revision);
        h.Adapter.ValueFactory = material => new ConfigurationObject(new Dictionary<string, ConfigurationValue>
        {
            ["Endpoint"] = new ConfigurationObject(new Dictionary<string, ConfigurationValue> { ["Host"] = new ConfigurationString("service.example.invalid") }),
            ["Consumer"] = ConfigurationNumber.Parse("0"),
            ["Password"] = new ConfigurationString(material)
        });
        using var result = await h.Run();
        Assert.Equal(ConfigurationDeliveryStatus.Delivered, result.Status);
        using var json = JsonDocument.Parse(result.Json);
        foreach (var path in new[] { "value", "second" })
        {
            var value = json.RootElement.GetProperty(path);
            Assert.Equal(JsonValueKind.Object, value.ValueKind);
            Assert.Equal("service.example.invalid", value.GetProperty("Endpoint"u8).GetProperty("Host"u8).GetString());
            Assert.Equal(ConfigurationNumber.Parse("0"), ConfigurationNumber.Parse(value.GetProperty("Consumer"u8).GetRawText()));
            Assert.Equal("synthetic-private-material", value.GetProperty("Password"u8).GetString());
        }
        Assert.Equal(1, h.Store.Resolutions);
        Assert.Equal(2, h.Adapter.Calls);
        Assert.Equal(before, ConfigurationDocumentCodec.Encode(h.Store.Artifact.Revision));
        Assert.DoesNotContain("synthetic-private-material", before);
        Assert.Equal(h.Store.Artifact.Revision.Metadata.Fingerprint, result.SourceFingerprint);
        var preview = ConfigurationJson.Preview(h.Store.Artifact.Revision.Tree);
        Assert.DoesNotContain("synthetic-private-material", preview.Json);
        Assert.Equal(new[] { "/second", "/value" }, preview.RedactedPaths);
    }

    [Fact]
    public async Task Formatter_cannot_inject_another_confidential_resolution_into_completed_JSON()
    {
        var h = await Prepare();
        h.Adapter.ValueFactory = _ => new ConfigurationObject(new Dictionary<string, ConfigurationValue>
        { ["injected"] = new ConfigurationSecretReference(new(SecretId.New(), SecretVersionId.New())) });
        using var result = await h.Run();
        Assert.Equal(ConfigurationDeliveryStatus.InputUnavailable, result.Status);
        Assert.True(result.Json.IsEmpty);
        Assert.Equal(1, h.Store.Resolutions);
        Assert.Equal(1, h.Adapter.Calls);
    }

    [Fact]
    public async Task Expanded_typed_formatter_depth_is_checked_in_the_complete_receiving_document()
    {
        var h = await Prepare();
        h.Adapter.ValueFactory = _ =>
        {
            ConfigurationValue value = new ConfigurationString("bounded");
            for (var i = 0; i < ConfigurationDocumentCodec.MaxContainers; i++) value = new ConfigurationArray([value]);
            return value;
        };
        using var result = await h.Run();
        Assert.Equal(ConfigurationDeliveryStatus.LimitExceeded, result.Status);
        Assert.True(result.Json.IsEmpty);
        Assert.Equal(1, h.Store.Resolutions);
        Assert.Equal(1, h.Adapter.Calls);
    }
}
