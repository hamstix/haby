using System.ComponentModel.DataAnnotations;

namespace Hamstix.Haby.Shared.Api.WebUi.v1.ConfigurationUnits
{
    public class ConfigurationUnitPostViewModel
    {
        /// <summary>
        /// The configuration unit name.
        /// </summary>
        [Required]
        public string Name { get; set; } = default!;

        /// <summary>
        /// The current configuration unit version.
        /// </summary>
        [Required]
        public string Version { get; set; } = default!;

        /// <summary>
        /// The configuration unit template.
        /// </summary>
        [Required]
        public string Template { get; set; } = default!;
    }
}
