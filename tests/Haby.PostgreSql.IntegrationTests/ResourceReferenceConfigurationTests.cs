using Haby.Application;
using Haby.Domain;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class ResourceReferenceKernelTests
{
    [Fact]
    public async Task Reference_with_independent_Component_documents_uses_the_ordinary_frozen_plan()
    {
        var connection = await CreateDatabaseAsync("reference_configuration_");
        var boundary = new Boundary();
        var item = await PrepareAsync(connection, boundary, mixed: false, components: """
            {"api":{"documents":{"settings":{"fileName":"appsettings.json","format":"json","template":{"Receiver":"api"}}}},
             "worker":{"documents":{"settings":{"fileName":"appsettings.json","format":"json","template":{"Receiver":"worker"}}}}}
            """);
        Assert.Equal(2, item.Plan.Inputs.Configuration!.Layouts.Count);
        Assert.All(item.Plan.Inputs.Configuration.Layouts, layout => Assert.Single(layout.Documents));
        Assert.All(item.Plan.Graph.Nodes, node => Assert.Equal(ResourceAcquisitionMode.Reference, node.Action.Admission.Mode));
        Assert.Equal(0, boundary.HandlerCalls);
        Assert.Equal(0, boundary.Observations);
    }
}
