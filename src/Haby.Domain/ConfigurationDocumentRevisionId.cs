namespace Haby.Domain;

public sealed record ConfigurationDocumentRevisionId
{
    private readonly Guid _value;
    private ConfigurationDocumentRevisionId(Guid value) => _value = value;
    public static ConfigurationDocumentRevisionId New() => new(Guid.CreateVersion7());
    public static ConfigurationDocumentRevisionId Parse(string value) => new(UuidV7.Parse(value));
    public override string ToString() => _value.ToString("D");
}
