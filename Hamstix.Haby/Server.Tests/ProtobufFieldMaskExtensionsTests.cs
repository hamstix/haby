using Google.Protobuf.WellKnownTypes;
using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Shared.Grpc.Services;
using Xunit;

namespace Hamstix.Haby.Server.Tests;

public class ProtobufFieldMaskExtensionsTests
{
    [Fact]
    public void ApplyTo_ProjectsSelectedTopLevelFields()
    {
        var source = new ServiceModel
        {
            Id = 42,
            Name = "database",
            Plugin = new PluginInServiceModel { Name = "postgresql" },
            Template = "template"
        };
        var mask = new FieldMask { Paths = { "id", "name" } };

        var result = mask.ApplyTo(source);

        Assert.Equal(42, result.Id);
        Assert.Equal("database", result.Name);
        Assert.Null(result.Plugin);
        Assert.Null(result.Template);
    }

    [Fact]
    public void ApplyTo_ProjectsNestedFieldsWithoutMutatingSource()
    {
        var source = new ServiceModel
        {
            Id = 42,
            Plugin = new PluginInServiceModel { Name = "postgresql" }
        };
        var mask = new FieldMask { Paths = { "plugin.name" } };

        var result = mask.ApplyTo(source);

        Assert.Equal(0, result.Id);
        Assert.Equal("postgresql", result.Plugin.Name);
        Assert.Equal(42, source.Id);
    }

    [Fact]
    public void ApplyTo_EmptyMaskClearsAllFields()
    {
        var source = new ServiceModel { Id = 42, Name = "database" };

        var result = new FieldMask().ApplyTo(source);

        Assert.Equal(0, result.Id);
        Assert.Equal(string.Empty, result.Name);
    }
}
