using Hamstix.Haby.Shared.PluginsCore;

namespace Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits
{
    public class ServiceConfigurationResultViewModel
    {
        public ServiceInConfigurationUnitViewModel Service { get; set; } = default!;
        public ConfigurationResultStatuses Status { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
