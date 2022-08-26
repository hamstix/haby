using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Api.WebUi.v1.Keys;
using Mapster;

namespace Hamstix.Haby.Server.Configuration.MapperProfiles.WebUi.v1;

#pragma warning disable CS1591
public class ConfigurationKeysProfile : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<ConfigurationKey, ConfigurationKeyPreviewViewModel>();
    }
}
