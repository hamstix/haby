using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Api.WebUi.v1.Services;
using Mapster;

namespace Hamstix.Haby.Server.Configuration.MapperProfiles.WebUi.v1;

#pragma warning disable CS1591
public class ServicesProfile : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Service, ServicePreviewViewModel>()
            .Map(dest => dest.Plugin, src => src.PluginName != null ?
                new PluginInServiceViewModel { Name = src.PluginName } : null);
        config.NewConfig<Service, ServiceViewModel>()
            .Map(dest => dest.Plugin, src => src.PluginName != null ?
                new PluginInServiceViewModel { Name = src.PluginName } : null);
    }
}
