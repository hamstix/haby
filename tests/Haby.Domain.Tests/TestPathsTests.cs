using Haby.Tests;
using Xunit;

namespace Haby.Domain.Tests;

public sealed class TestPathsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("PROTECTED-relative")]
    public void Missing_or_relative_source_root_is_refused_without_fallback(string? value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => TestPaths.ValidateRoot(value));
        Assert.Equal("Set HABY_TEST_SOURCE_ROOT to an absolute repository root.", error.Message);
    }
    [Fact]
    public void Existing_directory_without_source_markers_is_not_a_source_root()
    {
        var directory = Path.Combine(Path.GetTempPath(), "haby-source-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { Assert.Throws<InvalidOperationException>(() => TestPaths.ValidateRoot(directory)); }
        finally { Directory.Delete(directory); }
    }

    [Theory]
    [InlineData("../PROTECTED-outside")]
    [InlineData("..\\PROTECTED-outside")]
    public void Copied_assets_cannot_follow_parent_paths(string value) =>
        Assert.Throws<InvalidOperationException>(() => TestPaths.AssetPath(value));

    [Fact]
    public void Missing_copied_asset_is_not_replaced_by_repository_search() =>
        Assert.Throws<InvalidOperationException>(() => TestPaths.AssetPath("PROTECTED-missing-asset"));

    [Fact]
    public void Empty_copied_example_directory_cannot_silently_skip_validation()
    {
        var name = "empty-control-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(TestPaths.AssetsRoot, name);
        Directory.CreateDirectory(directory);
        try { Assert.Throws<InvalidOperationException>(() => TestPaths.AssetDirectory(name)); }
        finally { Directory.Delete(directory); }
    }
}
