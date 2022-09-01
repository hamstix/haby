using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Grpc.Services;
using Mapster;

namespace Hamstix.Haby.Server.Configuration.MapperProfiles.WebUi.v1;

#pragma warning disable CS1591
public class ServicesProfile : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Service, ServiceModel>()
            .Map(dest => dest.Plugin, src => src.PluginName != null ?
                new PluginInServiceModel { Name = src.PluginName } : null);
    }
}
