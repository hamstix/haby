namespace Haby.Tests;

// Test-only source inspection is explicitly supplied by the caller. Data-only
// consumers use copied assets; neither path walks build-output ancestors.
internal static class TestPaths
{
    internal static string SourceRoot => ValidateRoot(Environment.GetEnvironmentVariable("HABY_TEST_SOURCE_ROOT"));
    internal static string ValidateRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException("Set HABY_TEST_SOURCE_ROOT to an absolute repository root.");
        var root = Path.GetFullPath(value);
        if (!File.Exists(Path.Combine(root, "HamstixHaby.slnx")) ||
            !File.Exists(Path.Combine(root, "src", "Haby.Domain", "Haby.Domain.csproj")))
            throw new InvalidOperationException("HABY_TEST_SOURCE_ROOT is not a complete Haby source root.");
        return root;
    }
    internal static string AssetsRoot => Path.Combine(AppContext.BaseDirectory, "TestAssets");
    internal static string AssetPath(params string[] parts)
    {
        if (parts.Any(p => Path.IsPathRooted(p) || p.Replace('\\', '/').Split('/').Contains("..")))
            throw new InvalidOperationException("Test asset paths must remain relative to copied assets.");
        var path = Path.Combine([AssetsRoot, .. parts]);
        if (!File.Exists(path)) throw new InvalidOperationException("Required copied test asset is missing.");
        return path;
    }
    internal static string AssetDirectory(params string[] parts)
    {
        if (parts.Any(p => Path.IsPathRooted(p) || p.Replace('\\', '/').Split('/').Contains("..")))
            throw new InvalidOperationException("Test asset paths must remain relative to copied assets.");
        var path = Path.Combine([AssetsRoot, .. parts]);
        if (!Directory.Exists(path) || !Directory.EnumerateFiles(path, "*.json").Any())
            throw new InvalidOperationException("Required copied test asset directory is missing or empty.");
        return path;
    }
}
