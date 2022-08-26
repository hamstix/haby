using System.ComponentModel.DataAnnotations;

namespace Hamstix.Haby.Shared
{
    public class AclModel
    {
        [Required]
        public string Token { get; set; }
    }
}
