using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Server.Services;
using Hamstix.Haby.Shared.PluginsCore;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Configurator
{
    public class ServiceConfigurator : IServiceConfigurator
    {
        readonly IPluginsService _pluginsService;
        readonly IServiceProvider _serviceProvider;

        public ServiceConfigurator(IPluginsService pluginsService, IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _pluginsService = pluginsService;
        }

        public async Task<ConfigurationResult> Configure(Models.Service service, JsonNode renderedTemplate)
        {
            var pluginsService = new Service(service.Id, service.Name, service.JsonConfig.CloneJsonNode().AsObject())
            {
                Template = service.Template
            };

            var result = new ConfigurationResult(pluginsService);
            if (service.PluginName is null)
                return result;

            var plugin = _pluginsService.GetPluginByName(service.PluginName);
            if (plugin is null)
                return result;

            Type strategyType = plugin.StrategyType;
            var strategy = _serviceProvider.GetService(strategyType) as IStrategy;
            if (strategy is null)
                throw new NotSupportedException($"The plugin {plugin.Name} strategy implementation " +
                    $"for the service {service.Name} is not found");

            try
            {
                await strategy.Configure(pluginsService, renderedTemplate, new CancellationTokenSource().Token);
                result.Status = ConfigurationResultStatuses.Ok;
            }
            catch (Exception e)
            {
                result.ErrorMessage = e.Message;
                result.Status = ConfigurationResultStatuses.Failed;
            }
            return result;
        }
    }
}
