using System.ComponentModel.DataAnnotations;

namespace Hamstix.Haby.Shared.Api.WebUi.v1.Generators
{
    public class GeneratorPostViewModel
    {
        [Required]
        public string Name { get; set; } = default!;

        public string? Description { get; set; }

        [Required]
        public string Template { get; set; } = default!;
    }
}
