using System.Xml.Linq;
using Xunit;

namespace Haby.Application.Tests;

public sealed class ProjectDependencyTests
{
    [Fact]
    public void Application_references_only_Domain_and_Manifest_and_has_no_packages()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "Haby.Application", "Haby.Application.csproj");
        var project = XDocument.Load(path);
        var references = project.Descendants("ProjectReference")
            .Select(element => ((string)element.Attribute("Include")!).Replace('\\', '/').Split('/')[^1])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["Haby.Domain.csproj", "Haby.Manifest.csproj"], references);
        Assert.Empty(project.Descendants("PackageReference"));
    }

    private static string FindRepositoryRoot() => Haby.Tests.TestPaths.SourceRoot;
}
