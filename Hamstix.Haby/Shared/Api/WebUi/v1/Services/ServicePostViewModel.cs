using Monq.Models.Abstractions;
using System.ComponentModel.DataAnnotations;

namespace Hamstix.Haby.Shared.Api.WebUi.v1.Services
{
    public class ServicePostViewModel
    {
        [Required]
        public string Name { get; set; } = default!;

        [Required]
        public string JsonConfig { get; set; } = default!;

        /// <summary>
        /// The template, that will be generated as service configuration for the microservice.
        /// </summary>
        public string? Template { get; set; }

        /// <summary>
        /// The Plugin to be used as default configurator for the service.
        /// If Null, no plugins is used for the service.
        /// </summary>
        public string? PluginName { get; set; }
    }
}
