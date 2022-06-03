using AutoMapper;
using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Api.WebUi.v1.Services;

namespace Hamstix.Haby.Server.Configuration.MapperProfiles.WebUi.v1
{
#pragma warning disable CS1591
    public class ServicesProfile : Profile
    {
        public ServicesProfile()
        {
            CreateMap<Service, ServicePreviewViewModel>()
                .ForMember(dest => dest.Plugin, opt => opt.MapFrom(src => src.PluginName != null ? new PluginInServiceViewModel { Name = src.PluginName }: null));
            CreateMap<Service, ServiceViewModel>()
                .ForMember(dest => dest.Plugin, opt => opt.MapFrom(src => src.PluginName != null ? new PluginInServiceViewModel { Name = src.PluginName } : null));
        }
    }
}
