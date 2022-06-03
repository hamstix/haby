using System.ComponentModel.DataAnnotations;

namespace Hamstix.Haby.Shared.Api.WebUi.v1.RegistryStatuses
{
    public class RegStatusPutViewModel
    {
        [Required]
        public RegStatuses Status { get; set; }
    }
}
