using Haby.Domain;
using Xunit;

namespace Haby.Domain.Tests;

public sealed class ConfigurationValueTests
{
    [Fact]
    public void Nested_objects_compare_by_ordinal_keys_and_values_independent_of_member_order()
    {
        var first = new ConfigurationObject(new Dictionary<string, ConfigurationValue>
        {
            ["items"] = new ConfigurationArray([new ConfigurationBoolean(true), ConfigurationNull.Instance]),
            ["name"] = new ConfigurationString("é")
        });
        var reordered = new ConfigurationObject(new Dictionary<string, ConfigurationValue>
        {
            ["name"] = new ConfigurationString("é"),
            ["items"] = new ConfigurationArray([new ConfigurationBoolean(true), ConfigurationNull.Instance])
        });
        Assert.Equal(first, reordered);
        Assert.Equal(first.GetHashCode(), reordered.GetHashCode());
        Assert.NotEqual(first, new ConfigurationObject(new Dictionary<string, ConfigurationValue>
        {
            ["items"] = new ConfigurationArray([ConfigurationNull.Instance, new ConfigurationBoolean(true)]),
            ["name"] = new ConfigurationString("é")
        }));
        Assert.NotEqual(new ConfigurationString("a"), new ConfigurationString("A"));
        Assert.NotEqual(new ConfigurationString("é"), new ConfigurationString("e\u0301"));
        Assert.NotEqual(new ConfigurationObject(new Dictionary<string, ConfigurationValue> { ["a"] = ConfigurationNull.Instance }),
            new ConfigurationObject(new Dictionary<string, ConfigurationValue> { ["A"] = ConfigurationNull.Instance }));
        Assert.Equal(ConfigurationNull.Instance, ConfigurationNull.Instance);
    }

    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1", "1e0")]
    [InlineData("-0", "0")]
    [InlineData("123456789012345678901234567890.12345678901234567890", "1234567890123456789012345678901234567890123456789e-19")]
    [InlineData("1e999999999999999999999", "10e999999999999999999998")]
    public void Exact_numbers_have_mathematical_equality_and_consistent_hashes(string left, string right)
    {
        var a = ConfigurationNumber.Parse(left);
        var b = ConfigurationNumber.Parse(right);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Exact_numbers_preserve_distinct_long_precision_and_large_exponents()
    {
        Assert.NotEqual(ConfigurationNumber.Parse("1e999999999999999999999"), ConfigurationNumber.Parse("1e999999999999999999998"));
        Assert.NotEqual(ConfigurationNumber.Parse("0.1234567890123456789012345678901"), ConfigurationNumber.Parse("0.1234567890123456789012345678902"));
        foreach (var invalid in new[] { "", "+1", "01", "1.", ".1", "1e", "NaN", "Infinity", " 1" })
            Assert.Throws<FormatException>(() => ConfigurationNumber.Parse(invalid));
    }

    [Fact]
    public void Array_and_object_copy_input_collections_and_expose_no_mutable_storage()
    {
        var children = new List<ConfigurationValue> { new ConfigurationString("before") };
        var members = new Dictionary<string, ConfigurationValue> { ["nested"] = new ConfigurationArray(children) };
        var tree = new ConfigurationObject(members);
        children[0] = new ConfigurationString("after");
        members["nested"] = ConfigurationNull.Instance;
        Assert.Equal(new ConfigurationString("before"), Assert.IsType<ConfigurationArray>(tree.Members["nested"]).Items[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<ConfigurationValue>)Assert.IsType<ConfigurationArray>(tree.Members["nested"]).Items)[0] = ConfigurationNull.Instance);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, ConfigurationValue>)tree.Members)["nested"] = ConfigurationNull.Instance);
        Assert.Throws<ArgumentException>(() => new ConfigurationArray([null!]));
        Assert.Throws<ArgumentException>(() => new ConfigurationObject(new Dictionary<string, ConfigurationValue> { ["bad"] = null! }));
        Assert.Throws<ArgumentException>(() => new ConfigurationObject(new[]
        {
            new KeyValuePair<string, ConfigurationValue>("duplicate", ConfigurationNull.Instance),
            new KeyValuePair<string, ConfigurationValue>("duplicate", ConfigurationNull.Instance)
        }));
    }

    [Fact]
    public void Secret_reference_pins_two_typed_uuidv7_ids()
    {
        var secret = SecretId.New();
        var version = SecretVersionId.New();
        var reference = new SecretRef(secret, version);
        var same = new SecretRef(SecretId.Parse(secret.ToString()), SecretVersionId.Parse(version.ToString()));
        Assert.Equal(reference, same);
        Assert.Equal(reference.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(reference, new SecretRef(secret, SecretVersionId.New()));
        Assert.NotEqual(reference, new SecretRef(SecretId.New(), version));
        Assert.Equal(new ConfigurationSecretReference(reference), new ConfigurationSecretReference(same));
        Assert.Equal(new ConfigurationSecretReference(reference).GetHashCode(), new ConfigurationSecretReference(same).GetHashCode());
        Assert.Throws<ArgumentNullException>(() => new SecretRef(null!, version));
        Assert.Throws<ArgumentNullException>(() => new SecretRef(secret, null!));
        Assert.Throws<ArgumentException>(() => SecretId.Parse("00000000-0000-4000-8000-000000000000"));
        Assert.Throws<ArgumentException>(() => SecretVersionId.Parse("00000000-0000-4000-8000-000000000000"));
    }
}
