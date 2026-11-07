using Haby.Application;
using Haby.Domain;

namespace Haby.Tests;

internal static class ConfigurationRenderingSamples
{
    internal static readonly ConfigurationRendererId RendererId = ConfigurationRendererId.Create("sample.connection/v1");
    internal static readonly ConfigurationFormatterId FormatterId = ConfigurationFormatterId.Create("sample.string/v1");
    internal static readonly ManifestLocalId Implementation = ManifestLocalId.Create("sample-v1");
    internal sealed record Definition(ResourceTypeId ResourceType, ConfigurationObject DefaultOptions, ConfigurationRendererId? ExactRenderer = null) : ConfigurationRendererContract(
        ExactRenderer ?? RendererId, ResourceType, "sample.output/v1", "value", OperationId.Create("create"),
        new(new(ResourceType, ProviderKindId.Create("sample/v1"), ProviderConfigurationContractId.Create("sample.configuration/v1"),
            ProviderImplementationId.Create("sample/v1")), OperationContractId.Create("sample.create/v1")), FormatterId)
    {
        public override ConfigurationObject Defaults => DefaultOptions;
        public override bool ValidateOptions(ConfigurationObject options, bool complete) => ConfigurationJson.Serialize(options).IsSuccess;
        public override ConfigurationObject NormalizeOptions(ConfigurationObject options) => options;
    }
    internal sealed class Renderer(ConfigurationRendererId? exact = null) : IConfigurationRenderer
    {
        public ConfigurationRendererId Contract => exact ?? RendererId;
        public ManifestLocalId Identity => Implementation;
        public ConfigurationFragment? Render(ExternalOperationOutput output, ConfigurationObject settings) =>
            new(new ConfigurationDeferredValue(FormatterId, settings));
    }
    internal sealed record FormatterDefinition() : ConfigurationFormatterContract(FormatterId)
    {
        public override bool ValidateArguments(ConfigurationObject arguments) => true;
    }
    internal sealed class Formatter : IConfigurationFormatter
    {
        public ConfigurationFormatterId Contract => FormatterId;
        public ManifestLocalId Identity => Implementation;
        public ConfidentialConfigurationString? Format(ConfigurationObject arguments, ReadOnlySpan<char> material) => new(material.ToString());
    }
    internal static ConfigurationRenderingCatalog Catalog(string resource = "sample.resource/v1", ConfigurationObject? defaults = null, bool events = false) =>
        new(events ? [new Definition(ResourceTypeId.Create(resource), defaults ?? new([])),
                new Definition(ResourceTypeId.Create(resource), new([]), ConfigurationRendererId.Create("sample.events/v1"))]
            : [new Definition(ResourceTypeId.Create(resource), defaults ?? new([]))],
            events ? [new Renderer(), new Renderer(ConfigurationRendererId.Create("sample.events/v1"))] : [new Renderer()],
            new([new FormatterDefinition()], [new(Implementation, FormatterId)]), [new Formatter()]);
}
