namespace Haby.Domain;

/// <summary>The Haby-issued identity of one Secret.</summary>
public sealed record SecretId
{
    private readonly Guid _value;
    private SecretId(Guid value) => _value = value;
    public static SecretId New() => new(Guid.CreateVersion7());
    public static SecretId Parse(string value) => new(UuidV7.Parse(value));
    public override string ToString() => _value.ToString("D");
}
