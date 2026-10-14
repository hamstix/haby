namespace Haby.Domain;

internal static class UuidV7
{
    internal static Guid Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal) ||
            value[14] != '7' || value[19] is not ('8' or '9' or 'a' or 'b'))
            throw new ArgumentException("Value must be a canonical lowercase UUIDv7.", nameof(value));

        return parsed;
    }
}
