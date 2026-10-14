namespace Haby.Domain;

/// <summary>The persisted identity of an Application aggregate.</summary>
public sealed record ApplicationRecordId
{
    private readonly Guid _value;

    private ApplicationRecordId(Guid value) => _value = value;

    public static ApplicationRecordId New() => new(Guid.CreateVersion7());

    public static ApplicationRecordId Parse(string value) => new(UuidV7.Parse(value));

    public override string ToString() => _value.ToString("D");
}
