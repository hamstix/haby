using Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits.Variables;
using Hamstix.Haby.Shared.PluginsCore;

namespace Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits
{
    public class ConfigurationUnitKeyResultViewModel
    {
        public string Key { get; set; } = default!;
        public IEnumerable<ServiceConfigurationResultViewModel> Results { get; set; } =
            Enumerable.Empty<ServiceConfigurationResultViewModel>();
    }
}
