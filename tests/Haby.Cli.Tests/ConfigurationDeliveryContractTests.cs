using Haby.Configuration.Client;
using Haby.Configuration.Contracts;
using Haby.Operator.Contracts;
using System.Xml.Linq;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class ConfigurationDeliveryContractTests
{
    [Fact]
    public void Delivery_descriptor_and_assembly_are_independent_from_operator_contracts()
    {
        var service = ConfigurationDelivery.Descriptor;
        Assert.Equal("haby.configuration.v1alpha1.ConfigurationDelivery", service.FullName);
        Assert.Equal("configuration_delivery.proto", service.File.Name);
        Assert.Empty(service.File.Dependencies);
        Assert.Single(service.File.Services);
        Assert.Equal(new[] { "Deliver", "DeliverRuntime" }, service.Methods.Select(x => x.Name));
        var method = service.Methods[0];
        Assert.Equal("Deliver", method.Name);
        Assert.Same(service.File, method.InputType.File);
        Assert.Same(service.File, method.OutputType.File);
        var runtime = service.Methods[1];
        Assert.Equal(new[] { "binding_id", "revision_id" }, runtime.InputType.Fields.InFieldNumberOrder().Select(x => x.Name));
        Assert.Same(method.OutputType, runtime.OutputType);
        Assert.Same(service.File, runtime.InputType.File);
        Assert.Equal("Haby.Configuration.Contracts", service.File.GetOptions().CsharpNamespace);
        Assert.NotEqual(typeof(ManifestOperator).Assembly, typeof(DeliverConfigurationRequest).Assembly);
        Assert.DoesNotContain(typeof(DeliverConfigurationRequest).Assembly.GetReferencedAssemblies(), x => x.Name!.StartsWith("Haby.", StringComparison.Ordinal));
        Assert.DoesNotContain(ManifestOperator.Descriptor.File.Services, x => x.Name == "ConfigurationDelivery");
        Assert.DoesNotContain(ManifestOperator.Descriptor.File.Dependencies, x => x.Name == "configuration_delivery.proto");
        Assert.DoesNotContain(ManifestOperator.Descriptor.Methods, x => x.Name == "Deliver");
        Assert.Contains(ManifestOperator.Descriptor.Methods, x => x.Name == "CreateConfigurationAccess");
        Assert.Contains(ManifestOperator.Descriptor.Methods, x => x.Name == "GetConfigurationAccess");
        Assert.Contains(ManifestOperator.Descriptor.Methods, x => x.Name == "RevokeConfigurationAccess");
    }

    [Fact]
    public void Runtime_example_does_not_reference_operator_contracts_or_server_layers()
    {
        var dependencies = typeof(RuntimeConfigurationClient).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(dependencies, x => x.Name!.StartsWith("Haby.", StringComparison.Ordinal) &&
            x.Name is not ("Haby.Configuration.Contracts" or "Haby.Configuration.Client"));
    }

    [Fact]
    public void Runtime_transport_client_depends_only_on_delivery_contracts_and_grpc_runtime()
    {
        var dependencies = typeof(ExactConfigurationClient).Assembly.GetReferencedAssemblies();
        Assert.Contains(dependencies, x => x.Name == "Haby.Configuration.Contracts");
        Assert.All(dependencies, dependency => Assert.True(dependency.Name is { } name &&
            (name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Grpc.", StringComparison.Ordinal) ||
             name is "Haby.Configuration.Contracts" or "Google.Protobuf"), "Unexpected dependency in the runtime transport client."));
    }

    [Fact]
    public void Delivery_projects_exclude_even_unused_operator_and_server_project_references()
    {
        var root = Haby.Tests.TestPaths.SourceRoot;
        var contracts = XDocument.Load(Path.Combine(root, "src", "Haby.Configuration.Contracts", "Haby.Configuration.Contracts.csproj"));
        Assert.Empty(contracts.Descendants("ProjectReference"));
        Assert.Empty(contracts.Descendants("FrameworkReference"));
        var client = XDocument.Load(Path.Combine(root, "src", "Haby.Configuration.Client", "Haby.Configuration.Client.csproj"));
        var reference = Assert.Single(client.Descendants("ProjectReference"));
        Assert.Equal("Haby.Configuration.Contracts.csproj", Path.GetFileName((string?)reference.Attribute("Include")));
        Assert.Empty(client.Descendants("FrameworkReference"));
        Assert.Equal("Grpc.Net.Client", (string?)Assert.Single(client.Descendants("PackageReference")).Attribute("Include"));
        var example = XDocument.Load(Path.Combine(root, "examples", "Haby.Configuration.Client", "Haby.Configuration.Example.csproj"));
        Assert.Equal("Haby.Configuration.Client.csproj", Path.GetFileName((string?)Assert.Single(example.Descendants("ProjectReference")).Attribute("Include")));
    }
}
