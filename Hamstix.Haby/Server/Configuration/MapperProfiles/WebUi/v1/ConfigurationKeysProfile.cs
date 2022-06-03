using AutoMapper;
using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Api.WebUi.v1.Keys;

namespace Hamstix.Haby.Server.Configuration.MapperProfiles.WebUi.v1
{
#pragma warning disable CS1591
    public class ConfigurationKeysProfile : Profile
    {
        public ConfigurationKeysProfile()
        {
            CreateMap<ConfigurationKey, ConfigurationKeyPreviewViewModel>();
        }
    }
}
