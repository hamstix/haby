using System.Xml.Linq;
using Xunit;

namespace Haby.Manifest.Tests;

public sealed class ProjectDependencyTests
{
    [Fact]
    public void Manifest_references_only_Domain_and_has_no_packages()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "Haby.Manifest", "Haby.Manifest.csproj");
        var project = XDocument.Load(path);
        var references = project.Descendants("ProjectReference")
            .Select(element => ((string)element.Attribute("Include")!).Replace('\\', '/').Split('/')[^1])
            .ToArray();
        Assert.Equal(["Haby.Domain.csproj"], references);
        Assert.Empty(project.Descendants("PackageReference"));
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
