using Haby.Domain;
using Xunit;

namespace Haby.Domain.Tests;

public sealed class ConfigurationDeferredValueTests
{
    [Theory]
    [InlineData("sample.connection/v1")]
    [InlineData("sample-connection/v12beta3")]
    public void Formatter_ids_use_existing_exact_rules_and_distinct_categories(string text)
    {
        var id = ConfigurationFormatterId.Create(text);
        Assert.Equal(id, ConfigurationFormatterId.Create(text));
        Assert.Equal(text, id.Value);
        Assert.False(((VersionedContractId)id).Equals(ConfigurationRendererId.Create(text)));
    }

    [Theory]
    [InlineData("sample.connection")]
    [InlineData("sample.connection/latest")]
    [InlineData("sample.connection/v0")]
    [InlineData("Sample/v1")]
    [InlineData("sample/v1\n")]
    public void Invalid_formatter_ids_refuse(string text) => Assert.Throws<ArgumentException>(() => ConfigurationFormatterId.Create(text));

    [Fact]
    public void Arguments_are_snapshots_and_equality_and_hash_include_formatter_and_structural_arguments()
    {
        var source = new List<ConfigurationValue> { new ConfigurationString("before") };
        var members = new Dictionary<string, ConfigurationValue> { ["items"] = new ConfigurationArray(source) };
        var value = new ConfigurationDeferredValue(ConfigurationFormatterId.Create("sample/v1"), new(members));
        source.Clear();
        members.Clear();
        var same = new ConfigurationDeferredValue(ConfigurationFormatterId.Create("sample/v1"), new(new Dictionary<string, ConfigurationValue>
        { ["items"] = new ConfigurationArray([new ConfigurationString("before")]) }));
        Assert.Equal(same, value);
        Assert.Equal(same.GetHashCode(), value.GetHashCode());
        Assert.NotEqual(value, new ConfigurationDeferredValue(ConfigurationFormatterId.Create("sample/v2"), same.Arguments));
        Assert.NotEqual(value, new ConfigurationDeferredValue(value.Formatter, new([])));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, ConfigurationValue>)value.Arguments.Members).Clear());
        Assert.Throws<ArgumentNullException>(() => new ConfigurationDeferredValue(null!, new([])));
        Assert.Throws<ArgumentNullException>(() => new ConfigurationDeferredValue(value.Formatter, null!));
    }

    [Fact]
    public void Deferred_arguments_reject_deferred_nodes_at_any_depth_but_accept_exact_secret_references()
    {
        var id = ConfigurationFormatterId.Create("sample/v1");
        var secret = new ConfigurationSecretReference(new(SecretId.New(), SecretVersionId.New()));
        var inner = new ConfigurationDeferredValue(id, new(new Dictionary<string, ConfigurationValue> { ["secret"] = secret }));
        foreach (var nested in new ConfigurationValue[] { inner, new ConfigurationArray([inner]),
            new ConfigurationObject(new Dictionary<string, ConfigurationValue> { ["a"] = new ConfigurationArray([inner]) }) })
            Assert.Throws<ArgumentException>(() => new ConfigurationDeferredValue(id, new(new Dictionary<string, ConfigurationValue> { ["nested"] = nested })));
        Assert.Same(secret, inner.Arguments.Members["secret"]);
    }
}
