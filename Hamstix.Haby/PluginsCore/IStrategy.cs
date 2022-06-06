using System.Text.Json.Nodes;

namespace Hamstix.Haby.Shared.PluginsCore
{
    /// <summary>
    /// The service configuration strategy.
    /// </summary>
    public interface IStrategy
    {
        Task Configure(Service service, 
            JsonNode renderedTemplate, 
            JsonObject variables, 
            CancellationToken cancellationToken);

        Task UnConfigure(Service service, 
            JsonNode renderedTemplate, 
            JsonObject variables, 
            CancellationToken cancellationToken);
    }
}
