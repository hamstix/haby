using Haby.Domain;
using Xunit;

namespace Haby.Domain.Tests;

public sealed class ExactManifestNumberTests
{
    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1", "1e0")]
    [InlineData("-0", "0")]
    [InlineData("1e999999999999999999999", "10e999999999999999999998")]
    public void Mathematical_equivalence_has_equal_hashes(string first, string second)
    {
        var left = ExactManifestNumber.Parse(first);
        var right = ExactManifestNumber.Parse(second);
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.Equal(left.CanonicalText, right.CanonicalText);
    }

    [Fact]
    public void Long_precision_is_not_rounded()
    {
        Assert.NotEqual(
            ExactManifestNumber.Parse("0.123456789012345678901234567890123456789"),
            ExactManifestNumber.Parse("0.123456789012345678901234567890123456788"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1e2")]
    [InlineData("-0")]
    public void Mathematical_integers_are_recognized(string raw) =>
        Assert.True(ExactManifestNumber.Parse(raw).IsInteger);

    [Fact]
    public void Fractional_number_is_not_an_integer() =>
        Assert.False(ExactManifestNumber.Parse("1.1").IsInteger);
}
