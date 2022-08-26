using Hamstix.Haby.Shared.Api.WebUi.v1.Plugins;
using Hamstix.Haby.Shared.PluginsCore;
using Mapster;

namespace Hamstix.Haby.Server.Configuration.MapperProfiles.WebUi.v1;

#pragma warning disable CS1591
public class PluginsProfile : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Plugin, PluginPreviewViewModel>();
    }
}
