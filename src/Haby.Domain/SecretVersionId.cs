namespace Haby.Domain;

/// <summary>The Haby-issued identity of one immutable Secret version.</summary>
public sealed record SecretVersionId
{
    private readonly Guid _value;
    private SecretVersionId(Guid value) => _value = value;
    public static SecretVersionId New() => new(Guid.CreateVersion7());
    public static SecretVersionId Parse(string value) => new(UuidV7.Parse(value));
    public override string ToString() => _value.ToString("D");
}
