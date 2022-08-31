using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Grpc.Generators;
using Mapster;

namespace Hamstix.Haby.Server.Configuration.MapperProfiles.WebUi.v1;

#pragma warning disable CS1591
public class GeneratorsProfile : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Generator, GeneratorModel>();
    }
}
