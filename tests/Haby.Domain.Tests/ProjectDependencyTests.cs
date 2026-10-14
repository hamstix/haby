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
