namespace Hamstix.Haby.Shared.Api.WebUi.v1.Generators
{
    public class GeneratorViewModel
    {
        public long Id { get; set; }
        public string Name { get; set; } = default!;
        public string? Description { get; set; }
        public string Template { get; set; } = default!;
    }
}
