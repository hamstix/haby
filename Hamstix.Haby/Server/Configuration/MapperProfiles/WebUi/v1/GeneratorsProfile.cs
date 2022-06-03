using AutoMapper;
using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Api.WebUi.v1.Generators;

namespace Hamstix.Haby.Server.Configuration.MapperProfiles.WebUi.v1
{
#pragma warning disable CS1591
    public class GeneratorsProfile : Profile
    {
        public GeneratorsProfile()
        {
            CreateMap<Generator, GeneratorPreviewViewModel>();
            CreateMap<Generator, GeneratorViewModel>();
        }
    }
}
