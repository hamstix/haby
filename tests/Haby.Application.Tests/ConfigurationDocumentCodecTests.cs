using System.Text;
using System.Text.Json;
using Haby.Application;
using Haby.Domain;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ConfigurationDocumentCodecTests
{
    [Fact]
    public void Bounded_document_composes_thousands_of_explicit_siblings_without_an_extra_projection_count_limit()
    {
        var secret = new ConfigurationSecretReference(new(SecretId.New(), SecretVersionId.New()));
        var projections = Enumerable.Range(0, 6000).Select(i => new ConfigurationProjection("/root/k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new(secret), ManifestLocalId.Create("binding")));
        var composed = new ConfigurationComposer(new([], [])).Compose(Object(("Logging", new ConfigurationBoolean(true))), projections);
        Assert.True(composed.IsSuccess);
        Assert.Equal(6000, Assert.IsType<ConfigurationObject>(Assert.IsType<ConfigurationObject>(composed.Document).Members["root"]).Members.Count);
        var preview = ConfigurationJson.Preview(composed.Document!); Assert.Equal(6000, preview.RedactedPaths.Count);
        RoundTrip(Revision(composed.Document!));
    }

    private static ConfigurationDocumentRevision Revision(ConfigurationValue tree) => new(new(ConfigurationDocumentRevisionId.New(),
        new(ApplicationDeploymentId.New(), DeploymentGeneration.Create(1), ManifestLocalId.Create("api"), ManifestLocalId.Create("settings")),
        EnvironmentId.Create("pilot"), Haby.Domain.ApplicationId.Create("example"), ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)),
        DeploymentPlanId.New(), "appsettings.json", "json", ConfigurationJson.Profile, "", new string('a', 64), "tester", DateTimeOffset.UnixEpoch), "frozen", "[]", tree);
    private static ConfigurationValue RoundTrip(ConfigurationDocumentRevision revision)
    {
        var bytes = ConfigurationDocumentCodec.Encode(revision);
        var decoded = ConfigurationDocumentCodec.Decode(bytes, ConfigurationDocumentCodec.Fingerprint(bytes));
        Assert.Equal(revision.Tree, decoded.Tree); Assert.Equal(bytes, ConfigurationDocumentCodec.Encode(decoded));
        return decoded.Tree;
    }
    private static ConfigurationObject Object(params (string Key, ConfigurationValue Value)[] members) => new(members.Select(x => KeyValuePair.Create(x.Key, x.Value)));

    [Fact]
    public void Closed_codec_preserves_all_kinds_exact_numbers_magic_objects_and_whole_confidential_nodes()
    {
        var secret = new ConfigurationSecretReference(new(SecretId.New(), SecretVersionId.New()));
        var magic = Object(("kind", new ConfigurationString("SecretReference")), ("secret", new ConfigurationString("ordinary-selector")));
        var deferred = new ConfigurationDeferredValue(ConfigurationFormatterId.Create("synthetic.connection/v1"), Object(("password", secret), ("ordinary", magic)));
        var tree = Object(("a~/", new ConfigurationArray([ConfigurationNull.Instance, new ConfigurationBoolean(true), ConfigurationNumber.Parse("-123456789012345678901234567890.001e999"), magic, secret])),
            ("connection", deferred), ("literal", new ConfigurationString(ConfigurationJson.RedactedMarker)), ("unicode", new ConfigurationString("Ж🔑\n<>")));
        var actual = RoundTrip(Revision(tree));
        Assert.False(ConfigurationJson.Serialize(actual).IsSuccess);
        var preview = ConfigurationJson.Preview(actual);
        Assert.Equal(new[] { "/a~0~1/4", "/connection" }, preview.RedactedPaths);
        Assert.DoesNotContain(secret.Reference.SecretId.ToString(), preview.Json);
        Assert.DoesNotContain("password", preview.Json); Assert.DoesNotContain("synthetic.connection", preview.Json);
        Assert.Contains("ordinary-selector", preview.Json);
        Assert.Equal(new[] { "" }, ConfigurationJson.Preview(RoundTrip(Revision(secret))).RedactedPaths);
    }

    [Theory]
    [InlineData("unknown-tag")]
    [InlineData("unknown-version")]
    [InlineData("duplicate-key")]
    [InlineData("extra-member")]
    [InlineData("noncanonical-number")]
    public void Corrupt_or_unsupported_internal_content_never_reconstructs(string mode)
    {
        var revision = Revision(Object(("x", ConfigurationNumber.Parse("1"))));
        var payload = ConfigurationDocumentCodec.Encode(revision);
        payload = mode switch
        {
            "unknown-tag" => payload.Replace("\"number\"", "\"mystery\"", StringComparison.Ordinal),
            "unknown-version" => payload.Replace(ConfigurationDocumentCodec.Version, "haby.configuration-revision/v999", StringComparison.Ordinal),
            "duplicate-key" => payload.Replace("\"x\":[\"number\",\"1e0\"]", "\"x\":[\"number\",\"1e0\"],\"x\":[\"null\"]", StringComparison.Ordinal),
            "extra-member" => payload[..^1] + ",0]",
            _ => payload.Replace("\"1e0\"", "\"1.0\"", StringComparison.Ordinal)
        };
        Assert.Throws<InvalidDataException>(() => ConfigurationDocumentCodec.Decode(payload, ConfigurationDocumentCodec.Fingerprint(payload)));
    }

    [Fact]
    public void Semantic_64_container_boundary_has_separate_internal_wrapper_allowance()
    {
        ConfigurationValue tree = new ConfigurationString("leaf");
        for (var i = 0; i < 64; i++) tree = i % 2 == 0 ? new ConfigurationArray([tree]) : Object(("x", tree));
        RoundTrip(Revision(tree));
        using var preview = JsonDocument.Parse(ConfigurationJson.Preview(tree).Json, new JsonDocumentOptions { MaxDepth = 66 });
        Assert.Throws<ConfigurationDocumentLimitException>(() => ConfigurationDocumentCodec.Encode(Revision(new ConfigurationArray([tree]))));
        Assert.Throws<ConfigurationDocumentLimitException>(() => ConfigurationJson.Preview(new ConfigurationArray([tree])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Internal_payload_exact_4MiB_and_one_byte_over_are_measured_as_encoded_UTF8(bool unicode)
    {
        var initial = Revision(new ConfigurationString(unicode ? "Ж🔑<>\n" : ""));
        var overhead = Encoding.UTF8.GetByteCount(ConfigurationDocumentCodec.Encode(initial));
        var fill = new string('a', ConfigurationDocumentCodec.MaxBytes - overhead);
        var prefix = ((ConfigurationString)initial.Tree).Value;
        var exact = initial with { Tree = new ConfigurationString(prefix + fill) };
        var payload = ConfigurationDocumentCodec.Encode(exact);
        Assert.Equal(ConfigurationDocumentCodec.MaxBytes, Encoding.UTF8.GetByteCount(payload));
        RoundTrip(exact);
        Assert.Throws<ConfigurationDocumentLimitException>(() => ConfigurationDocumentCodec.Encode(exact with { Tree = new ConfigurationString(prefix + fill + "a") }));
    }

    [Fact]
    public void Complete_preview_envelope_exact_budget_handles_Unicode_escaping_and_literal_marker()
    {
        const string prefix = "Ж🔑<>\n***REDACTED***";
        var overhead = Encoding.UTF8.GetByteCount(ConfigurationJson.Preview(new ConfigurationString(prefix)).Json);
        var value = new ConfigurationString(prefix + new string('a', ConfigurationDocumentCodec.MaxBytes - overhead));
        var preview = ConfigurationJson.Preview(value);
        Assert.Equal(ConfigurationDocumentCodec.MaxBytes, Encoding.UTF8.GetByteCount(preview.Json)); Assert.Empty(preview.RedactedPaths);
        Assert.Throws<ConfigurationDocumentLimitException>(() => ConfigurationJson.Preview(new ConfigurationString(value.Value + "a")));
    }
}
