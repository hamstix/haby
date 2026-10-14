namespace Haby.Domain;

/// <summary>The persisted identity of a Manifest revision.</summary>
public sealed record ManifestRevisionId
{
    private readonly Guid _value;

    private ManifestRevisionId(Guid value) => _value = value;

    public static ManifestRevisionId New() => new(Guid.CreateVersion7());

    public static ManifestRevisionId Parse(string value) => new(UuidV7.Parse(value));

    public override string ToString() => _value.ToString("D");
}
