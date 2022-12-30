namespace Hamstix.Haby.Server.Models;

public class Generator
{
    public long Id { get; private set; }
    public string Name { get; set; }
    public string? Description { get; set; }
    public string Template { get; set; }

    public Generator(string name, string template)
    {
        Name = name;
        Template = template;
    }
}
