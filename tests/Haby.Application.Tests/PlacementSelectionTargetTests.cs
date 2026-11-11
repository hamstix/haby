using Haby.Application;
using Xunit;

namespace Haby.Application.Tests;

public sealed class PlacementSelectionTargetTests
{
    [Theory]
    [InlineData("environment", "", "", "")]
    [InlineData("named", "production", "", "")]
    [InlineData("application", "", "example.app", "")]
    [InlineData("component", "", "example.app", "api")]
    public void Four_closed_targets_have_logical_identity(string kind, string selector, string application, string component)
    {
        var first = PlacementSelectionTarget.Create(kind, selector, application, component);
        Assert.Equal(first, PlacementSelectionTarget.Create(kind, selector, application, component));
        Assert.Equal(kind, first.Kind); Assert.Equal(selector, first.Selector); Assert.Equal(application, first.Application); Assert.Equal(component, first.Component);
    }
    [Theory]
    [InlineData("", "", "", "")]
    [InlineData("other", "", "", "")]
    [InlineData("environment", "name", "", "")]
    [InlineData("environment", "", "app", "")]
    [InlineData("named", "", "", "")]
    [InlineData("named", "name", "app", "")]
    [InlineData("named", "name", "", "api")]
    [InlineData("application", "", "", "")]
    [InlineData("application", "name", "app", "")]
    [InlineData("application", "", "app", "api")]
    [InlineData("component", "", "app", "")]
    [InlineData("component", "", "", "api")]
    [InlineData("component", "name", "app", "api")]
    [InlineData("component", "", "app", "Bad")]
    public void Missing_extra_or_malformed_target_members_refuse(string kind, string selector, string application, string component)
        => Assert.ThrowsAny<ArgumentException>(() => PlacementSelectionTarget.Create(kind, selector, application, component));
}
