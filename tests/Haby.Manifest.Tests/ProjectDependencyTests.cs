using System.Xml.Linq;
using Xunit;

namespace Haby.Manifest.Tests;

public sealed class ProjectDependencyTests
{
    [Fact]
    public void Manifest_references_only_Domain_and_the_schema_validator()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "Haby.Manifest", "Haby.Manifest.csproj");
        var project = XDocument.Load(path);
        var references = project.Descendants("ProjectReference")
            .Select(element => ((string)element.Attribute("Include")!).Replace('\\', '/').Split('/')[^1])
            .ToArray();
        Assert.Equal(["Haby.Domain.csproj"], references);
        var packages = project.Descendants("PackageReference")
            .Select(element => (string)element.Attribute("Include")!)
            .ToArray();
        Assert.Equal(["Corvus.Text.Json.Validator"], packages);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HamstixHaby.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find HamstixHaby.slnx above the test output directory.");
    }
}
