using Haby.Domain;
using Xunit;

namespace Haby.Domain.Tests;

public sealed class ValueObjectTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("automation-processor-service")]
    [InlineData("org.example.automation")]
    [InlineData("a0-b1.c2")]
    public void ApplicationId_accepts_valid_values(string value) =>
        Assert.Equal(value, ApplicationId.Create(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("0app")]
    [InlineData("app--service")]
    [InlineData("app.-service")]
    [InlineData("app.")]
    [InlineData("app_1")]
    [InlineData("app/1")]
    [InlineData("appé")]
    public void ApplicationId_rejects_invalid_values(string value) =>
        Assert.Throws<ArgumentException>(() => ApplicationId.Create(value));

    [Fact]
    public void ApplicationId_enforces_maximum_length_and_ordinal_equality()
    {
        var longest = "a" + new string('0', 127);
        Assert.Equal(longest, ApplicationId.Create(longest).Value);
        Assert.Throws<ArgumentException>(() => ApplicationId.Create(longest + "0"));
        Assert.Equal(ApplicationId.Create("app.one"), ApplicationId.Create("app.one"));
        Assert.NotEqual(ApplicationId.Create("app.one"), ApplicationId.Create("app-one"));
        Assert.Throws<ArgumentNullException>(() => ApplicationId.Create(null!));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3.4.6-rev.1042")]
    [InlineData("2026.10.13")]
    [InlineData("1.0.0+build.17")]
    [InlineData("A_b-C")]
    public void ApplicationVersion_accepts_valid_values(string value) =>
        Assert.Equal(value, ApplicationVersion.Create(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData(".1")]
    [InlineData("_1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("v 1")]
    [InlineData("v/1")]
    [InlineData("vé")]
    public void ApplicationVersion_rejects_invalid_values(string value) =>
        Assert.Throws<ArgumentException>(() => ApplicationVersion.Create(value));

    [Fact]
    public void ApplicationVersion_enforces_maximum_length_and_ordinal_equality()
    {
        var longest = "V" + new string('a', 127);
        Assert.Equal(longest, ApplicationVersion.Create(longest).Value);
        Assert.Throws<ArgumentException>(() => ApplicationVersion.Create(longest + "a"));
        Assert.Equal(ApplicationVersion.Create("v1"), ApplicationVersion.Create("v1"));
        Assert.NotEqual(ApplicationVersion.Create("v1"), ApplicationVersion.Create("V1"));
        Assert.Throws<ArgumentNullException>(() => ApplicationVersion.Create(null!));
    }

    [Fact]
    public void Persisted_ids_are_uuidv7_canonical_and_aggregate_specific()
    {
        var applicationId = ApplicationRecordId.New();
        var revisionId = ManifestRevisionId.New();
        Assert.Equal(7, Guid.Parse(applicationId.ToString()).Version);
        Assert.Equal(7, Guid.Parse(revisionId.ToString()).Version);
        Assert.Equal(applicationId, ApplicationRecordId.Parse(applicationId.ToString()));
        Assert.Equal(revisionId, ManifestRevisionId.Parse(revisionId.ToString()));
        Assert.NotEqual(applicationId, ApplicationRecordId.New());
        Assert.NotEqual(revisionId, ManifestRevisionId.New());
        Assert.NotEqual<object>(applicationId, revisionId);
        Assert.NotEqual(typeof(ApplicationRecordId), typeof(ManifestRevisionId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("00000000-0000-4000-8000-000000000000")]
    [InlineData("00000000-0000-7000-0000-000000000000")]
    [InlineData("00000000000070008000000000000000")]
    public void Persisted_ids_reject_noncanonical_or_non_uuidv7_values(string value)
    {
        Assert.Throws<ArgumentException>(() => ApplicationRecordId.Parse(value));
        Assert.Throws<ArgumentException>(() => ManifestRevisionId.Parse(value));
    }

    [Fact]
    public void Revision_number_is_positive_and_compares_by_value()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ManifestRevisionNumber.Create(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ManifestRevisionNumber.Create(-1));
        Assert.Equal(ManifestRevisionNumber.Create(1), ManifestRevisionNumber.Create(1));
        Assert.NotEqual(ManifestRevisionNumber.Create(1), ManifestRevisionNumber.Create(2));
        Assert.Equal(long.MaxValue, ManifestRevisionNumber.Create(long.MaxValue).Value);
    }

    [Fact]
    public void Revision_reference_requires_both_parts_and_compares_both()
    {
        var applicationId = ApplicationRecordId.New();
        var number = ManifestRevisionNumber.Create(1);
        Assert.Throws<ArgumentNullException>(() => ManifestRevisionRef.Create(null!, number));
        Assert.Throws<ArgumentNullException>(() => ManifestRevisionRef.Create(applicationId, null!));
        var reference = ManifestRevisionRef.Create(applicationId, number);
        Assert.Equal(applicationId, reference.ApplicationRecordId);
        Assert.Equal(number, reference.RevisionNumber);
        Assert.Equal(reference, ManifestRevisionRef.Create(ApplicationRecordId.Parse(applicationId.ToString()), ManifestRevisionNumber.Create(1)));
        Assert.NotEqual(reference, ManifestRevisionRef.Create(ApplicationRecordId.New(), number));
        Assert.NotEqual(reference, ManifestRevisionRef.Create(applicationId, ManifestRevisionNumber.Create(2)));
    }
}
