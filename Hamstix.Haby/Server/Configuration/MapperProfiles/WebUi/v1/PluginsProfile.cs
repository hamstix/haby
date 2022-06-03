using AutoMapper;
using Hamstix.Haby.Shared.Api.WebUi.v1.Plugins;
using Hamstix.Haby.Shared.PluginsCore;

namespace Hamstix.Haby.Server.Configuration.MapperProfiles.WebUi.v1
{
#pragma warning disable CS1591
    public class PluginsProfile : Profile
    {
        public PluginsProfile()
        {
            CreateMap<Plugin, PluginPreviewViewModel>();
        }
    }
}
