namespace Hamstix.Haby.Client.Models
{
    public record BreadCrumbItem(string Name, bool IsActive, string? Uri = default);
}
