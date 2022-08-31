using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Grpc.Configuration;
using Mapster;

namespace Hamstix.Haby.Server.Configuration.MapperProfiles.WebUi.v1;

#pragma warning disable CS1591
public class ConfigurationKeysProfile : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<ConfigurationKey, KeyModel>();
    }
}
