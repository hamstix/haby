using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits;
using Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits.Variables;
using Mapster;

namespace Hamstix.Haby.Server.Configuration.MapperProfiles.WebUi.v1;

#pragma warning disable CS1591
public class ConfigurationUnitsProfile : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<ConfigurationUnit, ConfigurationUnitPreviewViewModel>();
        config.NewConfig<ConfigurationUnit, ConfigurationUnitViewModel>();

        config.NewConfig<Variable, ConfigurationUnitVariableViewModel>()
            .Map(dst => dst.Service, src => src.ConfigurationUnitAtService.Service);
        config.NewConfig<Service, ServiceInConfigurationUnitViewModel>();

        config.NewConfig<ConfigurationUnitKeyResult, ConfigurationUnitKeyResultViewModel>();
        config.NewConfig<Shared.PluginsCore.ConfigurationResult, ServiceConfigurationResultViewModel>();
        config.NewConfig<Shared.PluginsCore.Service, ServiceInConfigurationUnitViewModel>();
    }
}
