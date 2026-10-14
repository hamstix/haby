namespace Haby.Domain;

/// <summary>A positive revision sequence number local to one Application.</summary>
public sealed record ManifestRevisionNumber
{
    private ManifestRevisionNumber(long value) => Value = value;

    public long Value { get; }

    public static ManifestRevisionNumber Create(long value)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Revision number must be positive.");

        return new ManifestRevisionNumber(value);
    }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
