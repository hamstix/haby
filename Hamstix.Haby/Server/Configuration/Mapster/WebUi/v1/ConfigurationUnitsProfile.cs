using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Grpc.ConfigurationUnits;
using Mapster;

namespace Hamstix.Haby.Server.Configuration.MapperProfiles.WebUi.v1;

#pragma warning disable CS1591
public class ConfigurationUnitsProfile : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<ConfigurationUnit, ConfigurationUnitModel>();

        config.NewConfig<Variable, ConfigurationUnitVariableModel>()
            .Map(dst => dst.Service, src => src.ConfigurationUnitAtService.Service);
        config.NewConfig<Service, ServiceInCUModel>();

        config.NewConfig<ConfigurationUnitKeyResult, ReconfigurationKeyResultModel>();
        config.NewConfig<Shared.PluginsCore.ConfigurationResult, ReconfigurationResultModel>();
        config.NewConfig<Shared.PluginsCore.Service, ServiceInCUModel>();
    }
}
