using AutoMapper;
using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits;
using Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits.Variables;

namespace Hamstix.Haby.Server.Configuration.MapperProfiles.WebUi.v1
{
#pragma warning disable CS1591
    public class ConfigurationUnitsProfile : Profile
    {
        public ConfigurationUnitsProfile()
        {
            CreateMap<ConfigurationUnit, ConfigurationUnitPreviewViewModel>();
            CreateMap<ConfigurationUnit, ConfigurationUnitViewModel>();

            CreateMap<Variable, ConfigurationUnitVariableViewModel>()
                .ForMember(dst => dst.Service, opt => opt.MapFrom(src => src.ConfigurationUnitAtService.Service));
            CreateMap<Service, ServiceInConfigurationUnitViewModel>();

            CreateMap<ConfigurationUnitKeyResult, ConfigurationUnitKeyResultViewModel>();
            CreateMap<Shared.PluginsCore.ConfigurationResult, ServiceConfigurationResultViewModel>();
            CreateMap<Shared.PluginsCore.Service, ServiceInConfigurationUnitViewModel>();
        }
    }
}
