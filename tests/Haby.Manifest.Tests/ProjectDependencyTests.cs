using System.Xml.Linq;
using Xunit;

namespace Haby.Manifest.Tests;

public sealed class ProjectDependencyTests
{
    [Fact]
    public void Manifest_references_only_Domain_and_has_no_packages()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Haby.Manifest/Haby.Manifest.csproj"));
        var project = XDocument.Load(path);
        var references = project.Descendants("ProjectReference")
            .Select(element => ((string)element.Attribute("Include")!).Replace('\\', '/').Split('/')[^1])
            .ToArray();
        Assert.Equal(["Haby.Domain.csproj"], references);
        Assert.Empty(project.Descendants("PackageReference"));
    }
}
