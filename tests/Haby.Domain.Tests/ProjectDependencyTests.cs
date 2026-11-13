using System.Xml.Linq;
using Xunit;

namespace Haby.Domain.Tests;

public sealed class ProjectDependencyTests
{
    [Fact]
    public void Domain_has_no_project_or_package_dependencies()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "Haby.Domain", "Haby.Domain.csproj");
        var project = XDocument.Load(path);
        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("PackageReference"));
    }

    private static string FindRepositoryRoot() => Haby.Tests.TestPaths.SourceRoot;
}
